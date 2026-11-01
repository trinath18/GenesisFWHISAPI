namespace GenesisFIS.Core.Maintenance;

public sealed record IcdCodeRow(string Code, string? Description);

/// <summary>The two lists of the legacy ICD Selection tab: ICD codes not yet applied (left) and ICD codes applied, i.e. rows of ICDOriginal (right).</summary>
public sealed record IcdSelectionView(IReadOnlyList<IcdCodeRow> Available, IReadOnlyList<IcdCodeRow> Applied, int AvailableTotal, int AppliedTotal);

public sealed record IcdSelectionRequest(IReadOnlyList<string>? Codes);

public static class IcdSelectionRules
{
    public const int MaxCodes = 500;

    public static (string Field, string Message)? Validate(IcdSelectionRequest r)
    {
        if (r.Codes is null || r.Codes.Count == 0) return ("codes", "Please select at least one ICD code.");
        if (r.Codes.Count > MaxCodes) return ("codes", $"At most {MaxCodes} ICD codes can be moved at once.");
        if (r.Codes.Any(c => string.IsNullOrWhiteSpace(c) || c.Trim().Length > 5 || c.Any(char.IsControl))) return ("codes", "An ICD code is invalid.");
        return null;
    }
}

public interface IIcdSelectionRepository
{
    Task<IcdSelectionView> ListAsync(string? search, CancellationToken ct = default);

    /// <summary>Applies the codes: copies them from ICD into ICDOriginal (description cut to the 255 characters that table holds). Codes already applied are skipped.</summary>
    Task<int> SelectAsync(IcdSelectionRequest request, string user, CancellationToken ct = default);

    /// <summary>Removes the codes from ICDOriginal. The ICD table itself is not touched.</summary>
    Task<int> UnselectAsync(IcdSelectionRequest request, string user, CancellationToken ct = default);
}
