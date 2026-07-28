namespace ConsoleForClients.Models;

/// <summary>
/// Строка из ClientsPayers, которую нужно обогатить.
/// Идентификатор для UPDATE — PayerNum.
/// </summary>
public sealed class ClientPayer
{
    public long PayerNum { get; init; }
    public string? ClientCode { get; init; }
    public string? PayerNameRus { get; init; }
    public string? Inn { get; init; }
    public string? Kpp { get; init; }
    public string? GeneralDirector { get; init; }
    public string? GeneralDirectorPositionName { get; init; }
    public int? ContragentTypeId { get; init; }
    public string? Ogrn { get; init; }

    public bool HasKpp => !string.IsNullOrWhiteSpace(Kpp);
}
