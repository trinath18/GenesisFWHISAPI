using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Hospitals;

namespace GenesisFIS.Infrastructure.Hospitals;

internal sealed class HospitalRepository(IDbConnectionFactory db) : IHospitalRepository
{
    private const string Sql = """
        SELECT TOP (5000)
            HOSCode AS Code, HOSName AS Name, HOSTown AS Town, HOSState AS State,
            HOSTelNo AS Telephone, HOSEmail AS Email, HOSStatus AS Status
        FROM HOS
        WHERE @search IS NULL OR HOSCode LIKE @like ESCAPE '\' OR HOSName LIKE @like ESCAPE '\'
        ORDER BY HOSName
        """;

    public async Task<IReadOnlyList<HospitalListItem>> ListAsync(string? search, CancellationToken ct = default)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var like = search is null ? null : "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
        await using var conn = await db.OpenAsync(GenesisDb.Main, ct);
        var rows = await conn.QueryAsync<HospitalListItem>(new CommandDefinition(Sql, new { search, like }, cancellationToken: ct));
        return rows.ToList();
    }
}
