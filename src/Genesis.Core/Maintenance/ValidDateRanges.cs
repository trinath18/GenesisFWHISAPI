namespace Genesis.Core.Maintenance;

/// <summary>The date windows the membership screens accept (legacy: Maintenance > Date, table MBMValidDateRange: a single row).</summary>
public sealed record ValidDateRanges(
    DateOnly? EffectiveStart, DateOnly? EffectiveEnd, DateOnly? ExpiryStart, DateOnly? ExpiryEnd, DateOnly? BirthStart, DateOnly? BirthEnd,
    string? UpdatedBy = null, DateOnly? UpdatedOn = null);

public static class ValidDateRangeRules
{
    public static readonly DateOnly Earliest = new(1900, 1, 1);
    public static readonly DateOnly Latest = new(2100, 12, 31);

    /// <summary>Returns the first problem found, or null. All six dates are required (as in the legacy form); each start must not be after its end.</summary>
    public static (string Field, string Message)? Validate(ValidDateRanges r)
    {
        var pairs = new (string Name, string StartField, DateOnly? Start, string EndField, DateOnly? End)[]
        {
            ("effective", nameof(r.EffectiveStart), r.EffectiveStart, nameof(r.EffectiveEnd), r.EffectiveEnd),
            ("expiry", nameof(r.ExpiryStart), r.ExpiryStart, nameof(r.ExpiryEnd), r.ExpiryEnd),
            ("date of birth", nameof(r.BirthStart), r.BirthStart, nameof(r.BirthEnd), r.BirthEnd),
        };
        foreach (var p in pairs)
        {
            if (p.Start is null) return (p.StartField, $"Please enter the {p.Name} start date.");
            if (p.End is null) return (p.EndField, $"Please enter the {p.Name} end date.");
            if (p.Start < Earliest || p.Start > Latest) return (p.StartField, $"The {p.Name} start date must be between {Earliest.Year} and {Latest.Year}.");
            if (p.End < Earliest || p.End > Latest) return (p.EndField, $"The {p.Name} end date must be between {Earliest.Year} and {Latest.Year}.");
            if (p.Start > p.End) return (p.EndField, $"The {p.Name} end date cannot be before its start date.");
        }
        return null;
    }
}

public interface IValidDateRangeRepository
{
    Task<ValidDateRanges?> GetAsync(CancellationToken ct = default);

    /// <summary>Updates the single row (creating it when the table is empty) and stamps user and date like the legacy form.</summary>
    Task<ValidDateRanges> SaveAsync(ValidDateRanges ranges, string user, CancellationToken ct = default);
}
