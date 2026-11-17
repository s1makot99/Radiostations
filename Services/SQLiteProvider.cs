using Microsoft.Data.Sqlite;
using Radiostations.Models;
using System.Data.Common;

namespace Radiostations.Services;

public class SQLiteProvider : IDatabaseProvider
{
    private SqliteConnection? _connection;
    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
    public string ProviderName => "SQLite";

    public async Task ConnectAsync(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync();
        await CreateDatabaseAsync();
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
        
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ObjectRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ObjectID INTEGER NOT NULL UNIQUE,
                InDoc INTEGER NOT NULL,
                ResIndex INTEGER NOT NULL,
                IsGsk2011 INTEGER NOT NULL,
                ObjectLocationName TEXT NOT NULL,
                FiasAoguid TEXT NOT NULL,
                ObjectLocationExtraText TEXT NOT NULL,
                ObjectName TEXT NOT NULL,
                EtsId INTEGER NOT NULL,
                EtsCode TEXT NOT NULL,
                EtsName TEXT NOT NULL,
                DocumentID INTEGER NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DropDatabaseAsync()
    {
        await ClearTableAsync();
    }

    public async Task ClearTableAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");
        
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ObjectRecords";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ObjectRecord>> GetAllAsync()
    {
        var list = new List<ObjectRecord>();
        if (_connection == null) return list;
        
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ObjectRecords";
        
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapToRecord(reader));
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
            ON CONFLICT(ObjectID) DO UPDATE SET
                InDoc = excluded.InDoc,
                ResIndex = excluded.ResIndex,
                IsGsk2011 = excluded.IsGsk2011,
                ObjectLocationName = excluded.ObjectLocationName,
                FiasAoguid = excluded.FiasAoguid,
                ObjectLocationExtraText = excluded.ObjectLocationExtraText,
                ObjectName = excluded.ObjectName,
                EtsId = excluded.EtsId,
                EtsCode = excluded.EtsCode,
                EtsName = excluded.EtsName,
                DocumentID = excluded.DocumentID";
        
        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParameters(SqliteCommand cmd, ObjectRecord record)
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
