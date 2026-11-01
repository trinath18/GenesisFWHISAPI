namespace GenesisFIS.Core.Cases;

public sealed record LifetimeMembership(string MemberNumber, string CoverId, string? Name, string? IcNo, string? PolicyNo, DateOnly? EffectiveDate, DateOnly? ExpiryDate, string? Status, string? DataStatus);

public sealed record LifetimeCase(string MemberNumber, string CaseNumber, string CoverId, string? PatientName, DateOnly? Admitted, DateOnly? Discharged, string? Claimability);

/// <summary>The renewal chain of a member (all memberships sharing the top-up number) and whether a lifetime limit can be allocated.</summary>
public sealed record LifetimeLimitView(
    string MemberNumber, string CoverId, string? TopupNumber, IReadOnlyList<LifetimeMembership> Memberships, IReadOnlyList<LifetimeCase> CurrentCases, IReadOnlyList<LifetimeCase> EarlierYearCases,
    bool Allocated, decimal? LifetimeLimit, bool HasClaimsHistory, bool Qualified, string Status);

public sealed record LifetimeLimitResult(decimal LifetimeLimit, string Message);

public interface ILifetimeLimitRepository
{
    /// <summary>Loads the chain for the member and cover (Go button). Returns null when the member has no renewal chain.</summary>
    Task<LifetimeLimitView?> GetAsync(string memberNumber, string coverId, CancellationToken ct = default);

    /// <summary>
    /// Allocates the lifetime limit (three times the plan's annual limit) when the member has at least three memberships in the chain, no claims in the earlier years and no lifetime limit yet:
    /// sets MBMOthers.MBMGuaRenewal to 3 and adds the MBMLifeTime row under the chain's top-up number.
    /// </summary>
    Task<LifetimeLimitResult> AllocateAsync(string memberNumber, string coverId, string user, CancellationToken ct = default);
}

public sealed record CaseAuditRecord(
    string Status, string DiscountType, string? InvoiceNo, DateOnly? InvoiceDate, decimal? InvoiceAmount, DateOnly? LetterSent, DateOnly? HospitalReply, DateOnly? VisitDate, string? VisitTime, string? Officer, string? VisitRemarks,
    decimal? DiscountReceived, DateOnly? DateToClaims, DateOnly? OpenDate, string? Answer, string? Remarks, string? ReviewAudit, string? UpdatedBy, DateOnly? Updated);

public sealed record CaseAuditPayment(string? PvNumber, DateOnly? PvDate, decimal? PaidAmount);

public sealed record CaseAuditView(string CaseNumber, CaseAdjustmentView Case, CaseAuditRecord? Audit, IReadOnlyList<CaseAuditPayment> Payments);

public sealed record CaseAuditRequest(
    string? Status, string? DiscountType, string? InvoiceNo, DateOnly? InvoiceDate, decimal? InvoiceAmount, DateOnly? LetterSent, DateOnly? HospitalReply, DateOnly? VisitDate, string? VisitTime, string? Officer, string? VisitRemarks,
    decimal? DiscountReceived, DateOnly? DateToClaims, DateOnly? OpenDate, string? Answer, string? Remarks, string? ReviewAudit);

