using Radiostations.Models;

namespace Radiostations.Services;

public interface IDatabaseProvider
{
    Task ConnectAsync(string connectionString);
    Task DisconnectAsync();
    bool IsConnected { get; }
    string ProviderName { get; }
    
    Task CreateDatabaseAsync();      // создаёт БД и таблицу
    Task DropDatabaseAsync();        // удаляет все данные / БД
    Task ClearTableAsync();          // DELETE FROM ObjectRecords
    Task<List<ObjectRecord>> GetAllAsync();
    Task UpsertAsync(ObjectRecord record);
}
