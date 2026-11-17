using Microsoft.Data.SqlClient;
using Radiostations.Models;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace Radiostations.Services;

public class MssqlProvider : IDatabaseProvider
{
    private SqlConnection? _connection;
    private string? _databaseName;
    private string? _connectionString;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
    public string ProviderName => "MSSQL";

    public async Task ConnectAsync(string connectionString)
    {
        _connectionString = connectionString;

        // Извлекаем имя базы данных из строки подключения
        var builder = new SqlConnectionStringBuilder(connectionString);
        _databaseName = builder.InitialCatalog;

        if (string.IsNullOrEmpty(_databaseName))
            throw new ArgumentException("Database name (Initial Catalog) is required in connection string");

        ValidateDatabaseName(_databaseName);

        // Подключаемся к серверу SQL Server (без указания конкретной БД)
        // Убираем Initial Catalog из строки подключения
        var serverOnlyBuilder = new SqlConnectionStringBuilder(connectionString);
        serverOnlyBuilder.Remove("Initial Catalog");
        serverOnlyBuilder.Remove("Database");

        _connection = new SqlConnection(serverOnlyBuilder.ConnectionString);
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
        checkDbCmd.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = @dbName";
        checkDbCmd.Parameters.AddWithValue("@dbName", _databaseName);
        var dbExists = (int)await checkDbCmd.ExecuteScalarAsync() > 0;

        // Создаём базу данных, если она не существует
        if (!dbExists)
        {
            var createDbCmd = _connection.CreateCommand();
            createDbCmd.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await createDbCmd.ExecuteNonQueryAsync();
        }

        // Закрываем текущее соединение (оно без БД)
        await _connection.CloseAsync();
        await _connection.DisposeAsync();

        // Открываем новое соединение уже с БД
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync();

        // Создаём таблицу
        var createTableCmd = _connection.CreateCommand();
        createTableCmd.CommandText = @"
            IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ObjectRecords]') AND type in (N'U'))
            CREATE TABLE ObjectRecords (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                ObjectID BIGINT NOT NULL UNIQUE,
                InDoc INT NOT NULL,
                ResIndex INT NOT NULL,
                IsGsk2011 INT NOT NULL,
                ObjectLocationName NVARCHAR(MAX) NOT NULL,
                FiasAoguid NVARCHAR(100) NOT NULL,
                ObjectLocationExtraText NVARCHAR(MAX) NOT NULL,
                ObjectName NVARCHAR(MAX) NOT NULL,
                EtsId BIGINT NOT NULL,
                EtsCode NVARCHAR(50) NOT NULL,
                EtsName NVARCHAR(MAX) NOT NULL,
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

        // Подключаемся к серверу без БД
        var builder = new SqlConnectionStringBuilder(_connectionString);
        builder.Remove("Initial Catalog");
        builder.Remove("Database");

        using var tempConnection = new SqlConnection(builder.ConnectionString);
        await tempConnection.OpenAsync();

        // Удаляем базу данных
        var dropDbCmd = tempConnection.CreateCommand();
        dropDbCmd.CommandText = $@"
            IF EXISTS (SELECT * FROM sys.databases WHERE name = @dbName)
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END";
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
        catch (SqlException)
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
        catch (SqlException ex) when (ex.Number == 208) // Invalid object name (таблица не существует)
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
            MERGE INTO ObjectRecords AS target
            USING (SELECT @oid AS ObjectID) AS source
            ON target.ObjectID = source.ObjectID
            WHEN MATCHED THEN
                UPDATE SET 
                    InDoc = @inDoc,
                    ResIndex = @resIdx,
                    IsGsk2011 = @isGsk,
                    ObjectLocationName = @locName,
                    FiasAoguid = @fias,
                    ObjectLocationExtraText = @locExtra,
                    ObjectName = @objName,
                    EtsId = @etsId,
                    EtsCode = @etsCode,
                    EtsName = @etsName,
                    DocumentID = @docId
            WHEN NOT MATCHED THEN
                INSERT (ObjectID, InDoc, ResIndex, IsGsk2011, ObjectLocationName, 
                        FiasAoguid, ObjectLocationExtraText, ObjectName, EtsId, EtsCode, EtsName, DocumentID)
                VALUES (@oid, @inDoc, @resIdx, @isGsk, @locName, @fias, @locExtra, @objName, @etsId, @etsCode, @etsName, @docId);";

        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void ValidateDatabaseName(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName))
            throw new ArgumentException("Database name cannot be empty");

        // Для MSSQL допустимы буквы, цифры, подчеркивания, дефисы, но не может начинаться с цифры
        if (!Regex.IsMatch(dbName, @"^[a-zA-Z_][a-zA-Z0-9_\-]*$"))
            throw new ArgumentException($"Invalid database name: {dbName}");
    }

    private static void AddParameters(SqlCommand cmd, ObjectRecord record)
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