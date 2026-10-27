using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class IcdSelectionRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IIcdSelectionRepository
{
    private const int Max = 2000;
    private static string T(string? s) => s?.Trim() ?? "";

    public async Task<IcdSelectionView> ListAsync(string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var args = new { like };
        var available = await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max}) i.ICDCode, i.ICDDescription FROM ICD i
            WHERE NOT EXISTS (SELECT 1 FROM ICDOriginal o WHERE o.ICDCode = i.ICDCode) AND (@like IS NULL OR i.ICDCode LIKE @like OR i.ICDDescription LIKE @like) ORDER BY i.ICDCode
            """, args, ct);
        var applied = await QueryAsync(GenesisDb.Main, $"SELECT TOP ({Max}) ICDCode, ICDDescription FROM ICDOriginal WHERE @like IS NULL OR ICDCode LIKE @like OR ICDDescription LIKE @like ORDER BY ICDCode", args, ct);
        var totalA = (await QueryAsync(GenesisDb.Main, "SELECT COUNT(*) AS N FROM ICD i WHERE NOT EXISTS (SELECT 1 FROM ICDOriginal o WHERE o.ICDCode = i.ICDCode)", null, ct)).Select(r => I(r, "N") ?? 0).First();
        var totalB = (await QueryAsync(GenesisDb.Main, "SELECT COUNT(*) AS N FROM ICDOriginal", null, ct)).Select(r => I(r, "N") ?? 0).First();
        IcdCodeRow Map(IDictionary<string, object?> r) => new(T(S(r, "ICDCode")), S(r, "ICDDescription")?.Trim());
        return new IcdSelectionView(available.Select(Map).ToList(), applied.Select(Map).ToList(), totalA, totalB);
    }

    public async Task<int> SelectAsync(IcdSelectionRequest request, string user, CancellationToken ct = default)
    {
        if (IcdSelectionRules.Validate(request) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var codes = request.Codes!.Select(T).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var done = new List<string>();
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            foreach (var code in codes)
            {
                var icd = (await s.QueryAsync("SELECT ICDCode, ICDCatCode, ICDDescription FROM ICD WHERE ICDCode = @code", new { code }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"ICD code '{code}' does not exist.");
                if (await s.ScalarAsync<int>("SELECT COUNT(*) FROM ICDOriginal WHERE ICDCode = @code", new { code }, ct) > 0) continue;
                var description = T(S(icd, "ICDDescription")); if (description.Length > 255) description = description[..255];
                await s.ExecuteAsync("INSERT INTO ICDOriginal (ICDCode, ICDCatCode, ICDDescription, ICDLastUpdateUser, ICDLastUpdateDate) VALUES (@code, @cat, @description, @user, @today)",
                    new { code = T(S(icd, "ICDCode")), cat = S(icd, "ICDCatCode"), description, user, today = DateTime.Today }, ct);
                done.Add(code);
            }
            await s.CommitAsync(ct);
        }
        if (done.Count > 0) audit.Record(new AuditEntry("Icd.Select", "ICDOriginal", string.Join(",", done.Take(20)), user, new Dictionary<string, object?> { ["codes"] = done }));
        return done.Count;
    }

    public async Task<int> UnselectAsync(IcdSelectionRequest request, string user, CancellationToken ct = default)
    {
        if (IcdSelectionRules.Validate(request) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var codes = request.Codes!.Select(T).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = new List<IDictionary<string, object?>>(); var count = 0;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            foreach (var code in codes)
            {
                removed.AddRange(await s.QueryAsync("SELECT ICDCode, ICDCatCode, ICDDescription, ICDLastUpdateUser, ICDLastUpdateDate FROM ICDOriginal WHERE ICDCode = @code", new { code }, ct));
                count += await s.ExecuteAsync("DELETE FROM ICDOriginal WHERE ICDCode = @code", new { code }, ct);
            }
            await s.CommitAsync(ct);
        }
        if (count > 0) audit.Record(new AuditEntry("Icd.Unselect", "ICDOriginal", string.Join(",", codes.Take(20)), user, new Dictionary<string, object?> { ["removed"] = removed }));
        return count;
    }
}
