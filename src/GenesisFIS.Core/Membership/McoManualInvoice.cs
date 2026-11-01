namespace GenesisFIS.Core.Membership;

/// <summary>Criteria of MCOFees > Invoice Manual Upd: members and covered persons that have no invoice number yet.</summary>
public sealed record McoManualFilter(string? Payor, string? MemberFrom, string? MemberTo, DateOnly? BordxFrom, DateOnly? BordxTo, int? BatchFrom, int? BatchTo);

public sealed record McoManualRow(string MemberNumber, string CoverId, DateOnly? BordxDate, int? BatchNo, decimal Mco);

public sealed record McoManualListing(IReadOnlyList<McoManualRow> Rows, decimal TotalMco, bool HasZeroMco, bool Truncated);

public sealed record McoManualUpdateRequest(McoManualFilter? Filter, string? InvoiceNo, DateOnly? InvoiceDate);

public sealed record McoManualUpdateResult(int Updated, string InvoiceNo);

public static class McoManualRules
{
    public const int MaxRows = 5000;

    public static (string Field, string Message)? ValidateFilter(McoManualFilter f)
    {
        bool Bad(string? s) => s is not null && (s.Trim().Length > 30 || s.Any(char.IsControl));
        if (string.IsNullOrWhiteSpace(f.Payor) || f.Payor.Trim().Length > 4) return ("payor", "Please select the payor.");
        if (Bad(f.MemberFrom) || Bad(f.MemberTo)) return ("memberFrom", "The member number is invalid.");
        if (f.BordxFrom is null && f.BordxTo is null && f.BatchFrom is null && f.BatchTo is null && string.IsNullOrWhiteSpace(f.MemberFrom) && string.IsNullOrWhiteSpace(f.MemberTo))
            return ("bordxFrom", "Please enter a member number range, bordx dates or batch numbers as well as the payor.");
        if (f.BordxFrom is { } a && f.BordxTo is { } b && b < a) return ("bordxTo", "The end date cannot be before the start date.");
        if (f.BatchFrom is { } x && f.BatchTo is { } y && y < x) return ("batchTo", "The end batch cannot be before the start batch.");
        return null;
    }

    public static (string Field, string Message)? ValidateUpdate(McoManualUpdateRequest r)
    {
        if (r.Filter is null) return ("filter", "Please search first.");
        if (ValidateFilter(r.Filter) is { } p) return p;
        if (string.IsNullOrWhiteSpace(r.InvoiceNo) || r.InvoiceNo.Trim().Length > 40 || r.InvoiceNo.Any(char.IsControl)) return ("invoiceNo", "Please Keyin Invoice Number and Invoice Date !");
        if (r.InvoiceDate is null || r.InvoiceDate < new DateOnly(1900, 1, 1)) return ("invoiceDate", "Please Keyin Invoice Number and Invoice Date !");
        return null;
    }
}

public interface IMcoManualInvoiceRepository
{
    Task<McoManualListing> ListAsync(McoManualFilter filter, CancellationToken ct = default);

    /// <summary>Stamps the invoice number, date and user on every row the search finds (principals in MBMOthers, covered persons in MBMCoveredPersons). Refused when any fee is zero or more than 5,000 rows match.</summary>
    Task<McoManualUpdateResult> UpdateAsync(McoManualUpdateRequest request, string user, CancellationToken ct = default);
}
