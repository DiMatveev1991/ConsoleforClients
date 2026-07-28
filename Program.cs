using System.Text;
using ConsoleForClients;
using ConsoleForClients.Services;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = Encoding.UTF8;

// ── Конфигурация ────────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var connectionString = config.GetConnectionString("Express")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Express.");

var serviceOptions = config.GetSection("ContragentService").Get<ContragentServiceOptions>()
    ?? new ContragentServiceOptions();
var enrichmentOptions = config.GetSection("Enrichment").Get<EnrichmentOptions>()
    ?? new EnrichmentOptions();
var logFile = config.GetValue<string>("LogFile") ?? "enrichment-log.txt";

// ── Инфраструктура ──────────────────────────────────────────────────────────
using var http = new HttpClient
{
    BaseAddress = new Uri(serviceOptions.BaseUrl),
    Timeout = TimeSpan.FromSeconds(serviceOptions.TimeoutSeconds)
};

var api = new ContragentApiClient(http, serviceOptions);
var repo = new ClientRepository(connectionString, enrichmentOptions);
var stats = new Statistics();

// Маппинг типа контрагента берём из справочника БД (Types_ContragentTypes),
// конфиг appsettings — запасной вариант, если таблица недоступна.
try
{
    var dbMap = await repo.GetContragentTypeMapAsync(CancellationToken.None);
    if (dbMap.Count > 0)
    {
        enrichmentOptions.ContragentTypeMap = dbMap;
        Console.WriteLine($"Справочник типов контрагента из БД: " +
            string.Join(", ", dbMap.Select(kv => $"{kv.Key}={kv.Value}")));
    }
    else
    {
        Console.WriteLine("Справочник Types_ContragentTypes пуст — используется маппинг из appsettings.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Не удалось прочитать Types_ContragentTypes ({ex.Message}) — используется маппинг из appsettings.");
}

var enricher = new EnrichmentService(api, enrichmentOptions);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nОстановка по запросу пользователя…");
};

var logLock = new object();
await using var log = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };
void Log(string line)
{
    lock (logLock) log.WriteLine(line);
}

// ── Загрузка клиентов ───────────────────────────────────────────────────────
Console.WriteLine("Загрузка московских клиентов КО с ИНН…");
List<ConsoleForClients.Models.ClientPayer> clients;
try
{
    clients = await repo.GetClientsToEnrichAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка чтения из БД: {ex.Message}");
    return 1;
}

Console.WriteLine($"К обработке: {clients.Count} клиентов. Параллелизм: {serviceOptions.Concurrency}.");
Log($"Старт: {clients.Count} клиентов, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");

// ── Обработка с ограничением параллелизма ───────────────────────────────────
var gate = new SemaphoreSlim(Math.Max(1, serviceOptions.Concurrency));
var processed = 0;

var tasks = clients.Select(async client =>
{
    await gate.WaitAsync(cts.Token);
    try
    {
        var result = await enricher.ProcessAsync(client, cts.Token);

        if (result is { Outcome: EnrichmentOutcome.Enriched, Update: not null })
        {
            try
            {
                var affected = await repo.UpdateAsync(client.PayerNum, result.Update, cts.Token);
                if (affected == 0)
                {
                    stats.RegisterUpdateFailed();
                    Log($"[UPDATE-0] PayerNum={client.PayerNum} ИНН={client.Inn} — строка не обновлена");
                }
                else
                {
                    stats.Register(EnrichmentOutcome.Enriched);
                    Log($"[OK] PayerNum={client.PayerNum} ИНН={client.Inn} КПП={client.Kpp} " +
                        $"-> Type={result.Update.ContragentTypeId} OGRN={result.Update.Ogrn} " +
                        $"KPP={result.Update.Kpp} Дир={result.Update.GeneralDirector} " +
                        $"Должн={result.Update.GeneralDirectorPositionName}");
                }
            }
            catch (Exception ex)
            {
                stats.RegisterUpdateFailed();
                Log($"[DB-ERR] PayerNum={client.PayerNum} ИНН={client.Inn} — {ex.Message}");
            }
        }
        else
        {
            stats.Register(result.Outcome);
            if (result.Outcome is not EnrichmentOutcome.AlreadyFilled)
                Log($"[{result.Outcome}] PayerNum={client.PayerNum} ИНН={client.Inn} КПП={client.Kpp}" +
                    (result.Message is null ? "" : $" — {result.Message}"));
        }
    }
    catch (OperationCanceledException)
    {
        // остановка — не считаем как ошибку сервиса
    }
    catch (Exception ex)
    {
        stats.Register(EnrichmentOutcome.Error);
        Log($"[ERR] PayerNum={client.PayerNum} ИНН={client.Inn} — {ex.Message}");
    }
    finally
    {
        gate.Release();
        var n = Interlocked.Increment(ref processed);
        if (n % 100 == 0 || n == clients.Count)
            Console.Write($"\rОбработано: {n}/{clients.Count}   ");
    }
}).ToArray();

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nОбработка прервана.");
}

// ── Итоги ───────────────────────────────────────────────────────────────────
Console.WriteLine();
var report = stats.Render();
Console.WriteLine(report);
Log(report);
Log($"Завершено: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($"Подробный лог: {Path.GetFullPath(logFile)}");

return 0;
