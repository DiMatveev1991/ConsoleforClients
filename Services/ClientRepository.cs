using ConsoleForClients.Models;
using Microsoft.Data.SqlClient;

namespace ConsoleForClients.Services;

/// <summary>
/// Чтение московских клиентов КО и запись обогащённых полей в ClientsPayers.
/// </summary>
public sealed class ClientRepository
{
    private readonly string _connectionString;
    private readonly EnrichmentOptions _options;

    public ClientRepository(string connectionString, EnrichmentOptions options)
    {
        _connectionString = connectionString;
        _options = options;
    }

    /// <summary>
    /// Московские клиенты (AgentCode) с принадлежностью КО (Client_Department)
    /// и непустым ИНН, у которых есть хотя бы одно незаполненное целевое поле.
    /// </summary>
    public async Task<List<ClientPayer>> GetClientsToEnrichAsync(CancellationToken ct)
    {
        var top = _options.MaxClients > 0 ? $"TOP ({_options.MaxClients})" : "";

        var sql = $@"
SELECT {top}
     cp.PayerNum,
     cp.ClientCode,
     cp.PayerNameRus,
     cp.INN,
     cp.KPP,
     cp.GeneralDirector,
     cp.GeneralDirectorPositionName,
     cp.ContragentTypeId,
     cp.OGRN
FROM ClientsPayers cp WITH (NOLOCK)
INNER JOIN Clients c WITH (NOLOCK) ON cp.PayerNum = c.PayerNum
WHERE
     c.AgentCode = @AgentCode
     AND c.Client_Department = @ClientDepartment
     AND LTRIM(RTRIM(cp.INN)) <> ''
     AND (
          cp.ContragentTypeId IS NULL
       OR cp.OGRN IS NULL OR LTRIM(RTRIM(cp.OGRN)) = ''
       OR cp.KPP IS NULL OR LTRIM(RTRIM(cp.KPP)) = ''
       OR cp.GeneralDirectorPositionName IS NULL OR LTRIM(RTRIM(cp.GeneralDirectorPositionName)) = ''
     );";

        var result = new List<ClientPayer>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AgentCode", _options.AgentCode);
        cmd.Parameters.AddWithValue("@ClientDepartment", _options.ClientDepartment);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ClientPayer
            {
                PayerNum = reader.GetInt64(reader.GetOrdinal("PayerNum")),
                ClientCode = GetNullableString(reader, "ClientCode"),
                PayerNameRus = GetNullableString(reader, "PayerNameRus"),
                Inn = GetNullableString(reader, "INN"),
                Kpp = GetNullableString(reader, "KPP"),
                GeneralDirector = GetNullableString(reader, "GeneralDirector"),
                GeneralDirectorPositionName = GetNullableString(reader, "GeneralDirectorPositionName"),
                ContragentTypeId = GetNullableInt(reader, "ContragentTypeId"),
                Ogrn = GetNullableString(reader, "OGRN"),
            });
        }

        return result;
    }

    /// <summary>
    /// Обновляет только переданные (непустые) поля одной строки ClientsPayers по PayerNum.
    /// Возвращает число затронутых строк.
    /// </summary>
    public async Task<int> UpdateAsync(long payerNum, ClientUpdate update, CancellationToken ct)
    {
        var sets = new List<string>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand { Connection = conn };

        if (update.ContragentTypeId is not null)
        {
            sets.Add("ContragentTypeId = @ContragentTypeId");
            cmd.Parameters.AddWithValue("@ContragentTypeId", update.ContragentTypeId.Value);
        }
        if (update.Ogrn is not null)
        {
            sets.Add("OGRN = @OGRN");
            cmd.Parameters.AddWithValue("@OGRN", update.Ogrn);
        }
        if (update.Kpp is not null)
        {
            sets.Add("KPP = @KPP");
            cmd.Parameters.AddWithValue("@KPP", update.Kpp);
        }
        if (update.GeneralDirector is not null)
        {
            sets.Add("GeneralDirector = @GeneralDirector");
            cmd.Parameters.AddWithValue("@GeneralDirector", update.GeneralDirector);
        }
        if (update.GeneralDirectorPositionName is not null)
        {
            sets.Add("GeneralDirectorPositionName = @GeneralDirectorPositionName");
            cmd.Parameters.AddWithValue("@GeneralDirectorPositionName", update.GeneralDirectorPositionName);
        }

        if (sets.Count == 0)
            return 0;

        cmd.CommandText =
            $"UPDATE ClientsPayers SET {string.Join(", ", sets)} WHERE PayerNum = @PayerNum;";
        cmd.Parameters.AddWithValue("@PayerNum", payerNum);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? GetNullableString(SqlDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
    }

    private static int? GetNullableInt(SqlDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        if (reader.IsDBNull(i)) return null;
        return Convert.ToInt32(reader.GetValue(i));
    }
}

/// <summary>Набор полей для UPDATE. null = поле не меняем.</summary>
public sealed class ClientUpdate
{
    public int? ContragentTypeId { get; set; }
    public string? Ogrn { get; set; }
    public string? Kpp { get; set; }
    public string? GeneralDirector { get; set; }
    public string? GeneralDirectorPositionName { get; set; }

    public bool HasChanges =>
        ContragentTypeId is not null
        || Ogrn is not null
        || Kpp is not null
        || GeneralDirector is not null
        || GeneralDirectorPositionName is not null;
}
