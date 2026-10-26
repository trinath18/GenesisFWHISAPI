using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class PanelHospitalRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IPanelHospitalRepository
{
    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    public async Task<IReadOnlyList<HospitalPanel>> ListPanelsAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Maintenance, "SELECT HosPanelCode, HosPanelDesc, Payor, HLTType FROM HosPanelCode ORDER BY HosPanelCode", null, ct))
            .Select(r => new HospitalPanel(S(r, "HosPanelCode") ?? "", S(r, "HosPanelDesc"), S(r, "Payor"), S(r, "HLTType"))).ToList();

    public async Task<HospitalPanel?> GetPanelAsync(string panelCode, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Maintenance, "SELECT HosPanelCode, HosPanelDesc, Payor, HLTType FROM HosPanelCode WHERE HosPanelCode = @code", new { code = panelCode }, ct))
            .Select(r => new HospitalPanel(S(r, "HosPanelCode") ?? "", S(r, "HosPanelDesc"), S(r, "Payor"), S(r, "HLTType"))).FirstOrDefault();

    public async Task<IReadOnlyList<PanelHospital>> ListMembersAsync(string panelCode, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, """
            SELECT h.HOSCode, h.HOSName, h.HOSTelNo, h.HOSFaxNo, h.HOSAdd1, h.HOSAdd2, h.HOSAdd3, h.HOSTown, h.HOSState
            FROM HOS h INNER JOIN PanelHospitals p ON p.HOSCode = h.HOSCode
            WHERE p.HosPanelCode = @code ORDER BY h.HOSName
            """, new { code = panelCode }, ct))
            .Select(r => new PanelHospital(S(r, "HOSCode") ?? "", S(r, "HOSName"), S(r, "HOSTelNo"), S(r, "HOSFaxNo"), S(r, "HOSAdd1"), S(r, "HOSAdd2"),
                S(r, "HOSAdd3"), S(r, "HOSTown"), S(r, "HOSState"))).ToList();

    public async Task<IReadOnlyList<AvailableHospital>> ListAvailableAsync(string panelCode, string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + Like(search.Trim()) + "%";
        return (await QueryAsync(GenesisDb.Main, """
            SELECT HOSCode, HOSName, HOSTelNo, HOSFaxNo FROM HOS
            WHERE HOSCode NOT IN (SELECT HOSCode FROM PanelHospitals WHERE HosPanelCode = @code)
              AND (@like IS NULL OR HOSName LIKE @like OR HOSCode LIKE @like)
            ORDER BY HOSName
            """, new { code = panelCode, like }, ct))
            .Select(r => new AvailableHospital(S(r, "HOSCode") ?? "", S(r, "HOSName"), S(r, "HOSTelNo"), S(r, "HOSFaxNo"))).ToList();
    }

    public async Task<PanelHospitalChangeResult> ApplyAsync(string panelCode, PanelHospitalChange change, string user, CancellationToken ct = default)
    {
        var add = (change.Add ?? []).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remove = (change.Remove ?? []).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (add.Count == 0 && remove.Count == 0) throw new BusinessRuleException("Choose at least one hospital to add or remove.");
        if (add.Count > PanelHospitalRules.MaxCodesPerRequest || remove.Count > PanelHospitalRules.MaxCodesPerRequest)
            throw new BusinessRuleException($"At most {PanelHospitalRules.MaxCodesPerRequest} hospitals can be changed at once.");
        if (add.Concat(remove).FirstOrDefault(c => !PanelHospitalRules.IsValidHospitalCode(c)) is { } bad)
            throw new BusinessRuleException($"'{bad}' is not a valid hospital code.", field: "hospitals");
        if (add.Intersect(remove, StringComparer.OrdinalIgnoreCase).Any())
            throw new BusinessRuleException("A hospital cannot be added and removed in the same request.", field: "hospitals");
        if (await GetPanelAsync(panelCode, ct) is null) throw BusinessRuleException.NotFound($"Panel '{panelCode}' does not exist.");

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);

        if (add.Count > 0)
        {
            var known = (await session.QueryAsync("SELECT HOSCode FROM HOS WHERE HOSCode IN @codes", new { codes = add }, ct)).Select(r => S(r, "HOSCode")!.Trim()).ToList();
            if (add.Except(known, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } unknown)
                throw new BusinessRuleException($"Hospital '{unknown}' does not exist.", RuleFailure.NotFound, "hospitals");
        }

        int added = 0, refreshed = 0;
        foreach (var code in add)
        {
            var args = new { panel = panelCode, code, user, date = DateTime.Today };
            var updated = await session.ExecuteAsync("UPDATE PanelHospitals SET LastUpdateUSR = @user, LastUpdateDate = @date WHERE HosPanelCode = @panel AND HOSCode = @code", args, ct);
            if (updated > 0) { refreshed++; continue; }
            await session.ExecuteAsync("INSERT INTO PanelHospitals (HosPanelCode, HOSCode, LastUpdateUSR, LastUpdateDate) VALUES (@panel, @code, @user, @date)", args, ct);
            added++;
        }

        var removed = remove.Count == 0 ? 0
            : await session.ExecuteAsync("DELETE FROM PanelHospitals WHERE HosPanelCode = @panel AND HOSCode IN @codes", new { panel = panelCode, codes = remove }, ct);

        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Panel.Update", "PanelHospitals", panelCode, user,
            new Dictionary<string, object?> { ["added"] = add, ["removed"] = remove, ["addedCount"] = added, ["refreshedCount"] = refreshed, ["removedCount"] = removed }));
        return new PanelHospitalChangeResult(added, refreshed, removed);
    }
}
