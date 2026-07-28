using System.Collections.Concurrent;
using ConsoleForClients.Services;

namespace ConsoleForClients;

/// <summary>
/// Потокобезопасный сбор статистики обработки.
/// </summary>
public sealed class Statistics
{
    private readonly ConcurrentDictionary<EnrichmentOutcome, int> _counts = new();
    private int _updateFailed;

    public void Register(EnrichmentOutcome outcome)
        => _counts.AddOrUpdate(outcome, 1, (_, v) => v + 1);

    public void RegisterUpdateFailed()
        => Interlocked.Increment(ref _updateFailed);

    public int Get(EnrichmentOutcome outcome)
        => _counts.TryGetValue(outcome, out var v) ? v : 0;

    public int Total => _counts.Values.Sum() + _updateFailed;

    public int SuccessCount => Get(EnrichmentOutcome.Enriched);

    public int FailedCount =>
        Get(EnrichmentOutcome.Error)
        + Get(EnrichmentOutcome.NotFound)
        + Get(EnrichmentOutcome.SkippedAmbiguous)
        + Get(EnrichmentOutcome.SkippedNoMatch)
        + _updateFailed;

    public string Render()
    {
        var lines = new[]
        {
            "================ ИТОГИ ОБРАБОТКИ ================",
            $"Всего обработано клиентов : {Total}",
            $"  Успешно обогащено       : {Get(EnrichmentOutcome.Enriched)}",
            $"  Уже заполнено (пропуск) : {Get(EnrichmentOutcome.AlreadyFilled)}",
            $"  Пропущено (неоднозначно): {Get(EnrichmentOutcome.SkippedAmbiguous)}",
            $"  Пропущено (нет совпад.) : {Get(EnrichmentOutcome.SkippedNoMatch)}",
            $"  Не найдено в сервисе    : {Get(EnrichmentOutcome.NotFound)}",
            $"  Ошибок запроса          : {Get(EnrichmentOutcome.Error)}",
            $"  Ошибок записи в БД      : {_updateFailed}",
            "------------------------------------------------",
            $"  Итого успешно           : {SuccessCount}",
            $"  Итого не успешно        : {FailedCount}",
            "================================================",
        };
        return string.Join(Environment.NewLine, lines);
    }
}
