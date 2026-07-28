using ConsoleForClients.Models;

namespace ConsoleForClients.Services;

public enum EnrichmentOutcome
{
    /// <summary>Данные получены, поля обновлены.</summary>
    Enriched,
    /// <summary>Все целевые поля уже заполнены — обновлять нечего.</summary>
    AlreadyFilled,
    /// <summary>Нет КПП и сервис вернул неоднозначный ответ (не ровно 1 запись).</summary>
    SkippedAmbiguous,
    /// <summary>С КПП, но подходящей записи не нашлось.</summary>
    SkippedNoMatch,
    /// <summary>Сервис ничего не вернул по ИНН.</summary>
    NotFound,
    /// <summary>Ошибка запроса к сервису.</summary>
    Error
}

public sealed record EnrichmentResult(
    ClientPayer Client,
    EnrichmentOutcome Outcome,
    ClientUpdate? Update = null,
    string? Message = null);

/// <summary>
/// Применяет к клиенту данные из сервиса ИНН по согласованным правилам.
/// Чистая логика, без побочных эффектов (запись в БД — снаружи).
/// </summary>
public sealed class EnrichmentService
{
    private readonly ContragentApiClient _api;
    private readonly EnrichmentOptions _options;

    public EnrichmentService(ContragentApiClient api, EnrichmentOptions options)
    {
        _api = api;
        _options = options;
    }

    public async Task<EnrichmentResult> ProcessAsync(ClientPayer client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.Inn))
            return new EnrichmentResult(client, EnrichmentOutcome.SkippedNoMatch, Message: "Пустой ИНН");

        // Если заполнять нечего — к сервису не обращаемся вовсе.
        if (!NeedsEnrichment(client))
            return new EnrichmentResult(client, EnrichmentOutcome.AlreadyFilled,
                Message: "все целевые поля уже заполнены — запрос не выполнялся");

        var kpp = client.HasKpp ? client.Kpp!.Trim() : null;
        var inn = NormalizeInn(client.Inn);

        var response = await _api.GetAsync(inn, kpp, ct);
        if (response is null)
            return new EnrichmentResult(client, EnrichmentOutcome.Error, Message: "Сервис недоступен/ошибка");

        if (response.Count == 0)
            return new EnrichmentResult(client, EnrichmentOutcome.NotFound);

        // Выбор основной записи под клиента.
        ContragentDto? primary;
        if (kpp is null)
        {
            // Без КПП: пригодно только если ответ однозначный (ровно одна запись).
            if (response.Count != 1)
                return new EnrichmentResult(client, EnrichmentOutcome.SkippedAmbiguous,
                    Message: $"Без КПП сервис вернул {response.Count} записей");
            primary = response[0];
        }
        else
        {
            // С КПП: берём запись, совпавшую по КПП; если сервис вернул одну — её.
            primary = response.FirstOrDefault(r =>
                          string.Equals(r.Kpp?.Trim(), kpp, StringComparison.Ordinal))
                      ?? (response.Count == 1 ? response[0] : null);

            if (primary is null)
                return new EnrichmentResult(client, EnrichmentOutcome.SkippedNoMatch,
                    Message: $"Не найдена запись с КПП {kpp} (записей: {response.Count})");
        }

        // Головная запись (MAIN) — источник главного КПП и руководителя.
        var main = response.FirstOrDefault(r => r.IsMain) ?? primary;

        var update = BuildUpdate(client, primary, main);

        if (!update.HasChanges)
            return new EnrichmentResult(client, EnrichmentOutcome.AlreadyFilled,
                Message: "в ответе сервиса нет недостающих данных");

        return new EnrichmentResult(client, EnrichmentOutcome.Enriched, update);
    }

    /// <summary>
    /// Есть ли у клиента хотя бы одно пустое целевое поле, которое имеет смысл заполнять.
    /// Если нет — запрос к сервису не нужен.
    /// </summary>
    private static bool NeedsEnrichment(ClientPayer client)
    {
        var needsType = client.ContragentTypeId is null;
        var needsOgrn = string.IsNullOrWhiteSpace(client.Ogrn);
        var needsKpp = !client.HasKpp;
        var needsDirector = string.IsNullOrWhiteSpace(client.GeneralDirectorPositionName);
        return needsType || needsOgrn || needsKpp || needsDirector;
    }

    /// <summary>
    /// Заполняет ТОЛЬКО пустые поля клиента. Уже заполненные не трогаем.
    /// </summary>
    private ClientUpdate BuildUpdate(ClientPayer client, ContragentDto primary, ContragentDto main)
    {
        var update = new ClientUpdate();

        // ContragentTypeId — только если пусто.
        if (client.ContragentTypeId is null
            && !string.IsNullOrWhiteSpace(primary.ContragentType)
            && _options.ContragentTypeMap.TryGetValue(primary.ContragentType.Trim(), out var typeId))
        {
            update.ContragentTypeId = typeId;
        }

        // OGRN — только если пусто.
        if (string.IsNullOrWhiteSpace(client.Ogrn) && !string.IsNullOrWhiteSpace(primary.Ogrn))
            update.Ogrn = primary.Ogrn.Trim();

        // KPP — только если у клиента пусто: берём главный КПП (запись MAIN).
        if (!client.HasKpp && !string.IsNullOrWhiteSpace(main.Kpp))
            update.Kpp = main.Kpp.Trim();

        // ФИО + должность директора — только если ДОЛЖНОСТЬ пустая.
        // Если должность уже заполнена — ни должность, ни ФИО не перезаписываем.
        if (string.IsNullOrWhiteSpace(client.GeneralDirectorPositionName))
        {
            var (fio, post) = ResolveDirector(main, primary);
            if (!string.IsNullOrWhiteSpace(fio) && !string.IsNullOrWhiteSpace(post))
            {
                update.GeneralDirectorPositionName = post;
                // ФИО заполняем вместе с должностью, тоже только если оно пустое.
                if (string.IsNullOrWhiteSpace(client.GeneralDirector))
                    update.GeneralDirector = fio;
            }
        }

        return update;
    }

    /// <summary>
    /// Восстанавливает потерянные ведущие нули ИНН.
    /// ИНН ЮЛ — 10 знаков, ИП — 12. Если в БД лежит короче (обрезан ноль слева) —
    /// дополняем: до 10 (если короче 10) или до 12 (если 11).
    /// </summary>
    private static string NormalizeInn(string inn)
    {
        var s = inn.Trim();
        if (s.Length is 10 or 12) return s;
        if (s.Length < 10) return s.PadLeft(10, '0');
        if (s.Length == 11) return s.PadLeft(12, '0');
        return s;
    }

    /// <summary>
    /// Руководитель: приоритет — managment головной записи, затем primary.
    /// Для ИП (managment нет) — individuaL_FIO + должность из конфигурации.
    /// </summary>
    private (string? fio, string? post) ResolveDirector(ContragentDto main, ContragentDto primary)
    {
        var mgmt = main.Managment ?? primary.Managment;
        if (mgmt is not null
            && !string.IsNullOrWhiteSpace(mgmt.Fio)
            && !string.IsNullOrWhiteSpace(mgmt.Post))
        {
            return (mgmt.Fio.Trim(), mgmt.Post.Trim());
        }

        var individualFio = main.IndividualFio ?? primary.IndividualFio;
        if (!string.IsNullOrWhiteSpace(individualFio))
            return (individualFio.Trim(), _options.IndividualPositionName);

        return (null, null);
    }
}
