namespace GenesisFIS.Core.Cases;

/// <summary>
/// One person of a membership on the legacy "Record Of Non-Disclosure" screen (frmRECNonDisclosure): the principal (cover 00, MBMTwo) or a covered person
/// (MBMCoveredPersonsTwo), with the pre-existing and underlying illness that the member did not disclose.
/// Categories keep the legacy codes: pre-existing 0 Illness, 1 Injury, 2 Hospitalisation; underlying 0 Diabetes, 1 Hypertension.
/// </summary>
public sealed record NonDisclosurePerson(
    string MemberNumber, string CoverId, string? Name, string? PolicyNo, DateOnly? EffectiveDate, DateOnly? ExpiryDate,
    DateOnly? PreExistingDate, string? PreExistingCategory, string? PreExistingInformedBy, DateOnly? UnderlyingDate, string? UnderlyingCategory, string? UnderlyingInformedBy,
    bool HasDetailRecord);

public sealed record NonDisclosureView(string MemberNumber, string? Name, IReadOnlyList<NonDisclosurePerson> Persons);

public sealed record NonDisclosureRequest(
    DateOnly? PreExistingDate, string? PreExistingCategory, string? PreExistingInformedBy, DateOnly? UnderlyingDate, string? UnderlyingCategory, string? UnderlyingInformedBy);

public static class NonDisclosureRules
{
    public static readonly IReadOnlyDictionary<string, string> PreExistingCategories = new Dictionary<string, string> { ["0"] = "Illness", ["1"] = "Injury", ["2"] = "Hospitalisation" };
    public static readonly IReadOnlyDictionary<string, string> UnderlyingCategories = new Dictionary<string, string> { ["0"] = "Diabetes", ["1"] = "Hypertension" };
    public const int InformedByMax = 50;   // nvarchar(50) in MBMTwo and MBMCoveredPersonsTwo

    public static (string Field, string Message)? Validate(NonDisclosureRequest r, DateOnly today)
    {
        if (!string.IsNullOrEmpty(r.PreExistingCategory) && !PreExistingCategories.ContainsKey(r.PreExistingCategory)) return ("preExistingCategory", "Invalid pre-existing illness category.");
        if (!string.IsNullOrEmpty(r.UnderlyingCategory) && !UnderlyingCategories.ContainsKey(r.UnderlyingCategory)) return ("underlyingCategory", "Invalid underlying illness category.");
        foreach (var (field, d) in new[] { ("preExistingDate", r.PreExistingDate), ("underlyingDate", r.UnderlyingDate) })
            if (d is { } v && (v.Year < 1900 || v > today)) return (field, "Invalid Date Format! ");
        foreach (var (field, s) in new[] { ("preExistingInformedBy", r.PreExistingInformedBy), ("underlyingInformedBy", r.UnderlyingInformedBy) })
            if ((s?.Trim().Length ?? 0) > InformedByMax || (s?.Any(char.IsControl) ?? false)) return (field, $"Informed by can have at most {InformedByMax} characters.");
        return null;
    }
}

public interface ICaseNonDisclosureRepository
{
    /// <summary>The principal and, for family (F) and husband-and-wife (H) memberships, the covered persons (Go button). Null when the member does not exist.</summary>
    Task<NonDisclosureView?> GetAsync(string memberNumber, CancellationToken ct = default);

    /// <summary>The next or previous membership number (Next / Pre buttons); null at the end.</summary>
    Task<string?> NeighbourAsync(string memberNumber, bool next, CancellationToken ct = default);

    /// <summary>Saves the six non-disclosure fields of one person: MBMTwo for cover 00, MBMCoveredPersonsTwo otherwise.</summary>
    Task UpdateAsync(string memberNumber, string coverId, NonDisclosureRequest request, string user, CancellationToken ct = default);
}
