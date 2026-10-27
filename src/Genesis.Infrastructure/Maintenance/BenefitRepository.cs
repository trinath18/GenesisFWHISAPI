using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class BenefitRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IBenefitRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d ? DateOnly.FromDateTime(d) : null;
    private static int? Smallint(IDictionary<string, object?> r, string c) => I(r, c);

    public async Task<BenefitOptions> OptionsAsync(CancellationToken ct = default) => new(
        (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, HLTDescription FROM HLT ORDER BY HLTCode", null, ct)).Select(r => new BenefitOption(T(S(r, "HLTCode")), S(r, "HLTDescription"))).ToList(),
        (await QueryAsync(GenesisDb.Main, "SELECT INSCode, INSDescription FROM INS ORDER BY INSCode", null, ct)).Select(r => new BenefitOption(T(S(r, "INSCode")), S(r, "INSDescription"))).ToList(),
        (await QueryAsync(GenesisDb.Main, "SELECT DISTINCT PLNCode, HLTCode, PAYCode FROM PLN ORDER BY PLNCode", null, ct)).Select(r => new BenefitPlanOption(T(S(r, "PLNCode")), T(S(r, "HLTCode")), T(S(r, "PAYCode")))).ToList());

    private static async Task<List<BenefitLine>> LinesAsync(IDbSession s, string insured, string plan, DateOnly eff, CancellationToken ct)
    {
        var stored = (await s.QueryAsync("SELECT BNFCode, BNFNoOfDays, BNFClaimAbleAmount, BNFRMPerDis FROM BNF WHERE INSCode = @insured AND PLNCode = @plan AND BNFEffDate = @eff", new { insured, plan, eff = At(eff) }, ct))
            .ToDictionary(r => T(S(r, "BNFCode")), r => r, StringComparer.OrdinalIgnoreCase);
        return (await s.QueryAsync("SELECT BNFSubCode, BNFSUBName FROM BNFSubCat ORDER BY BNFSubCode DESC", null, ct)).Select(r =>
        {
            var code = T(S(r, "BNFSubCode"));
            stored.TryGetValue(code, out var b);
            return new BenefitLine(code, S(r, "BNFSUBName"), b is null ? null : Smallint(b, "BNFNoOfDays"), b is null ? null : Blank(S(b, "BNFClaimAbleAmount")), b is null ? null : Blank(S(b, "BNFRMPerDis")));
        }).ToList();
    }

    public async Task<BenefitEntry> GetEntryAsync(string health, string insured, string plan, DateOnly effectiveDate, CancellationToken ct = default)
    {
        if (BenefitRules.ValidateKey(health, insured, plan, effectiveDate) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var lines = await LinesAsync(s, T(insured), T(plan), effectiveDate, ct);
        return new BenefitEntry(T(health), T(insured), T(plan), effectiveDate, lines.Any(l => l.Days is not null || l.Amount is not null || l.PerDisability is not null), lines);
    }

    private async Task<BenefitEntry> SaveAsync(BenefitSaveRequest r, bool add, string user, CancellationToken ct)
    {
        if (BenefitRules.Validate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var (health, insured, plan, eff) = (T(r.Health), T(r.Insured), T(r.Plan), r.EffectiveDate!.Value);
        var today = DateTime.Today;
        List<BenefitLine> before, after;

        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            if (await s.ScalarAsync<int>("SELECT COUNT(*) FROM PLN WHERE PLNCode = @plan AND HLTCode = @health", new { plan, health }, ct) == 0)
                throw BusinessRuleException.NotFound($"Plan '{plan}' does not exist for health type '{health}'.");
            var valid = (await s.QueryAsync("SELECT BNFSubCode FROM BNFSubCat", null, ct)).Select(x => T(S(x, "BNFSubCode"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (r.Lines!.FirstOrDefault(l => !valid.Contains(l.Code!.Trim())) is { } unknown) throw new BusinessRuleException($"Sub category '{unknown.Code}' does not exist.", field: "lines");

            before = await LinesAsync(s, insured, plan, eff, ct);
            var exists = before.Any(l => l.Days is not null || l.Amount is not null || l.PerDisability is not null);
            if (add && exists) throw BusinessRuleException.Conflict("Record Duplicated! Benefits already exist for this plan, insured type and effective date; edit them instead.");
            if (!add && !exists) throw BusinessRuleException.NotFound("There are no benefits for this plan, insured type and effective date to edit.");

            var keep = new List<string>();
            foreach (var l in r.Lines!.Where(l => l.Days is not null || !string.IsNullOrWhiteSpace(l.Amount) || !string.IsNullOrWhiteSpace(l.PerDisability)))
            {
                var code = l.Code!.Trim(); keep.Add(code);
                var args = new { code, insured, plan, health, eff = At(eff), days = l.Days, amount = Blank(l.Amount), per = Blank(l.PerDisability), user, today };
                var updated = await s.ExecuteAsync("""
                    UPDATE BNF SET HLTCode = @health, BNFNoOfDays = @days, BNFClaimAbleAmount = @amount, BNFRMPerDis = @per, BNFLastUpdateUser = @user, BNFLastUpdateDate = @today
                    WHERE BNFCode = @code AND INSCode = @insured AND PLNCode = @plan AND BNFEffDate = @eff
                    """, args, ct);
                if (updated == 0)
                    await s.ExecuteAsync("""
                        INSERT INTO BNF (BNFCode, HLTCode, INSCode, PLNCode, BNFNoOfDays, BNFClaimAbleAmount, BNFRMPerDis, BNFEffDate, BNFLastUpdateUser, BNFLastUpdateDate)
                        VALUES (@code, @health, @insured, @plan, @days, @amount, @per, @eff, @user, @today)
                        """, args, ct);
            }
            foreach (var gone in before.Where(l => (l.Days is not null || l.Amount is not null || l.PerDisability is not null) && !keep.Contains(l.Code, StringComparer.OrdinalIgnoreCase)))
                await s.ExecuteAsync("DELETE FROM BNF WHERE BNFCode = @code AND INSCode = @insured AND PLNCode = @plan AND BNFEffDate = @eff", new { code = gone.Code, insured, plan, eff = At(eff) }, ct);
            after = await LinesAsync(s, insured, plan, eff, ct);
            await s.CommitAsync(ct);
        }

        audit.Record(new AuditEntry(add ? "Benefit.Add" : "Benefit.Update", "BNF", $"{health}|{insured}|{plan}|{eff:yyyy-MM-dd}", user, new Dictionary<string, object?>
            { ["before"] = before.Where(l => l.Days is not null || l.Amount is not null || l.PerDisability is not null), ["after"] = after.Where(l => l.Days is not null || l.Amount is not null || l.PerDisability is not null) }));
        return new BenefitEntry(health, insured, plan, eff, true, after);
    }

    public Task<BenefitEntry> AddAsync(BenefitSaveRequest request, string user, CancellationToken ct = default) => SaveAsync(request, true, user, ct);
    public Task<BenefitEntry> UpdateAsync(BenefitSaveRequest request, string user, CancellationToken ct = default) => SaveAsync(request, false, user, ct);

    public async Task<int> DeleteAsync(string health, string insured, string plan, DateOnly effectiveDate, string user, CancellationToken ct = default)
    {
        if (BenefitRules.ValidateKey(health, insured, plan, effectiveDate) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        (health, insured, plan) = (T(health), T(insured), T(plan));
        List<BenefitLine> before; int deleted;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            before = (await LinesAsync(s, insured, plan, effectiveDate, ct)).Where(l => l.Days is not null || l.Amount is not null || l.PerDisability is not null).ToList();
            if (before.Count == 0) throw BusinessRuleException.NotFound("There are no benefits for this plan, insured type and effective date.");
            var members = await s.ScalarAsync<int>("SELECT COUNT(*) FROM MBM WHERE INSCode = @insured AND PLNCode = @plan AND HLTCode = @health", new { insured, plan, health }, ct);
            if (members > 0) throw BusinessRuleException.Conflict($"This record cannot be deleted because it is used in Member table ({members} member(s) are on this plan).");
            deleted = await s.ExecuteAsync("DELETE FROM BNF WHERE INSCode = @insured AND PLNCode = @plan AND BNFEffDate = @eff", new { insured, plan, eff = At(effectiveDate) }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Benefit.Delete", "BNF", $"{health}|{insured}|{plan}|{effectiveDate:yyyy-MM-dd}", user, new Dictionary<string, object?> { ["before"] = before }));
        return deleted;
    }

    public async Task<IReadOnlyList<BenefitListingRow>> ListAsync(BenefitListingFilter f, CancellationToken ct = default)
    {
        var group = Blank(f.Group);
        var sql = """
            SELECT TOP (2000) b.BNFCode, c.BNFSUBName, b.HLTCode, b.INSCode, b.PLNCode, b.BNFEffDate, b.BNFNoOfDays, b.BNFClaimAbleAmount, b.BNFRMPerDis
            FROM BNF b LEFT JOIN BNFSubCat c ON c.BNFSubCode = b.BNFCode
            WHERE (@health IS NULL OR b.HLTCode = @health) AND (@insured IS NULL OR b.INSCode = @insured) AND (@plan IS NULL OR b.PLNCode = @plan)
              AND (@eff IS NULL OR b.BNFEffDate = @eff) AND (@code IS NULL OR b.BNFCode = @code)
              AND (@payor IS NULL AND @group IS NULL OR EXISTS (SELECT 1 FROM PLN p WHERE p.PLNCode = b.PLNCode AND p.HLTCode = b.HLTCode AND (@payor IS NULL OR p.PAYCode = @payor) AND (@group IS NULL OR p.GRPCompany LIKE @group)))
            ORDER BY b.BNFCode, b.PLNCode, b.BNFEffDate DESC
            """;
        var args = new { health = Blank(f.Health), insured = Blank(f.Insured), plan = Blank(f.Plan), eff = f.EffectiveDate is { } e ? (DateTime?)At(e) : null, code = Blank(f.Code), payor = Blank(f.Payor),
            group = group is null ? null : group.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%" };
        return (await QueryAsync(GenesisDb.Main, sql, args, ct)).Select(r => new BenefitListingRow(T(S(r, "BNFCode")), S(r, "BNFSUBName"), T(S(r, "HLTCode")), T(S(r, "INSCode")), T(S(r, "PLNCode")), Day(r, "BNFEffDate"),
            Smallint(r, "BNFNoOfDays"), Blank(S(r, "BNFClaimAbleAmount")), Blank(S(r, "BNFRMPerDis")))).ToList();
    }
}
