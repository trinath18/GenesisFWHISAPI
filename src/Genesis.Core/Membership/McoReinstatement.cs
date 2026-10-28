namespace Genesis.Core.Membership;

/// <summary>Criteria of MCOFees > MCO Reinstatement: "batch" = payor + endorsement bordx date + batch number; "invoice" = an invoice number.</summary>
public sealed record McoReinstatementFilter(string? Mode, string? Payor, DateOnly? BordxDate, int? BatchNo, string? InvoiceNo);

public sealed record McoReinstatementRow(
    string MemberNumber, string CoverId, string? Name, string Relation, DateOnly? BordxDate, string? Batch, string? PolicyNo, DateOnly? PayorEffective, DateOnly? PayorExpiry, string? Type,
    DateOnly? LapsedDate, DateOnly? ReinstateDate, string? InvoiceNo, DateOnly? InvoiceDate, string? InvoiceBy, decimal? McoFee);

public sealed record McoReinstatementListing(IReadOnlyList<McoReinstatementRow> Rows, int TotalPrincipal, int TotalSupplementary, decimal TotalFee, bool Invoiced, bool Truncated);

public sealed record McoReinstatementInvoiceResult(string InvoiceNo, int RowsInvoiced, McoReinstatementListing Listing);

public static class McoReinstatementRules
{
    public static (string Field, string Message)? Validate(McoReinstatementFilter f)
    {
        if (f.Mode == "invoice") return string.IsNullOrWhiteSpace(f.InvoiceNo) || f.InvoiceNo.Trim().Length > 40 || f.InvoiceNo.Any(char.IsControl) ? ("invoiceNo", "Please Enter Invoice No!") : null;
        if (f.Mode != "batch") return ("mode", "Please Enter MCO Criteria!");
        if (string.IsNullOrWhiteSpace(f.Payor) || f.Payor.Trim().Length > 4) return ("payor", "Please Enter Payor!");
        if (f.BordxDate is null) return ("bordxDate", "Please Enter Bordereaux Date!");
        if (f.BatchNo is null or < 0 or > 99999) return ("batchNo", "Please Enter Batch No!");
        return null;
    }
}

public interface IMcoReinstatementRepository
{
    /// <summary>Reinstated members and covered persons (table MBMReinstatement) of one bordx batch, or of one invoice.</summary>
    Task<McoReinstatementListing> ListAsync(McoReinstatementFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Print: gives every row of the batch the next invoice number (the payor's MCOInvoicePrefix, default S, + 5 digits from MBMMCOINVQ), today's date and the user, in one transaction.
    /// Refused when a row of the batch already has an invoice number (reprint it by invoice number).
    /// </summary>
    Task<McoReinstatementInvoiceResult> InvoiceAsync(McoReinstatementFilter filter, string user, CancellationToken ct = default);
}
