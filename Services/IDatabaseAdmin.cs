public interface IDatabaseAdmin
{
    Task CreateStoredProceduresAsync();
    Task<string> CreateBackupAsync();
    Task RestoreBackupAsync(byte[] backupFileContent, string fileName); // Изменен параметр
    Task<Dictionary<string, object>> GetStatisticsAsync();
    Task<List<Dictionary<string, object>>> GetAuditLogAsync(int days);
    Task<Dictionary<string, object>> GetUserActivityMonitorAsync();
}