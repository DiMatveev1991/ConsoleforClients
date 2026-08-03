using ConsoleForClients.Models;

namespace ConsoleForClients.Services;

public enum EnrichmentOutcome
{
    /// <summary>Данные получены, поля обновлены.</summary>
    Enriched,
    /// <summary>Все целевые поля уже заполнены — обновлять нечего.</summary>
    AlreadyFilled,
    /// <summary>Ответ неоднозначный и головной записи в нём нет.</summary>
    SkippedAmbiguous,
    /// <summary>С КПП, но подходящей записи не нашлось.</summary>
    SkippedNoMatch,
    /// <summary>Сервис ничего не вернул ни по паре ИНН+КПП, ни по одному ИНН.</summary>
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
    /// <summary>
    /// Предел длины ClientsPayers.GeneralDirector.
    /// Колонка расширена до varchar(150) 30.07.2026 (вместе с log_table_ClientsPayers,
    /// ClientsReqisitsHistory, ClientsSalesDep, log_table_ClientsSalesDep).
    /// </summary>
    private const int FioMaxLength = 150;

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

        // Сервис ищет по ПАРЕ ИНН+КПП и отдаёт 404 (пустой список), если такой пары нет.
        // Три подтверждённые причины:
        //   1) КПП крупнейшего налогоплательщика (99xx-50-xxx) в ЕГРЮЛ отсутствует — КАРКАДЕ;
        //   2) КПП в карточке устарел после смены инспекции — Центр ЮрИнфоР;
        //   3) организация представлена в ЕГРЮЛ только филиалами, головной записи нет.
        // ОГРН, тип контрагента и руководитель от КПП не зависят, поэтому повторяем
        // запрос по одному ИНН и дальше берём головную запись.
        var kppUnknown = false;
        if (response.Count == 0 && kpp is not null)
        {
            response = await _api.GetAsync(inn, null, ct);
            if (response is null)
                return new EnrichmentResult(client, EnrichmentOutcome.Error,
                    Message: "Сервис недоступен/ошибка при повторе без КПП");
            kppUnknown = true;
        }

        if (response.Count == 0)
            return new EnrichmentResult(client, EnrichmentOutcome.NotFound);

        // Выбор основной записи под клиента.
        ContragentDto? primary;
        var skipKpp = false;
        string? ambiguityNote = null;

        if (kpp is null || kppUnknown)
        {
            // Сопоставить карточку с конкретным подразделением нечем.
            // Берём головную запись: ОГРН, тип и руководитель у неё и у филиалов
            // одинаковы (у филиалов managment вообще пуст). КПП при этом не заполняем.
            primary = response.FirstOrDefault(r => r.IsMain);

            if (primary is null)
            {
                // Головной записи в ответе нет. Так бывает у иностранных представительств
                // (головная контора за рубежом и в ЕГРЮЛ отсутствует) и у организаций,
                // представленных только филиалами. Раньше такие карточки пропускались
                // целиком; теперь берём первую запись: запрос шёл по ИНН, значит все
                // записи принадлежат одному юрлицу, а ОГРН и тип у них общие.
                primary = response[0];
                if (response.Count > 1)
                    ambiguityNote = $"головной записи (MAIN) нет, взята первая из {response.Count}";
                else
                    ambiguityNote = "головной записи (MAIN) нет, взята единственная";
            }

            skipKpp = true;
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
        // Если её нет, вместо неё выступает primary.
        var main = response.FirstOrDefault(r => r.IsMain) ?? primary;

        var update = BuildUpdate(client, primary, main, skipKpp);
        var note = BuildNote(kpp, kppUnknown, main, ambiguityNote);

        if (!update.HasChanges)
            return new EnrichmentResult(client, EnrichmentOutcome.AlreadyFilled,
                Message: note ?? "в ответе сервиса нет недостающих данных");

        return new EnrichmentResult(client, EnrichmentOutcome.Enriched, update, note);
    }

    /// <summary>
    /// Есть ли у клиента хотя бы одно пустое целевое поле, которое имеет смысл заполнять.
    /// Если нет — запрос к сервису не нужен.
    ///
    /// КПП из проверки ИСКЛЮЧЁН: он не заполняется никогда (в ветке выбора головной записи
    /// ставится skipKpp, а запись требует пустого КПП у карточки), а у ИП и физлиц его
    /// не существует. Иначе такие карточки возвращались бы в выборку на каждом заходе.
    /// </summary>
    private static bool NeedsEnrichment(ClientPayer client)
    {
        var needsType = client.ContragentTypeId is null;
        var needsOgrn = string.IsNullOrWhiteSpace(client.Ogrn);
        var needsDirector = string.IsNullOrWhiteSpace(client.GeneralDirectorPositionName);
        return needsType || needsOgrn || needsDirector;
    }

