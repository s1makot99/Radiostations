using Npgsql;
using Radiostations.Models;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace Radiostations.Services;

public class PostgresProvider : IDatabaseProvider
{
    private NpgsqlConnection? _connection;
    private string? _databaseName;
    private string? _connectionString;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
    public string ProviderName => "PostgreSQL";

    public async Task ConnectAsync(string connectionString)
    {
        _connectionString = connectionString;

        // Извлекаем имя базы данных из строки подключения
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        _databaseName = builder.Database;

        if (string.IsNullOrEmpty(_databaseName))
            throw new ArgumentException("Database name is required in connection string");

        ValidateDatabaseName(_databaseName);

        // Подключаемся к серверу PostgreSQL (без указания конкретной БД)
        var serverOnlyBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        serverOnlyBuilder.Database = "postgres"; // Подключаемся к стандартной БД postgres

        _connection = new NpgsqlConnection(serverOnlyBuilder.ConnectionString);
        await _connection.OpenAsync();
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async Task CreateDatabaseAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");
        if (string.IsNullOrEmpty(_databaseName))
            throw new InvalidOperationException("Database name not specified in connection string");

        // Проверяем, существует ли база данных
        var checkDbCmd = _connection.CreateCommand();
        checkDbCmd.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @dbName";
        checkDbCmd.Parameters.AddWithValue("@dbName", _databaseName);
        var dbExists = (long)await checkDbCmd.ExecuteScalarAsync() > 0;

        // Создаём базу данных, если она не существует
        if (!dbExists)
        {
            var createDbCmd = _connection.CreateCommand();
            createDbCmd.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await createDbCmd.ExecuteNonQueryAsync();
        }

        // Закрываем текущее соединение (оно к БД postgres)
        await _connection.CloseAsync();
        await _connection.DisposeAsync();

        // Открываем новое соединение уже с нашей БД
        _connection = new NpgsqlConnection(_connectionString);
        await _connection.OpenAsync();

        // Создаём таблицу
        var createTableCmd = _connection.CreateCommand();
        createTableCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ObjectRecords (
                Id SERIAL PRIMARY KEY,
                ObjectID BIGINT NOT NULL UNIQUE,
                InDoc INT NOT NULL,
                ResIndex INT NOT NULL,
                IsGsk2011 INT NOT NULL,
                ObjectLocationName TEXT NOT NULL,
                FiasAoguid VARCHAR(100) NOT NULL,
                ObjectLocationExtraText TEXT NOT NULL,
                ObjectName TEXT NOT NULL,
                EtsId BIGINT NOT NULL,
                EtsCode VARCHAR(50) NOT NULL,
                EtsName TEXT NOT NULL,
                DocumentID BIGINT NOT NULL
            )";
        await createTableCmd.ExecuteNonQueryAsync();
    }

