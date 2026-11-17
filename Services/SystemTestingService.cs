using System.Diagnostics;
using System.Text;
using Radiostations.Models;

namespace Radiostations.Services;

public class TestResultItem
{
    public string TestType { get; set; } = string.Empty;
    public string TestName { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public long ElapsedMs { get; set; }
    public string Details { get; set; } = string.Empty;
}

public class SystemTestingService
{
    private readonly XmlParserService _xmlParser;

    public SystemTestingService(XmlParserService xmlParser)
    {
        _xmlParser = xmlParser;
    }

    public async Task<List<TestResultItem>> RunAllTestsAsync(IDatabaseProvider provider)
    {
        var results = new List<TestResultItem>
        {
            await RunXmlParserTestAsync(),
            await RunCrudAndUpsertTestAsync(provider),
            await RunLoadBenchmarkTestAsync(provider, 300),
            await RunNegativeConnectionTestAsync()
        };

        return results;
    }

    private Task<TestResultItem> RunXmlParserTestAsync()
    {
        var sw = Stopwatch.StartNew();
        var tempFile = Path.GetTempFileName();
        try
        {
            var sampleXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Root>
  <DocumentID>999888</DocumentID>
  <Object>
    <ObjectID>777001</ObjectID>
    <InDoc>1</InDoc>
    <ResIndex>2</ResIndex>
    <isGsk2011>1</isGsk2011>
    <ObjectLocationName>г. Петрозаводск</ObjectLocationName>
    <FiasAoguid>test-guid-123</FiasAoguid>
    <ObjectLocationExtraText>Тестовая площадка</ObjectLocationExtraText>
    <ObjectName>Базовая станция ТЕСТ-1</ObjectName>
    <EtsId>10</EtsId>
    <EtsCode>GSM-900</EtsCode>
    <EtsName>Стандарт связи</EtsName>
  </Object>
</Root>";
            File.WriteAllText(tempFile, sampleXml, Encoding.UTF8);
            var parsed = _xmlParser.ParseXmlFile(tempFile);
            sw.Stop();

            var firstRecord = parsed.FirstOrDefault();
            bool isValid = parsed.Count == 1
                           && firstRecord != null
                           && firstRecord.ObjectID == 777001
                           && firstRecord.DocumentID == 999888
                           && firstRecord.ObjectName == "Базовая станция ТЕСТ-1";

            return Task.FromResult(new TestResultItem
            {
                TestType = "Модульный (Unit)",
                TestName = "Парсинг XML-документа",
                Passed = isValid,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = isValid
                    ? "XML успешно разобран, все поля (ObjectID, DocumentID, ObjectName) совпали."
                    : "Ошибка несоответствия полей после парсинга."
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(new TestResultItem
            {
                TestType = "Модульный (Unit)",
                TestName = "Парсинг XML-документа",
                Passed = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = $"Исключение: {ex.Message}"
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private async Task<TestResultItem> RunCrudAndUpsertTestAsync(IDatabaseProvider provider)
    {
        var sw = Stopwatch.StartNew();
        const long testObjectId = -999999;

        try
        {
            var initialRecord = new ObjectRecord
            {
                ObjectID = testObjectId,
                InDoc = 1,
                ResIndex = 1,
                IsGsk2011 = 1,
                ObjectLocationName = "Локация 1",
                FiasAoguid = "guid-1",
                ObjectLocationExtraText = "Доп 1",
                ObjectName = "Тест ИС v1",
                EtsId = 1,
                EtsCode = "C1",
                EtsName = "ETS 1",
                DocumentID = 100
            };

            await provider.UpsertAsync(initialRecord);

            initialRecord.ObjectName = "Тест ИС v2 (Обновлено)";
            await provider.UpsertAsync(initialRecord);

            var all = await provider.GetAllAsync();
            var matches = all.Where(x => x.ObjectID == testObjectId).ToList();
            sw.Stop();

            var firstMatch = matches.FirstOrDefault();
            bool passed = matches.Count == 1 && firstMatch?.ObjectName == "Тест ИС v2 (Обновлено)";

            return new TestResultItem
            {
                TestType = "Интеграционный",
                TestName = $"CRUD и Upsert ({provider.ProviderName})",
                Passed = passed,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = passed
                    ? "Запись добавлена и обновлена без создания дубликата."
                    : $"Найдено записей: {matches.Count} (ожидалась 1)."
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResultItem
            {
                TestType = "Интеграционный",
                TestName = $"CRUD и Upsert ({provider.ProviderName})",
                Passed = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = $"Ошибка БД: {ex.Message}"
            };
        }
    }

    private async Task<TestResultItem> RunLoadBenchmarkTestAsync(IDatabaseProvider provider, int recordsCount)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 1; i <= recordsCount; i++)
            {
                await provider.UpsertAsync(new ObjectRecord
                {
                    ObjectID = -100000 - i,
                    InDoc = i,
                    ResIndex = i,
                    IsGsk2011 = 1,
                    ObjectLocationName = $"Тестовый регион #{i}",
                    FiasAoguid = Guid.NewGuid().ToString(),
                    ObjectLocationExtraText = "Нагрузочный тест",
                    ObjectName = $"РЭС-{i}",
                    EtsId = i,
                    EtsCode = "LTE",
                    EtsName = "4G/LTE",
                    DocumentID = 500000 + i
                });
            }

            var writeMs = sw.ElapsedMilliseconds;
            var readStart = Stopwatch.StartNew();
            var list = await provider.GetAllAsync();
            readStart.Stop();
            sw.Stop();

            double rps = writeMs > 0 ? Math.Round(recordsCount / (writeMs / 1000.0), 1) : recordsCount;

            return new TestResultItem
            {
                TestType = "Нагрузочный (Stress)",
                TestName = $"Пакетная запись {recordsCount} записей и чтение",
                Passed = list.Count >= recordsCount,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = $"Запись: {writeMs} мс ({rps} зап/сек), Чтение всех ({list.Count} шт.): {readStart.ElapsedMilliseconds} мс."
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResultItem
            {
                TestType = "Нагрузочный (Stress)",
                TestName = $"Пакетная запись {recordsCount} записей",
                Passed = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = $"Сбой под нагрузкой: {ex.Message}"
            };
        }
    }

    private async Task<TestResultItem> RunNegativeConnectionTestAsync()
    {
        var sw = Stopwatch.StartNew();
        var sqlite = new SQLiteProvider();
        try
        {
            await sqlite.ConnectAsync("Data Source=Z:\\NonExistentDrive_999\\broken.db");
            sw.Stop();
            return new TestResultItem
            {
                TestType = "Негативный",
                TestName = "Обработка неверного пути к БД",
                Passed = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = "Ожидалась ошибка подключения, но соединение прошло."
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResultItem
            {
                TestType = "Негативный",
                TestName = "Обработка неверного пути к БД",
                Passed = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                Details = $"Система штатно перехватила исключение: {ex.GetType().Name}"
            };
        }
        finally
        {
            await sqlite.DisconnectAsync();
        }
    }
}
