namespace Genesis.Core.Membership;

/// <summary>Criteria of MCOFees > Date Correction: like Invoice Manual Upd plus an invoice number range; lists rows that already have an invoice.</summary>
public sealed record McoDateFilter(string? Payor, string? MemberFrom, string? MemberTo, DateOnly? BordxFrom, DateOnly? BordxTo, int? BatchFrom, int? BatchTo, string? InvoiceFrom, string? InvoiceTo);

public sealed record McoDateCorrectionRequest(IReadOnlyList<McoCorrectionItem>? Items, DateOnly? InvoiceDate, int? BatchNo);

public sealed record McoDateCorrectionResult(int Updated, DateOnly InvoiceDate, int? BatchNo);

public static class McoDateCorrectionRules
{
    public const int MaxItems = 2000;

    public static (string Field, string Message)? ValidateFilter(McoDateFilter f)
    {
        bool Bad(string? s) => s is not null && (s.Trim().Length > 40 || s.Any(char.IsControl));
        if (string.IsNullOrWhiteSpace(f.Payor) || f.Payor.Trim().Length > 4) return ("payor", "Please select the payor.");
        if (Bad(f.MemberFrom) || Bad(f.MemberTo) || Bad(f.InvoiceFrom) || Bad(f.InvoiceTo)) return ("memberFrom", "A search value is invalid.");
        if (f.BordxFrom is null && f.BordxTo is null && f.BatchFrom is null && f.BatchTo is null && string.IsNullOrWhiteSpace(f.MemberFrom) && string.IsNullOrWhiteSpace(f.MemberTo) && string.IsNullOrWhiteSpace(f.InvoiceFrom) && string.IsNullOrWhiteSpace(f.InvoiceTo))
            return ("bordxFrom", "Please enter a member, bordx date, batch or invoice range as well as the payor.");
        if (f.BordxFrom is { } a && f.BordxTo is { } b && b < a) return ("bordxTo", "The end date cannot be before the start date.");
        if (f.BatchFrom is { } x && f.BatchTo is { } y && y < x) return ("batchTo", "The end batch cannot be before the start batch.");
        return null;
    }

    public static (string Field, string Message)? ValidateUpdate(McoDateCorrectionRequest r)
    {
        if (r.Items is null || r.Items.Count == 0) return ("items", "Please select record!");
        if (r.Items.Count > MaxItems) return ("items", $"At most {MaxItems} records can be corrected at once.");
        if (r.Items.Any(i => string.IsNullOrWhiteSpace(i.MemberNumber) || i.MemberNumber.Trim().Length > 30 || i.MemberNumber.Any(char.IsControl) || (i.CoverId?.Trim().Length ?? 0) is 0 or > 2)) return ("items", "A selected record is invalid.");
        if (r.Items.Select(i => (i.MemberNumber!.Trim(), i.CoverId!.Trim())).Distinct().Count() != r.Items.Count) return ("items", "A record is selected twice.");
        if (r.InvoiceDate is null || r.InvoiceDate < new DateOnly(1900, 1, 1)) return ("invoiceDate", "Please Enter Invoice Date!");
        if (r.BatchNo is < 0 or > 32767) return ("batchNo", "The batch number must be between 0 and 32767.");
        return null;
    }
}

public interface IMcoDateCorrectionRepository
{
    Task<McoCorrectionListing> SearchAsync(McoDateFilter filter, CancellationToken ct = default);

    /// <summary>Sets the invoice date (and the batch number when given) of rows that already have an invoice number. Refused for a row without one ("Please generate Invoice!"); all or nothing.</summary>
    Task<McoDateCorrectionResult> UpdateAsync(McoDateCorrectionRequest request, string user, CancellationToken ct = default);
}
