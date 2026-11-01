namespace GenesisFIS.Core.Membership;

/// <summary>One principal (cover 00) or covered person found by MCO Correction, with the invoice it may already be on.</summary>
public sealed record McoCorrectionRow(
    string MemberNumber, string CoverId, string? Name, DateOnly? BordxDate, int? BatchNo, string? InvoiceNo, DateOnly? InvoiceDate, decimal? Mco, DateOnly? PayorEffective, DateOnly? PayorExpiry);

public sealed record McoCorrectionListing(IReadOnlyList<McoCorrectionRow> Rows, bool Truncated);

public sealed record McoCorrectionItem(string? MemberNumber, string? CoverId);

public sealed record McoCorrectionRequest(IReadOnlyList<McoCorrectionItem>? Items, decimal? Fee);

public sealed record McoCorrectionResult(int Updated, decimal Fee);

public static class McoCorrectionRules
{
    public const int MaxItems = 2000;
    public const decimal MaxFee = 922337203685477m;

    public static (string Field, string Message)? Validate(McoCorrectionRequest r)
    {
        if (r.Items is null || r.Items.Count == 0) return ("items", "Please select record!");
        if (r.Items.Count > MaxItems) return ("items", $"At most {MaxItems} records can be corrected at once.");
        if (r.Items.Any(i => string.IsNullOrWhiteSpace(i.MemberNumber) || i.MemberNumber.Trim().Length > 30 || i.MemberNumber.Any(char.IsControl) || (i.CoverId?.Trim().Length ?? 0) is 0 or > 2)) return ("items", "A selected record is invalid.");
        if (r.Items.Select(i => (i.MemberNumber!.Trim(), i.CoverId!.Trim())).Distinct().Count() != r.Items.Count) return ("items", "A record is selected twice.");
        if (r.Fee is null) return ("fee", "Please Enter MCO Fee!");
        if (r.Fee < 0 || r.Fee > MaxFee) return ("fee", "The MCO fee must be an amount of zero or more.");
        return null;
    }
}

public interface IMcoCorrectionRepository
{
    /// <summary>Principals and their covered persons for the payor and criteria (same criteria as Invoice Manual Upd).</summary>
    Task<McoCorrectionListing> SearchAsync(McoManualFilter filter, CancellationToken ct = default);

    /// <summary>Sets the MCO fee of the selected rows (MBMOthers.MBMMCOFee for cover 00, MBMCoveredPersons.MBMCMCO otherwise). Refused when a row is missing or already on an invoice ("Invoice has been generated!"); all or nothing.</summary>
    Task<McoCorrectionResult> UpdateAsync(McoCorrectionRequest request, string user, CancellationToken ct = default);
}