    public async Task DropDatabaseAsync()
    {
        if (string.IsNullOrEmpty(_databaseName))
            throw new InvalidOperationException("Database name not specified in connection string");

        // Закрываем текущее соединение если есть
        if (_connection != null)
        {
            await DisconnectAsync();
        }

        // Подключаемся к серверу PostgreSQL без нашей БД
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        builder.Database = "postgres"; // Подключаемся к стандартной БД postgres

        using var tempConnection = new NpgsqlConnection(builder.ConnectionString);
        await tempConnection.OpenAsync();

        // Завершаем все соединения с нашей БД и удаляем её
        var dropDbCmd = tempConnection.CreateCommand();
        dropDbCmd.CommandText = $@"
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = @dbName AND pid <> pg_backend_pid();
            
            DROP DATABASE IF EXISTS ""{_databaseName}""";
        dropDbCmd.Parameters.AddWithValue("@dbName", _databaseName);
        await dropDbCmd.ExecuteNonQueryAsync();

        // Переподключаемся (БД уже не существует)
        await ConnectAsync(_connectionString);
    }

    public async Task ClearTableAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "TRUNCATE TABLE ObjectRecords";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            // Если TRUNCATE не работает, используем DELETE
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ObjectRecords";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<ObjectRecord>> GetAllAsync()
    {
        var list = new List<ObjectRecord>();
        if (_connection == null) return list;

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM ObjectRecords";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(MapToRecord(reader));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // 42P01 = undefined_table
        {
            // Таблицы нет - возвращаем пустой список
            Console.WriteLine("Table does not exist yet");
        }

        return list;
    }

    public async Task UpsertAsync(ObjectRecord record)
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ObjectRecords (ObjectID, InDoc, ResIndex, IsGsk2011, ObjectLocationName, 
                FiasAoguid, ObjectLocationExtraText, ObjectName, EtsId, EtsCode, EtsName, DocumentID)
            VALUES (@oid, @inDoc, @resIdx, @isGsk, @locName, @fias, @locExtra, @objName, @etsId, @etsCode, @etsName, @docId)
            ON CONFLICT (ObjectID) DO UPDATE SET
                InDoc = EXCLUDED.InDoc,
                ResIndex = EXCLUDED.ResIndex,
                IsGsk2011 = EXCLUDED.IsGsk2011,
                ObjectLocationName = EXCLUDED.ObjectLocationName,
                FiasAoguid = EXCLUDED.FiasAoguid,
                ObjectLocationExtraText = EXCLUDED.ObjectLocationExtraText,
                ObjectName = EXCLUDED.ObjectName,
                EtsId = EXCLUDED.EtsId,
                EtsCode = EXCLUDED.EtsCode,
                EtsName = EXCLUDED.EtsName,
                DocumentID = EXCLUDED.DocumentID";

        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void ValidateDatabaseName(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName))
            throw new ArgumentException("Database name cannot be empty");

        // Для PostgreSQL допустимы буквы, цифры, подчеркивания, но не может начинаться с цифры или дефиса
        if (!Regex.IsMatch(dbName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            throw new ArgumentException($"Invalid database name: {dbName}");
    }

    private static void AddParameters(NpgsqlCommand cmd, ObjectRecord record)
    {
        cmd.Parameters.AddWithValue("@oid", record.ObjectID);
        cmd.Parameters.AddWithValue("@inDoc", record.InDoc);
        cmd.Parameters.AddWithValue("@resIdx", record.ResIndex);
        cmd.Parameters.AddWithValue("@isGsk", record.IsGsk2011);
        cmd.Parameters.AddWithValue("@locName", record.ObjectLocationName);
        cmd.Parameters.AddWithValue("@fias", record.FiasAoguid);
        cmd.Parameters.AddWithValue("@locExtra", record.ObjectLocationExtraText);
        cmd.Parameters.AddWithValue("@objName", record.ObjectName);
        cmd.Parameters.AddWithValue("@etsId", record.EtsId);
        cmd.Parameters.AddWithValue("@etsCode", record.EtsCode);
        cmd.Parameters.AddWithValue("@etsName", record.EtsName);
        cmd.Parameters.AddWithValue("@docId", record.DocumentID);
    }

    private static ObjectRecord MapToRecord(DbDataReader reader)
    {
        return new ObjectRecord
        {
            Id = reader.GetInt32(0),
            ObjectID = reader.GetInt64(1),
            InDoc = reader.GetInt32(2),
            ResIndex = reader.GetInt32(3),
            IsGsk2011 = reader.GetInt32(4),
            ObjectLocationName = reader.GetString(5),
            FiasAoguid = reader.GetString(6),
            ObjectLocationExtraText = reader.GetString(7),
            ObjectName = reader.GetString(8),
            EtsId = reader.GetInt64(9),
            EtsCode = reader.GetString(10),
            EtsName = reader.GetString(11),
            DocumentID = reader.GetInt64(12)
        };
    }
}