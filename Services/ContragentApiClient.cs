using System.Net;
using System.Text.Json;
using ConsoleForClients.Models;

namespace ConsoleForClients.Services;

/// <summary>
/// Обёртка над сервисом ИНН.
/// Вызов: GET {BaseUrl}{Path}?inn={inn}&kpp={kpp}&showMainContragent={bool}
/// </summary>
public sealed class ContragentApiClient
{
    private readonly HttpClient _http;
    private readonly ContragentServiceOptions _options;

    private int _httpCalls;
    private int _httpFailed;

    /// <summary>Фактическое число обращений к сервису, включая ретраи. Печатать в итогах прогона.</summary>
    public int HttpCalls => Volatile.Read(ref _httpCalls);

    /// <summary>Сколько из них завершились неуспешным статусом или исключением.</summary>
    public int HttpFailed => Volatile.Read(ref _httpFailed);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ContragentApiClient(HttpClient http, ContragentServiceOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>
    /// Возвращает список контрагентов по ИНН (и КПП, если передан).
    /// Пустой список — ничего не найдено. null — запрос не удался после ретраев.
    /// Причина отказа (код статуса, тело ответа, текст исключения) пишется в stderr:
    /// без неё в логе оставалось только обобщённое «Сервис недоступен/ошибка».
    /// </summary>
    public async Task<IReadOnlyList<ContragentDto>?> GetAsync(
        string inn, string? kpp, CancellationToken ct)
    {
        var query = new List<string>
        {
            $"inn={Uri.EscapeDataString(inn)}"
        };
        if (!string.IsNullOrWhiteSpace(kpp))
            query.Add($"kpp={Uri.EscapeDataString(kpp)}");
        query.Add($"showMainContragent={_options.ShowMainContragent.ToString().ToLowerInvariant()}");

        var url = $"{_options.Path}?{string.Join("&", query)}";
        var who = $"ИНН={inn} КПП={(string.IsNullOrWhiteSpace(kpp) ? "нет" : kpp)}";

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                Interlocked.Increment(ref _httpCalls);

                using var resp = await _http.GetAsync(url, ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return Array.Empty<ContragentDto>();

                if (!resp.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _httpFailed);

                    // тело читаем всегда: у этого сервиса при HTTP 500 в нём лежит
                    // текст ошибки PostgreSQL (в частности duplicate key по PK_ContragentData)
                    string body;
                    try
                    {
                        body = await resp.Content.ReadAsStringAsync(ct);
                    }
                    catch (Exception exBody)
                    {
                        body = $"(тело не прочитано: {exBody.Message})";
                    }

                    if (body.Length > 400)
                        body = body.Substring(0, 400);
                    body = body.Replace("\r", " ").Replace("\n", " ");

                    Console.Error.WriteLine(
                        $"[{who}] попытка {attempt}/{_options.MaxRetries}: " +
                        $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

                    if (attempt == _options.MaxRetries)
                        return null;

                    await DelayBeforeRetry(attempt, ct);
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var result = await JsonSerializer.DeserializeAsync<List<ContragentDto>>(
                    stream, JsonOptions, ct);
                return result ?? new List<ContragentDto>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // остановка по Ctrl+C — не наша ошибка, пробрасываем наверх
                throw;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries)
            {
                Interlocked.Increment(ref _httpFailed);
                Console.Error.WriteLine(
                    $"[{who}] попытка {attempt}/{_options.MaxRetries}: " +
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException is null ? "" : $" | внутренняя: {ex.InnerException.Message}"));

                await DelayBeforeRetry(attempt, ct);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _httpFailed);
                Console.Error.WriteLine(
                    $"[{who}] попытка {attempt}/{_options.MaxRetries} (последняя): " +
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException is null ? "" : $" | внутренняя: {ex.InnerException.Message}"));

                return null;
            }
        }

        return null;
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
}