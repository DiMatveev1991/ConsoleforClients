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

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return Array.Empty<ContragentDto>();

                if (!resp.IsSuccessStatusCode)
                {
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
            catch (Exception) when (attempt < _options.MaxRetries)
            {
                await DelayBeforeRetry(attempt, ct);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
}
