using Genesis.Core.Data;
using Genesis.Core.Fees;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Fees;

internal sealed class FeeRateSource(IDbConnectionFactory connections) : SqlRepository(connections), IFeeRateSource
{
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>For the Sihat Malaysia health plan (S) the legacy rate lookups ignore the payor; for every other plan the payor must match.</summary>
    private static bool UsesPayor(string health) => health.Trim() != "S";

    public async Task<(decimal Amount, decimal? FemaleAmount)?> GetPremiumRateAsync(FeeRequest r, CancellationToken ct = default)
    {
        var rows = await QueryAsync(GenesisDb.Main, """
            SELECT TOP 1 PRMAmount, PRMAmountFemale FROM PRM
            WHERE INSCode = @ins AND HLTCode = @health AND PLNCode = @plan AND AGECode = @age AND PRMEffDate <= @date AND (@usePayor = 0 OR PAYCode = @payor)
            ORDER BY PRMEffDate DESC
            """, new { ins = r.Insurer.Trim(), health = r.Health.Trim(), plan = r.Plan.Trim(), age = r.AgeCode.Trim(), date = At(r.RateDate), usePayor = UsesPayor(r.Health) ? 1 : 0, payor = r.Payor.Trim() }, ct);
        return rows.Count == 0 || Dec(rows[0], "PRMAmount") is not { } amount ? null : (amount, Dec(rows[0], "PRMAmountFemale"));
    }

    public async Task<(decimal Adult, decimal Child)?> GetMcoRateAsync(FeeRequest r, CancellationToken ct = default)
    {
        // The plan-specific row wins; the plan-less row (PLNCode IS NULL) is the fallback, exactly like the legacy GetMCO.
        var rows = await QueryAsync(GenesisDb.Main, """
            SELECT TOP 1 MCOAmountAdult, MCOAmountChild FROM MCO
            WHERE INSCode = @ins AND HLTCode = @health AND MCOEffDate <= @date AND (PLNCode = @plan OR PLNCode IS NULL)
            ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
            """, new { ins = r.Insurer.Trim(), health = r.Health.Trim(), plan = r.Plan.Trim(), date = At(r.RateDate) }, ct);
        return rows.Count == 0 ? null : (Dec(rows[0], "MCOAmountAdult") ?? 0, Dec(rows[0], "MCOAmountChild") ?? 0);
    }

    public async Task<PlanFeeFlags> GetPlanFlagsAsync(FeeRequest r, CancellationToken ct = default)
    {
        // PLN has one row per plan version; the legacy form took an arbitrary one. The latest version effective on the rate date is used.
        var rows = await QueryAsync(GenesisDb.Main, """
            SELECT TOP 1 PLNRoundUpStatus, PLNProRate, PLNPremGenderStatus FROM PLN
            WHERE PLNCode = @plan AND HLTCode = @health AND PLNEffDate <= @date AND (@usePayor = 0 OR PAYCode = @payor)
            ORDER BY PLNEffDate DESC
            """, new { plan = r.Plan.Trim(), health = r.Health.Trim(), date = At(r.RateDate), usePayor = r.Health.Trim() == "N" ? 1 : 0, payor = r.Payor.Trim() }, ct);
        if (rows.Count == 0) return new PlanFeeFlags();
        var row = rows[0];
        return new PlanFeeFlags(
            RoundUp: !string.Equals(S(row, "PLNRoundUpStatus") is { Length: > 0 } ru ? ru : "Y", "N", StringComparison.OrdinalIgnoreCase),
            ProRate: !string.Equals(S(row, "PLNProRate") is { Length: > 0 } pr ? pr : "Y", "N", StringComparison.OrdinalIgnoreCase),
            GenderPremium: string.Equals(S(row, "PLNPremGenderStatus"), "Y", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DiscountRates> GetDiscountsAsync(string plan, CancellationToken ct = default)
    {
        var rows = await QueryAsync(GenesisDb.Maintenance, "SELECT TOP 1 PolicyDisc, RenewalDisc FROM DiscPlan WHERE PLNCode = @plan", new { plan = plan.Trim() }, ct);
        return rows.Count == 0 ? new DiscountRates() : new DiscountRates(Dec(rows[0], "PolicyDisc") ?? 0, Dec(rows[0], "RenewalDisc") ?? 0);
    }
}
