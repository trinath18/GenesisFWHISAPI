namespace GenesisFIS.Core.Maintenance;

public sealed record BenefitOption(string Code, string? Description);
public sealed record BenefitPlanOption(string Code, string? Health, string? Payor);
public sealed record BenefitOptions(IReadOnlyList<BenefitOption> HealthPlans, IReadOnlyList<BenefitOption> Insured, IReadOnlyList<BenefitPlanOption> Plans);

/// <summary>One row of the Benefit Entry grid: a sub category with the values of the chosen plan version (all null when none is stored yet).</summary>
public sealed record BenefitLine(string Code, string? Name, int? Days, string? Amount, string? PerDisability);

public sealed record BenefitEntry(string Health, string Insured, string Plan, DateOnly EffectiveDate, bool Exists, IReadOnlyList<BenefitLine> Lines);

public sealed record BenefitLineInput(string? Code, int? Days, string? Amount, string? PerDisability);

/// <summary>Benefits of one plan version (health, insured, plan, effective date). The grid sent is the whole truth: lines with no values are not stored.</summary>
public sealed record BenefitSaveRequest(string? Health, string? Insured, string? Plan, DateOnly? EffectiveDate, IReadOnlyList<BenefitLineInput>? Lines);

public sealed record BenefitListingRow(string Code, string? Name, string Health, string Insured, string Plan, DateOnly? EffectiveDate, int? Days, string? Amount, string? PerDisability);

public sealed record BenefitListingFilter(string? Health, string? Insured, string? Plan, DateOnly? EffectiveDate, string? Code, string? Payor, string? Group);

public static class BenefitRules
{
    public static (string Field, string Message)? ValidateKey(string? health, string? insured, string? plan, DateOnly? effective)
    {
        string T(string? s) => s?.Trim() ?? "";
        if (T(health).Length is 0 or > 2) return ("health", "Please enter Health Type !");
        if (T(insured).Length is 0 or > 2) return ("insured", "Please enter Insured Type!");
        if (T(plan).Length is 0 or > 10 || T(plan).Any(c => !char.IsLetterOrDigit(c))) return ("plan", "Please enter Plan Code!");
        if (effective is null || effective < new DateOnly(1900, 1, 1) || effective > new DateOnly(2999, 12, 31)) return ("effectiveDate", "Please enter (Valid) effective Date!");
        return null;
    }

    public static (string Field, string Message)? Validate(BenefitSaveRequest r)
    {
        if (ValidateKey(r.Health, r.Insured, r.Plan, r.EffectiveDate) is { } k) return k;
        if (r.Lines is null || r.Lines.Count == 0 || r.Lines.Count > 200) return ("lines", "Please enter the benefits of at least one sub category.");
        var seen = new HashSet<string>();
        foreach (var l in r.Lines)
        {
            var code = l.Code?.Trim() ?? "";
            if (code.Length is 0 or > 10 || !seen.Add(code)) return ("lines", $"Invalid or repeated sub category code '{code}'.");
            if (l.Days is < 0 or > 32767) return ("lines", $"Days / months of {code} must be between 0 and 32767.");
            foreach (var (value, label) in new[] { (l.Amount, "RM (days/months)"), (l.PerDisability, "RM per disability") })
                if ((value?.Length ?? 0) > 30 || (value?.Any(char.IsControl) ?? false)) return ("lines", $"{label} of {code} can be at most 30 characters.");
        }
        if (!r.Lines.Any(l => l.Days is not null || !string.IsNullOrWhiteSpace(l.Amount) || !string.IsNullOrWhiteSpace(l.PerDisability))) return ("lines", "Please enter the benefits of at least one sub category.");
        return null;
    }
}

public interface IBenefitRepository
{
    Task<BenefitOptions> OptionsAsync(CancellationToken ct = default);

    /// <summary>The grid for one plan version: every sub category (highest code first, as in the legacy grid) merged with the stored values.</summary>
    Task<BenefitEntry> GetEntryAsync(string health, string insured, string plan, DateOnly effectiveDate, CancellationToken ct = default);

    /// <summary>Adds a plan version; refused when the plan, insured type and effective date already have benefits (legacy "Record Duplicated").</summary>
    Task<BenefitEntry> AddAsync(BenefitSaveRequest request, string user, CancellationToken ct = default);

    /// <summary>Replaces the benefits of an existing plan version with the grid sent (rows left empty are removed).</summary>
    Task<BenefitEntry> UpdateAsync(BenefitSaveRequest request, string user, CancellationToken ct = default);

    /// <summary>Deletes one plan version; refused while members exist on that plan (legacy VIEW_BNF_MBM_CodeChecking). Other versions are untouched.</summary>
    Task<int> DeleteAsync(string health, string insured, string plan, DateOnly effectiveDate, string user, CancellationToken ct = default);

    Task<IReadOnlyList<BenefitListingRow>> ListAsync(BenefitListingFilter filter, CancellationToken ct = default);
}
