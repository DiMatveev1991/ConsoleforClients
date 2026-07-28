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
// Сервис отдаёт JSON как text/plain — совпадает с рабочим curl из swagger.
http.DefaultRequestHeaders.Accept.ParseAdd("text/plain");

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

var ioLock = new object();
await using var log = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };

// Только в файл-лог.
void Log(string line)
{
    lock (ioLock) log.WriteLine(line);
}

// В консоль (с цветом) и в файл-лог одновременно.
void Emit(string line, ConsoleColor? color = null)
{
    lock (ioLock)
    {
        if (color is { } c)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }
        else
        {
            Console.WriteLine(line);
        }
        log.WriteLine(line);
    }
}

// Перечень заполненных полей для строки-отчёта.
static string DescribeFilled(ConsoleForClients.Services.ClientUpdate u)
{
    var parts = new List<string>();
    if (u.ContragentTypeId is not null) parts.Add($"Тип={u.ContragentTypeId}");
    if (u.Ogrn is not null) parts.Add($"ОГРН={u.Ogrn}");
    if (u.Kpp is not null) parts.Add($"КПП={u.Kpp}");
    if (u.GeneralDirector is not null) parts.Add($"ФИО={u.GeneralDirector}");
    if (u.GeneralDirectorPositionName is not null) parts.Add($"Должность={u.GeneralDirectorPositionName}");
    return parts.Count == 0 ? "(нет изменений)" : string.Join("; ", parts);
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

var total = clients.Count;

var tasks = clients.Select(async client =>
{
    await gate.WaitAsync(cts.Token);
    var n = Interlocked.Increment(ref processed);
    var who = $"[{n}/{total}] PayerNum={client.PayerNum} Код={client.ClientCode} " +
              $"ИНН={client.Inn} КПП={(client.HasKpp ? client.Kpp : "нет")} «{client.PayerNameRus}»";
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
                    Emit($"{who} -> ОШИБКА: строка не обновлена (0 строк)", ConsoleColor.Red);
                }
                else
                {
                    stats.Register(EnrichmentOutcome.Enriched);
                    Emit($"{who} -> ЗАПОЛНЕН: {DescribeFilled(result.Update)}", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
                stats.RegisterUpdateFailed();
                Emit($"{who} -> ОШИБКА записи в БД: {ex.Message}", ConsoleColor.Red);
            }
        }
        else
        {
            stats.Register(result.Outcome);
            switch (result.Outcome)
            {
                case EnrichmentOutcome.AlreadyFilled:
                    Emit($"{who} -> ПРОПУСК: {result.Message ?? "заполнять нечего"}", ConsoleColor.DarkGray);
                    break;
                case EnrichmentOutcome.SkippedAmbiguous:
                    Emit($"{who} -> ПРОПУСК: {result.Message}", ConsoleColor.Yellow);
                    break;
                case EnrichmentOutcome.SkippedNoMatch:
                    Emit($"{who} -> ПРОПУСК: {result.Message}", ConsoleColor.Yellow);
                    break;
                case EnrichmentOutcome.NotFound:
                    Emit($"{who} -> НЕ ЗАПОЛНЕН: сервис ничего не вернул по ИНН", ConsoleColor.Yellow);
                    break;
                case EnrichmentOutcome.Error:
                    Emit($"{who} -> ОШИБКА запроса к сервису: {result.Message}", ConsoleColor.Red);
                    break;
                default:
                    Emit($"{who} -> {result.Outcome}: {result.Message}");
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // остановка — не считаем как ошибку сервиса
    }
    catch (Exception ex)
    {
        stats.Register(EnrichmentOutcome.Error);
        Emit($"{who} -> ОШИБКА: {ex.Message}", ConsoleColor.Red);
    }
    finally
    {
        gate.Release();
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
