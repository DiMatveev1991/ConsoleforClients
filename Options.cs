namespace ConsoleForClients;

public sealed class ContragentServiceOptions
{
    public string BaseUrl { get; set; } = "";
    public string Path { get; set; } = "/api/contragent";
    public bool ShowMainContragent { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int Concurrency { get; set; } = 10;
}

public sealed class EnrichmentOptions
{
    public int AgentCode { get; set; } = 304;
    public int ClientDepartment { get; set; } = 1;

    public Dictionary<string, int> ContragentTypeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Должность для ИП, если сервис вернул только individuaL_FIO без managment.</summary>
    public string IndividualPositionName { get; set; } = "Индивидуальный предприниматель";

    /// <summary>
    /// Организациями под управляющей компанией ЕГРЮЛ отдаёт её название в managment.fio,
    /// а managment.post оставляет пустым — человека-руководителя у них нет.
    /// При true название УК записывается в ДОЛЖНОСТЬ (GeneralDirectorPositionName,
    /// nvarchar(max), ограничения по длине нет), а ФИО не трогается.
    /// При false такие карточки не заполняются вовсе — поведение как раньше.
    /// </summary>
    public bool FillPositionFromManagementCompany { get; set; }

    /// <summary>Больше нигде не читается: отбор ограничивается через MaxRequests.</summary>
    public int MaxClients { get; set; }

    /// <summary>Ограничение по числу РАЗЛИЧНЫХ запросов к сервису за запуск (0 = без ограничения).</summary>
    public int MaxRequests { get; set; }
}