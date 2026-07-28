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
    public string IndividualPositionName { get; set; } = "Индивидуальный предприниматель";
    public int MaxClients { get; set; }
}
