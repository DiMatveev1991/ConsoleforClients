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
    ///
    /// Ограничение задаётся числом РАЗЛИЧНЫХ запросов к сервису (MaxRequests), а не числом
    /// карточек: берутся первые N различных пар ИНН+КПП и ВСЕ карточки с этими парами.
    ///
    /// Из отбора исключены заведомо незаполнимые карточки:
    ///   * КПП — не заполняется никогда (в ветке kpp is null ставится skipKpp, а запись
    ///     требует пустого КПП у карточки), а у ИП и физлиц его не существует;
    ///   * ИНН на 9909 — иностранные организации и их представительства, в ЕГРЮЛ отсутствуют;
    ///   * ИНН длиной не 10 и не 12 — битые значения (КПП или БИК в поле ИНН, обрезанные номера).
    /// Без этих условий очередь не опустеет никогда: карточки возвращаются в выборку
    /// на каждом заходе и тратят лимит запросов впустую.
    /// </summary>
    public async Task<List<ClientPayer>> GetClientsToEnrichAsync(CancellationToken ct)
    {
        const string sql = @"
WITH scope AS (
    SELECT
         cp.PayerNum,
         cp.ClientCode,
         cp.PayerNameRus,
         cp.INN,
         cp.KPP,
         cp.GeneralDirector,
         cp.GeneralDirectorPositionName,
         cp.ContragentTypeId,
         cp.OGRN,
         LTRIM(RTRIM(cp.INN))              AS ReqInn,
         ISNULL(LTRIM(RTRIM(cp.KPP)), '')  AS ReqKpp
    FROM ClientsPayers cp WITH (NOLOCK)
    INNER JOIN Clients c WITH (NOLOCK) ON cp.PayerNum = c.PayerNum
    WHERE
         c.AgentCode = @AgentCode
         AND c.Client_Department = @ClientDepartment
         AND LTRIM(RTRIM(cp.INN)) <> ''
         AND LTRIM(RTRIM(cp.INN)) NOT LIKE '9909%'
         AND LEN(LTRIM(RTRIM(cp.INN))) IN (10, 12)
         AND (
              cp.ContragentTypeId IS NULL
           OR cp.OGRN IS NULL OR LTRIM(RTRIM(cp.OGRN)) = ''
           OR cp.GeneralDirectorPositionName IS NULL OR LTRIM(RTRIM(cp.GeneralDirectorPositionName)) = ''
         )
),
keys AS (
    -- первые N РАЗЛИЧНЫХ запросов к сервису; порядок стабильный, чтобы заходы не пересекались
    SELECT TOP (@MaxRequests)
           ReqInn,
           ReqKpp
    FROM scope
    GROUP BY ReqInn, ReqKpp
    ORDER BY MIN(PayerNum)
)
SELECT s.PayerNum,
       s.ClientCode,
       s.PayerNameRus,
       s.INN,
       s.KPP,
       s.GeneralDirector,
       s.GeneralDirectorPositionName,
       s.ContragentTypeId,
       s.OGRN
FROM scope s
INNER JOIN keys k
        ON k.ReqInn = s.ReqInn
       AND k.ReqKpp = s.ReqKpp
ORDER BY s.PayerNum;";

        var result = new List<ClientPayer>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AgentCode", _options.AgentCode);
        cmd.Parameters.AddWithValue("@ClientDepartment", _options.ClientDepartment);
        cmd.Parameters.AddWithValue("@MaxRequests",
            _options.MaxRequests > 0 ? _options.MaxRequests : int.MaxValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ClientPayer
            {
                PayerNum = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("PayerNum"))),
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
    /// Сколько карточек и сколько РАЗЛИЧНЫХ запросов осталось во всём охвате (без учёта MaxRequests).
    /// Условия те же, что в основной выборке.
    /// </summary>
    public async Task<(int Cards, int Requests)> GetRemainingAsync(CancellationToken ct)
    {
        const string sql = @"
WITH scope AS (
    SELECT cp.PayerNum,
           LTRIM(RTRIM(cp.INN))             AS ReqInn,
           ISNULL(LTRIM(RTRIM(cp.KPP)), '') AS ReqKpp
    FROM ClientsPayers cp WITH (NOLOCK)
    INNER JOIN Clients c WITH (NOLOCK) ON cp.PayerNum = c.PayerNum
    WHERE
         c.AgentCode = @AgentCode
         AND c.Client_Department = @ClientDepartment
         AND LTRIM(RTRIM(cp.INN)) <> ''
         AND LTRIM(RTRIM(cp.INN)) NOT LIKE '9909%'
         AND LEN(LTRIM(RTRIM(cp.INN))) IN (10, 12)
         AND (
              cp.ContragentTypeId IS NULL
           OR cp.OGRN IS NULL OR LTRIM(RTRIM(cp.OGRN)) = ''
           OR cp.GeneralDirectorPositionName IS NULL OR LTRIM(RTRIM(cp.GeneralDirectorPositionName)) = ''
         )
)
SELECT COUNT(*)                                     AS Cards,
       COUNT(DISTINCT CONCAT(ReqInn, '|', ReqKpp))  AS Requests
FROM scope;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AgentCode", _options.AgentCode);
        cmd.Parameters.AddWithValue("@ClientDepartment", _options.ClientDepartment);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)));

        return (0, 0);
    }

    /// <summary>
    /// Читает справочник типов контрагента (Types_ContragentTypes):
    /// ContragentTypeName (строка из сервиса, напр. "LEGAL") -> ContragentTypeId.
    /// Регистронезависимо. Пустой словарь — таблица недоступна/пуста.
    /// </summary>
    public async Task<Dictionary<string, int>> GetContragentTypeMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        const string sql =
            "SELECT ContragentTypeName, ContragentTypeId FROM dbo.Types_ContragentTypes WITH (NOLOCK);";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = GetNullableString(reader, "ContragentTypeName");
            if (string.IsNullOrWhiteSpace(name) || reader.IsDBNull(reader.GetOrdinal("ContragentTypeId")))
                continue;
            map[name.Trim()] = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ContragentTypeId")));
        }

        return map;
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