    /// <summary>
    /// Пометки к строке отчёта. В БД ничего не пишет — только для лога.
    /// КПП сознательно НЕ перезаписываем: поле ведётся из 1С, и ближайший прогон
    /// синхронизации вернёт своё значение. Расхождение фиксируем, чтобы получить
    /// список карточек, где КПП в 1С разошёлся с ЕГРЮЛ.
    /// </summary>
    private static string? BuildNote(string? kpp, bool kppUnknown, ContragentDto main, string? ambiguityNote)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(ambiguityNote))
            parts.Add(ambiguityNote);

        if (kppUnknown && !string.IsNullOrWhiteSpace(main.Kpp)
            && !string.Equals(main.Kpp.Trim(), kpp, StringComparison.Ordinal))
        {
            parts.Add($"КПП {kpp} в ЕГРЮЛ не найден, у головной записи {main.Kpp.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(main.State)
            && !string.Equals(main.State.Trim(), "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"состояние организации: {main.State.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Справочные поля (тип, ОГРН, КПП) заполняются только если пусты.
    /// Руководитель — по правилу должности: пустая должность разрешает запись пары
    /// «ФИО + должность», непустая запрещает трогать оба поля.
    /// </summary>
    private ClientUpdate BuildUpdate(ClientPayer client, ContragentDto primary, ContragentDto main, bool skipKpp)
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

        // KPP — только если у клиента пусто И ответ однозначно относится к его подразделению.
        // При выборе головной записи из нескольких КПП не пишем: карточка может
        // принадлежать филиалу, а мы подставили бы КПП головной организации.
        if (!skipKpp && !client.HasKpp && !string.IsNullOrWhiteSpace(main.Kpp))
            update.Kpp = main.Kpp.Trim();

        // ФИО + должность директора — решает ТОЛЬКО должность.
        // Должность непустая  -> не трогаем ни должность, ни ФИО.
        // Должность пустая    -> пишем оба поля; ФИО перезаписываем БЕЗУСЛОВНО,
        //                        даже если оно уже заполнено (инициалы вида
        //                        «Конев А.Е.» заменяются полным ФИО из ЕГРЮЛ).
        if (string.IsNullOrWhiteSpace(client.GeneralDirectorPositionName))
        {
            var mgmt = main.Managment ?? primary.Managment;

            // Организацией руководит УПРАВЛЯЮЩАЯ КОМПАНИЯ: ЕГРЮЛ отдаёт её название
            // в managment.fio, а managment.post оставляет пустым — человека-руководителя
            // у такой организации нет. Название УК кладём в ДОЛЖНОСТЬ: она nvarchar(max),
            // ограничения по длине нет, а GeneralDirector — varchar(150), куда длинные
            // названия вида «ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ УПРАВЛЯЮЩАЯ
            // КОМПАНИЯ "..."» попросту не влезают. ФИО при этом не трогаем.
            var isManagementCompany = mgmt is not null
                && !string.IsNullOrWhiteSpace(mgmt.Fio)
                && string.IsNullOrWhiteSpace(mgmt.Post);

            // Только для ДЕЙСТВУЮЩИХ организаций. У ликвидированных ЕГРЮЛ хранит
            // последнюю известную запись об управляющей компании, и без этой проверки
            // она попадала бы в должность — причём необратимо: непустая должность
            // навсегда закрывает карточку для повторного прохода.
            var isActive = !string.IsNullOrWhiteSpace(main.State)
                && string.Equals(main.State.Trim(), "ACTIVE", StringComparison.OrdinalIgnoreCase);

            if (isManagementCompany && _options.FillPositionFromManagementCompany && isActive)
            {
                update.GeneralDirectorPositionName = mgmt!.Fio.Trim();
            }
            else
            {
                var (fio, post) = ResolveDirector(main, primary);
                if (!string.IsNullOrWhiteSpace(fio) && !string.IsNullOrWhiteSpace(post))
                {
                    // Не влезает в колонку — не пишем НИ ОДНО из двух полей.
                    // Обрезать ФИО нельзя (порча данных), а записать одну лишь должность
                    // означало бы навсегда закрыть карточку для повторного прохода:
                    // непустая должность запрещает запись руководителя.
                    if (fio.Length <= FioMaxLength)
                    {
                        update.GeneralDirectorPositionName = post;
                        update.GeneralDirector = fio;
                    }
                }
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
    ///
    /// Случай управляющей компании (managment.fio заполнен, managment.post пуст)
    /// сюда НЕ попадает — он разбирается отдельно в BuildUpdate.
    ///
    /// У ФИЛИАЛОВ managment пуст всегда: руководитель числится за головной организацией.
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