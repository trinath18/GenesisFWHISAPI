using Genesis.Core.Fees;

namespace Genesis.Core.Membership;

public sealed record AgeBandRow(int Index, string Code, string? Description, string? From, string? To, string? Plan);

/// <summary>
/// The legacy age functions (Medix.bas GetAge / GetAgeBand), reproduced exactly, including their quirks: the year difference is not birthday-adjusted
/// for ages of 18 and over, and for 1-17 the form's uninitialised year variable makes the text "0Y" or "-1Y" (both fall in the child band).
/// </summary>
public static class LegacyAge
{
    /// <summary>GetAge(effectiveDate, dateOfBirth): "-1D", "0Y", "-1Y" or "&lt;years&gt;Y".</summary>
    public static string Text(DateOnly effective, DateOnly dateOfBirth)
    {
        var diff = effective.Year - dateOfBirth.Year;
        if (diff <= 0) return "-1D";
        if (diff <= 17) return dateOfBirth.Month > effective.Month || dateOfBirth.Day > effective.Day ? "-1Y" : "0Y";
        return diff + "Y";
    }

    private static bool TryNumber(string? value, out int n)
    {
        n = 0;
        return value is { Length: > 1 } && int.TryParse(value[..^1], out n);
    }

    /// <summary>
    /// GetAgeBand: the plan's own rows are tried first, then the rows without a plan. Years (Y) need From &lt;= age &lt;= To; days (D) need From &lt;= age,
    /// rows ordered by the From text, first row taken. Returns the matching row or null.
    /// </summary>
    public static AgeBandRow? Band(string ageText, string plan, IEnumerable<AgeBandRow> rows)
    {
        if (ageText.Length < 2) return null;
        var cat = ageText[^1];
        if (!int.TryParse(ageText[..^1], out var age)) return null;

        IEnumerable<AgeBandRow> Match(IEnumerable<AgeBandRow> candidates)
        {
            var hits = candidates.Where(r => TryNumber(r.From, out var from) && from <= age && (cat != 'Y' || (TryNumber(r.To, out var to) && to >= age)));
            return cat == 'Y' ? hits.OrderBy(r => r.Index) : hits.OrderBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.Index);
        }
        var byPlan = string.IsNullOrWhiteSpace(plan) ? [] : Match(rows.Where(r => string.Equals(r.Plan?.Trim(), plan.Trim(), StringComparison.OrdinalIgnoreCase)));
        var row = byPlan.FirstOrDefault() ?? Match(rows.Where(r => string.IsNullOrWhiteSpace(r.Plan))).FirstOrDefault();
        return row;
    }

    /// <summary>The form treats age band code 01 as a child, everything else as an adult.</summary>
    public static string AdultChild(string ageCode) => ageCode.Trim() == "01" ? "C" : "A";
}

/// <summary>Plan change (endorsement type P). The new plan is priced on the member's payor period; <see cref="EffectiveDate"/> is the endorsement date used for the refund.</summary>
public sealed record PlanChangeRequest(string? MemberNumber, string? NewPlan, DateOnly? EffectiveDate, DateOnly? EndorsementBordxDate, int? BatchNo, bool UpdateMcoFee = true, bool UpdateAnnualLimit = false);

public sealed record PlanChangeQuote(
    string MemberNumber, string? Name, string? Status, string? OldPlan, string NewPlan, string AgeText, string AgeCode, string AdultChild,
    decimal BasicPremium, decimal Premium, decimal McoFee, decimal CurrentPremium, decimal CurrentMcoFee, bool McoFeeDiffers,
    decimal? CurrentAnnualLimit, decimal? NewAnnualLimit, bool AnnualLimitDiffers, DateOnly PayorEffectiveDate, DateOnly PayorExpiryDate, RefundAmounts Refund);

public sealed record PlanChangeResult(PlanChangeQuote Quote, bool CrossReferenceUpdated);

public static class PlanChangeRules
{
    public static (string Field, string Message)? Validate(PlanChangeRequest r, bool needEndorsement)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        var plan = r.NewPlan?.Trim() ?? "";
        if (plan.Length == 0) return ("newPlan", "Please enter the new plan code.");
        if (plan.Length > 8 || plan.Any(c => !char.IsLetterOrDigit(c))) return ("newPlan", "The plan code is invalid.");
        var min = new DateOnly(1900, 1, 1);
        if (r.EffectiveDate is null || r.EffectiveDate < min) return ("effectiveDate", "Please enter the endorsement effective date.");
        if (!needEndorsement) return null;
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

public interface IPlanChangeRepository
{
    /// <summary>Calculates the new age band, fees and refund exactly as the legacy form would, without changing anything.</summary>
    Task<PlanChangeQuote> QuoteAsync(PlanChangeRequest request, CancellationToken ct = default);

    /// <summary>Changes the plan: member status P, new plan and age band, new fees on MBMOthers, history, refund row (maintenance-database copies afterwards).</summary>
    Task<PlanChangeResult> ChangeAsync(PlanChangeRequest request, string user, CancellationToken ct = default);
}