public static class CaseAuditRules
{
    public static (string Field, string Message)? Validate(CaseAuditRequest r)
    {
        if (r.DiscountType is not ("A" or "B")) return ("discountType", "Please Enter Discount Type!");
        if (r.Status is not ("C" or "O")) return ("status", "Please Select Audit Review Status!");
        var today = DateOnly.FromDateTime(DateTime.Today).AddYears(1);
        foreach (var (name, d) in new[] { ("invoiceDate", r.InvoiceDate), ("letterSent", r.LetterSent), ("hospitalReply", r.HospitalReply), ("visitDate", r.VisitDate), ("dateToClaims", r.DateToClaims), ("openDate", r.OpenDate) })
            if (d is { } v && (v < new DateOnly(1990, 1, 1) || v > today)) return (name, "A date is invalid.");
        if (!string.IsNullOrEmpty(r.VisitTime) && !System.Text.RegularExpressions.Regex.IsMatch(r.VisitTime, @"^([01]\d|2[0-3]):[0-5]\d$")) return ("visitTime", "Please enter the visit time as hh:mm.");
        if (r.InvoiceAmount is < 0 or > 922337203685477m || r.DiscountReceived is < 0 or > 922337203685477m) return ("amount", "Amounts must be zero or more.");
        foreach (var (name, s, max) in new[] { ("invoiceNo", r.InvoiceNo, 255), ("officer", r.Officer, 255), ("visitRemarks", r.VisitRemarks, 255), ("answer", r.Answer, 255), ("remarks", r.Remarks, 255), ("reviewAudit", r.ReviewAudit, 4000) })
            if ((s?.Length ?? 0) > max || (s?.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ?? false)) return (name, $"The text is too long (maximum {max} characters) or has invalid characters.");
        return null;
    }
}

public interface ICaseAuditReviewRepository
{
    Task<CaseAuditView?> GetAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>The next or previous case in case-number order (Next / Prev), or the next case that already has an audit review (Next Rev Case). Null at the end.</summary>
    Task<string?> NeighbourAsync(string caseNumber, bool next, bool reviewedOnly, CancellationToken ct = default);

    /// <summary>Adds or updates the audit review of the case (CASAuditReview) and the audit status on the case cross reference in the maintenance database.</summary>
    Task<bool> SaveAsync(string caseNumber, CaseAuditRequest request, string user, CancellationToken ct = default);
}

public sealed record BordxSearchRequest(
    string? CaseNoFrom, string? CaseNoTo, bool ExactCase, string? BordxFrom, string? BordxTo, int? VersionFrom, int? VersionTo, string? CaseStatus, string? Claimability, string? Department, string? PassedBy,
    DateOnly? RegFrom, DateOnly? RegTo, DateOnly? AdmFrom, DateOnly? AdmTo, DateOnly? DisFrom, DateOnly? DisTo, DateOnly? DpoFrom, DateOnly? DpoTo, DateOnly? BordxDateFrom, DateOnly? BordxDateTo,
    bool NoAdmissionDate, bool NoRegDate, bool NoDischargeDate, bool NoDpo, string? Payor, string? StayType, string? CashType);

public sealed record BordxRow(
    bool Passed, string CaseNumber, int Version, string MemberNumber, string? PatientName, DateOnly? Registered, DateOnly? Admitted, DateOnly? Discharged, DateOnly? Dpo, int? Days, string? Batch, string? PassedBy, string? OpenedBy,
    string? Hospital, string? ClaimStatus, string? BordxRefNo, DateOnly? BordxDate, decimal? BordxAmount, string? StayType, string? CashType);

public sealed record BordxSearchResult(IReadOnlyList<BordxRow> Rows, bool Truncated);

public sealed record BordxPassItem(string? CaseNumber, int Version);

public sealed record BordxPassResult(string Batch, int Count, string Message);

public sealed record BordxOptions(IReadOnlyList<CaseOption> Payors, IReadOnlyList<string> PassedBy);

public interface ICaseSendToBordxRepository
{
    Task<BordxOptions> OptionsAsync(CancellationToken ct = default);
    Task<BordxSearchResult> SearchAsync(BordxSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Pass to BU: takes the next batch number of today for the user (CASPASSSeq) and stamps the ticked claim versions in ClaimOthersTwo with today's date (DPO), the batch, the user and department stage 1 (claim).
    /// </summary>
    Task<BordxPassResult> PassAsync(IReadOnlyList<BordxPassItem> items, string user, CancellationToken ct = default);

    /// <summary>Amend: changes the DPO of a version already passed, or clears it (back to the admission stage) when no date is given. Returns false when the version was not passed.</summary>
    Task<bool> AmendAsync(string caseNumber, int version, DateOnly? dpo, string user, CancellationToken ct = default);
}
