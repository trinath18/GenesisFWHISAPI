namespace Genesis.Core.Membership;

public sealed record McoPayor(string Code, string? Name, bool CancelledOnly);

/// <summary>Legacy MCO Refund Listing criteria. DateBy is "effective" (endorsement effective date) or "bordx" (endorsement bordx date); PrintStatus N = no credit note yet, Y = credit note issued.</summary>
public sealed record McoRefundFilter(string? Payor, string? DateBy, DateOnly? From, DateOnly? To, string? PrintStatus);

public sealed record McoRefundRow(
    string MemberNumber, string CoverId, string? PolicyNo, string? Name, string? IcBcPp, string? Status, DateOnly? BordxDate, DateOnly? PayorEffective, DateOnly? PayorExpiry, int? DaysInsured,
    DateOnly? EndorsementEffective, DateOnly? EndorsementBordx, int? DaysUtilised, int? MonthsUtilised, decimal? McoFee, decimal? McoUtilised, decimal? McoRefund, int TotalCases,
    string? CreditNote, DateOnly? CreditNoteDate, string? InvoiceNo, DateOnly? InvoiceDate);

public sealed record McoRefundListing(IReadOnlyList<McoRefundRow> Rows, decimal TotalRefund, bool Truncated, bool CancelledOnly);

public static class McoRefundRules
{
    public static (string Field, string Message)? Validate(McoRefundFilter f)
    {
        if (string.IsNullOrWhiteSpace(f.Payor) || f.Payor.Trim().Length > 4) return ("payor", "Please Select Payor!");
        if (f.DateBy is not ("effective" or "bordx")) return ("dateBy", "Please Select Date Selection!");
        if (f.From is null && f.To is null) return ("from", "Please enter a date or a date range.");
        if (f.From is { } a && f.To is { } b && b < a) return ("to", "The end date cannot be before the start date.");
        if (f.PrintStatus is not ("N" or "Y")) return ("printStatus", "Please Select Print Status");
        return null;
    }
}

public interface IMcoRefundRepository
{
    Task<IReadOnlyList<McoPayor>> PayorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Principal members with an endorsement in the date range. For payors flagged MBMCancelStatus = Y only cancelled members whose endorsement was
    /// entered within 30 days of its effective date are listed (legacy rule); other payors list every member found.
    /// </summary>
    Task<McoRefundListing> ListAsync(McoRefundFilter filter, CancellationToken ct = default);

    /// <summary>Cancelled supplementary persons of one member (legacy View_MBM_Supplementary_Refund).</summary>
    Task<IReadOnlyList<McoRefundRow>> SupplementaryAsync(string memberNumber, CancellationToken ct = default);
}
