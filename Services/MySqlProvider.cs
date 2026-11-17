using MySqlConnector;
using Radiostations.Models;
using System.Data.Common;
using System.Text.RegularExpressions;
using static MySqlConnector.MySqlBackup;

namespace Radiostations.Services;

public class MySqlProvider : IDatabaseProvider, IDatabaseAdmin
{
    private MySqlConnection? _connection;
    private string? _databaseName;
    private string? _connectionString;
    private string? _backupPath;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
    public string ProviderName => "MySQL";

    #region IDatabaseProvider Implementation

    public async Task ConnectAsync(string connectionString)
    {
        _connectionString = connectionString;

        var builder = new MySqlConnectionStringBuilder(connectionString);
        _databaseName = builder.Database;

        ValidateDatabaseName(_databaseName);

        _backupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
        if (!Directory.Exists(_backupPath))
            Directory.CreateDirectory(_backupPath);

        try
        {
            _connection = new MySqlConnection(connectionString);
            await _connection.OpenAsync();
        }
        catch (MySqlException ex) when (ex.Number == 1049)
        {
            builder.Database = null;
            _connection = new MySqlConnection(builder.ConnectionString);
            await _connection.OpenAsync();
        }
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

        bool isConnectedToDatabase = false;
        try
        {
            var testCmd = _connection.CreateCommand();
            testCmd.CommandText = "SELECT DATABASE()";
            var currentDb = await testCmd.ExecuteScalarAsync() as string;
            isConnectedToDatabase = !string.IsNullOrEmpty(currentDb);
        }
        catch { }

        if (!isConnectedToDatabase)
        {
            var createDbCmd = _connection.CreateCommand();
            createDbCmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_databaseName}`";
            await createDbCmd.ExecuteNonQueryAsync();

            var useDbCmd = _connection.CreateCommand();
            useDbCmd.CommandText = $"USE `{_databaseName}`";
            await useDbCmd.ExecuteNonQueryAsync();
        }

        var createTableCmd = _connection.CreateCommand();
        createTableCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ObjectRecords (
                Id INT PRIMARY KEY AUTO_INCREMENT,
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
        if (_connection == null) throw new InvalidOperationException("Not connected");
        if (string.IsNullOrEmpty(_databaseName))
            throw new InvalidOperationException("Database name not specified in connection string");

        var builder = new MySqlConnectionStringBuilder(_connectionString);
        builder.Database = null;
        var serverOnlyConnection = builder.ConnectionString;

        await DisconnectAsync();

        using var tempConnection = new MySqlConnection(serverOnlyConnection);
        await tempConnection.OpenAsync();

        var dropDbCmd = tempConnection.CreateCommand();
        dropDbCmd.CommandText = $"DROP DATABASE IF EXISTS `{_databaseName}`";
        await dropDbCmd.ExecuteNonQueryAsync();

        await ConnectAsync(_connectionString);
    }

    public async Task ClearTableAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ObjectRecords";
            await cmd.ExecuteNonQueryAsync();
            await LogAuditAsync("CLEAR_TABLE", "Table cleared");
        }
        catch (MySqlException ex) when (ex.Number == 1146)
        {
            Console.WriteLine("Table does not exist yet");
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
        catch (MySqlException ex) when (ex.Number == 1146)
        {
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
            ON DUPLICATE KEY UPDATE
                InDoc = VALUES(InDoc),
                ResIndex = VALUES(ResIndex),
                IsGsk2011 = VALUES(IsGsk2011),
                ObjectLocationName = VALUES(ObjectLocationName),
                FiasAoguid = VALUES(FiasAoguid),
                ObjectLocationExtraText = VALUES(ObjectLocationExtraText),
                ObjectName = VALUES(ObjectName),
                EtsId = VALUES(EtsId),
                EtsCode = VALUES(EtsCode),
                EtsName = VALUES(EtsName),
                DocumentID = VALUES(DocumentID)";

        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync();
        await LogAuditAsync("UPSERT", $"Upsert record with ObjectID: {record.ObjectID}");
    }

    #endregion

    #region IDatabaseAdmin Implementation

    public async Task CreateStoredProceduresAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        // 1. Процедура для очистки старых данных (обслуживание)
        var cleanupProc = @"
            DROP PROCEDURE IF EXISTS CleanupOldRecords;
            CREATE PROCEDURE CleanupOldRecords(IN days_to_keep INT)
            BEGIN
                DELETE FROM ObjectRecords 
                WHERE Id IN (
                    SELECT Id FROM (
                        SELECT Id FROM ObjectRecords 
                        ORDER BY Id DESC 
                        LIMIT 1000 OFFSET days_to_keep
                    ) AS tmp
                );
                SELECT ROW_COUNT() AS DeletedCount;
            END;";

        // 2. Процедура для получения статистики
        var statsProc = @"
            DROP PROCEDURE IF EXISTS GetDBStatistics;
            CREATE PROCEDURE GetDBStatistics()
            BEGIN
                SELECT 
                    IFNULL((SELECT COUNT(*) FROM ObjectRecords), 0) AS TotalRecords,
                    IFNULL((SELECT COUNT(DISTINCT ObjectID) FROM ObjectRecords), 0) AS UniqueObjects,
                    IFNULL((SELECT COUNT(DISTINCT DocumentID) FROM ObjectRecords), 0) AS UniqueDocuments,
                    IFNULL((SELECT MIN(Id) FROM ObjectRecords), 0) AS MinId,
                    IFNULL((SELECT MAX(Id) FROM ObjectRecords), 0) AS MaxId;
            END;";

        // 3. Процедура для аудита
        var auditProc = @"
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id INT PRIMARY KEY AUTO_INCREMENT,
                ActionTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                ActionType VARCHAR(50),
                Details TEXT,
                UserName VARCHAR(100)
            );
            
            DROP PROCEDURE IF EXISTS LogAction;
            CREATE PROCEDURE LogAction(IN action_type VARCHAR(50), IN details TEXT)
            BEGIN
                INSERT INTO AuditLog (ActionType, Details, UserName) 
                VALUES (action_type, details, CURRENT_USER());
            END;
            
            DROP PROCEDURE IF EXISTS GetAuditLog;
            CREATE PROCEDURE GetAuditLog(IN days_back INT)
            BEGIN
                SELECT ActionTime, ActionType, Details, UserName
                FROM AuditLog 
                WHERE ActionTime >= DATE_SUB(NOW(), INTERVAL days_back DAY)
                ORDER BY ActionTime DESC;
            END;";

        // 4. Процедура для мониторинга активности пользователей
        var monitorProc = @"
            DROP PROCEDURE IF EXISTS GetUserActivityMonitor;
            CREATE PROCEDURE GetUserActivityMonitor()
            BEGIN
                -- Активные соединения
                SELECT 
                    'Активные соединения' AS Metric,
                    COUNT(*) AS Value,
                    CONCAT('Пользователи: ', GROUP_CONCAT(DISTINCT user SEPARATOR ', ')) AS Details
                FROM information_schema.processlist
                WHERE db = DATABASE() OR db IS NULL;
                
                -- Последние действия пользователей (из AuditLog)
                SELECT 
                    'Последние действия' AS Metric,
                    COUNT(*) AS Value,
                    CONCAT('За последний час: ', COUNT(*), ' действий') AS Details
                FROM AuditLog 
                WHERE ActionTime >= DATE_SUB(NOW(), INTERVAL 1 HOUR);
                
                -- Топ-5 самых активных пользователей
                SELECT 
                    'Топ пользователей' AS Metric,
                    COUNT(*) AS Value,
                    CONCAT('Пользователь: ', UserName, ' (', COUNT(*), ' действий)') AS Details
                FROM AuditLog 
                WHERE ActionTime >= DATE_SUB(NOW(), INTERVAL 7 DAY)
                GROUP BY UserName
                ORDER BY COUNT(*) DESC
                LIMIT 5;
                
                -- Статистика по типам действий
                SELECT 
                    'Статистика действий' AS Metric,
                    COUNT(*) AS Value,
                    CONCAT(ActionType, ': ', COUNT(*), ' раз') AS Details
                FROM AuditLog 
                WHERE ActionTime >= DATE_SUB(NOW(), INTERVAL 7 DAY)
                GROUP BY ActionType;
            END;";

        var commands = new[] { cleanupProc, statsProc, auditProc, monitorProc };

        foreach (var commandText in commands)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = commandText;
            await cmd.ExecuteNonQueryAsync();
        }

        await LogAuditAsync("CREATE_PROCEDURES", "Stored procedures created");
    }

    public async Task<string> CreateBackupAsync()
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"backup_{_databaseName}_{timestamp}.sql";
        var backupFilePath = Path.Combine(_backupPath, backupFileName);

        try
        {
            // Используем существующее открытое соединение
            using (var cmd = new MySqlCommand())
            {
                cmd.Connection = _connection;
                using (var mb = new MySqlBackup(cmd))
                {
                    // Выполняем резервное копирование прямо в файл
                    mb.ExportToFile(backupFilePath);
                }
            }

            await LogAuditAsync("BACKUP", $"Backup created: {backupFileName}");
            return backupFilePath;
        }
        catch (Exception ex)
        {
            await LogAuditAsync("BACKUP_FAILED", ex.Message);
            throw;
        }
    }

    public async Task RestoreBackupAsync(byte[] backupFileContent, string fileName)
    {
        if (_connection == null) throw new InvalidOperationException("Not connected");

        // Сохраняем временный файл
        var tempPath = Path.GetTempFileName();
        File.WriteAllBytes(tempPath, backupFileContent);

        try
        {
            using (var cmd = new MySqlCommand())
            {
                cmd.Connection = _connection;
                using (var mb = new MySqlBackup(cmd))
                {
                    mb.ImportFromFile(tempPath);
                }
            }

            await LogAuditAsync("RESTORE", $"Restored from: {fileName}");
        }
        catch (Exception ex)
        {
            await LogAuditAsync("RESTORE_FAILED", ex.Message);
            throw;
        }
        finally
        {
            // Удаляем временный файл
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async Task<Dictionary<string, object>> GetStatisticsAsync()
    {
        var result = new Dictionary<string, object>();

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "CALL GetDBStatistics()";

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                result["Total Records"] = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                result["Unique Objects"] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                result["Unique Documents"] = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                result["Min ID"] = reader.IsDBNull(3) ? "Нет данных" : reader.GetInt64(3);
                result["Max ID"] = reader.IsDBNull(4) ? "Нет данных" : reader.GetInt64(4);
            }
        }
        catch (Exception ex)
        {
            result["Error"] = ex.Message;
        }

        return result;
    }

    public async Task<List<Dictionary<string, object>>> GetAuditLogAsync(int days)
    {
        var logs = new List<Dictionary<string, object>>();

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "CALL GetAuditLog(@days)";
            cmd.Parameters.AddWithValue("@days", days);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var log = new Dictionary<string, object>
                {
                    ["Time"] = reader.GetDateTime(0),
                    ["Action"] = reader.GetString(1),
                    ["Details"] = reader.GetString(2),
                    ["User"] = reader.GetString(3)
                };
                logs.Add(log);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audit log error: {ex.Message}");
        }

        return logs;
    }

    public async Task<Dictionary<string, object>> GetUserActivityMonitorAsync()
    {
        var result = new Dictionary<string, object>();

        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "CALL GetUserActivityMonitor()";

            using var reader = await cmd.ExecuteReaderAsync();
            var metrics = new List<Dictionary<string, object>>();

            // Читаем первый результат - активные соединения
            while (await reader.ReadAsync())
            {
                var metric = new Dictionary<string, object>
                {
                    ["Metric"] = reader.GetString(0),
                    ["Value"] = reader.GetInt64(1),
                    ["Details"] = reader.GetString(2)
                };
                metrics.Add(metric);
            }
            result["Active Connections"] = metrics.Count > 0 ? metrics[0] : new Dictionary<string, object>();

            // Второй результат - последние действия
            await reader.NextResultAsync();
            metrics.Clear();
            while (await reader.ReadAsync())
            {
                var metric = new Dictionary<string, object>
                {
                    ["Metric"] = reader.GetString(0),
                    ["Value"] = reader.GetInt64(1),
                    ["Details"] = reader.GetString(2)
                };
                metrics.Add(metric);
            }
            result["Recent Activity"] = metrics.Count > 0 ? metrics[0] : new Dictionary<string, object>();

            // Третий результат - топ пользователей
            await reader.NextResultAsync();
            var topUsers = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync())
            {
                var user = new Dictionary<string, object>
                {
                    ["Metric"] = reader.GetString(0),
                    ["Actions"] = reader.GetInt64(1),
                    ["Details"] = reader.GetString(2)
                };
                topUsers.Add(user);
            }
            result["Top Users"] = topUsers;

            // Четвёртый результат - статистика действий
            await reader.NextResultAsync();
            var actionStats = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync())
            {
                var stat = new Dictionary<string, object>
                {
                    ["Metric"] = reader.GetString(0),
                    ["Count"] = reader.GetInt64(1),
                    ["Details"] = reader.GetString(2)
                };
                actionStats.Add(stat);
            }
            result["Action Statistics"] = actionStats;
        }
        catch (Exception ex)
        {
            result["Error"] = ex.Message;
        }

        return result;
    }

    #endregion

    #region Private Methods

    private async Task LogAuditAsync(string actionType, string details)
    {
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "CALL LogAction(@action, @details)";
            cmd.Parameters.AddWithValue("@action", actionType);
            cmd.Parameters.AddWithValue("@details", details);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audit log error: {ex.Message}");
        }
    }

    private static void ValidateDatabaseName(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName))
            throw new ArgumentException("Database name cannot be empty");

        if (!Regex.IsMatch(dbName, @"^[a-zA-Z0-9_\-]+$"))
            throw new ArgumentException($"Invalid database name: {dbName}");
    }

    private static void AddParameters(MySqlCommand cmd, ObjectRecord record)
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

    #endregion
}