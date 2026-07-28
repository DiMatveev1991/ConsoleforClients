using System.Text.Json.Serialization;

namespace ConsoleForClients.Models;

/// <summary>
/// Один элемент ответа сервиса ИНН (contragents.major-express.ru).
/// Описаны только поля, которые используются при обогащении;
/// остальное игнорируется десериализатором.
/// </summary>
public sealed class ContragentDto
{
    [JsonPropertyName("contragentType")]
    public string? ContragentType { get; set; }

    [JsonPropertyName("organizationFullName")]
    public string? OrganizationFullName { get; set; }

    [JsonPropertyName("inn")]
    public string? Inn { get; set; }

    /// <summary>MAIN — головная организация, BRANCH — филиал.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("kpp")]
    public string? Kpp { get; set; }

    [JsonPropertyName("ogrn")]
    public string? Ogrn { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("managment")]
    public ManagmentDto? Managment { get; set; }

    [JsonPropertyName("individuaL_FIO")]
    public string? IndividualFio { get; set; }

    public bool IsMain =>
        string.Equals(Branch, "MAIN", StringComparison.OrdinalIgnoreCase);
}

public sealed class ManagmentDto
{
    [JsonPropertyName("fio")]
    public string? Fio { get; set; }

    [JsonPropertyName("post")]
    public string? Post { get; set; }

    [JsonPropertyName("startDate")]
    public DateTimeOffset? StartDate { get; set; }
}
