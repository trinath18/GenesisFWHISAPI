using System.Text.RegularExpressions;

namespace Genesis.Core.Membership;

public sealed record MemberSearchResult(
    string Number, string? CoverId, string Name, string? IcBcPp, string? PolicyNo, string? Status,
    DateTime? PayorEffDate, DateTime? PayorExpDate, string? GroupCompany, DateTime? BordxDate,
    DateTime? ValueDate, string? RegUser);

public sealed record MemberSearchPage(IReadOnlyList<MemberSearchResult> Items, bool Truncated);

public sealed record MemberRefund(
    decimal? PremiumReduction, decimal? McoRefund, decimal? ProviderRefund,
    decimal? ProviderEffective, decimal? McoEffective, decimal? PremiumEffective);

public sealed record MemberDetail(
    string Number, string Name, string? IcBcPp, string? PolicyNo, string? Status, string? DataStatus,
    string? ExpiredStatus, string? InsurerCode, string? PlanCode, string? HealthCode, string? PayorCode,
    string? MemberType, string? RenewFlag, string? Takeover, string? Sex, string? RaceCode, string? AgeCode,
    string? AdultChild, string? BranchCode, string? PolicyYear, string? BatchNo, DateTime? DateOfBirth,
    DateTime? ValueDate, DateTime? BordxDate, DateTime? PayorEffDate, DateTime? PayorExpDate,
    DateTime? FinalExpiryDate, DateTime? EndorsementDate, string? EndorseType, DateTime? PayorSubmissionDate,
    decimal? AvailableLimit, bool AnnualLimitApplies, string? PlanSpecialNote, bool? PlanMajorMedical,
    string? GroupCompany, string? Address1, string? Address2, string? Address3, string? Address4,
    string? City, string? PostCode, string? State, bool Reinstated, bool TopUp, MemberRefund? Refund);

public sealed record MemberAmendment(
    int Id, string? Type, DateTime? AmendDate, DateTime? EffectiveDate, DateTime? BordxDate, int? BatchNo);

public sealed record MemberCase(
    string CaseNumber, string MemberNumber, string? PatientName, string? Relationship, DateTime? Admitted,
    DateTime? Discharged, string? Claimability, string? Diagnosis, string? Status, string? Hospital, string? PayorCode);

public sealed record MemberAccountSummary(
    string? CoverId, decimal TotalApproved, decimal TotalPayableToHospital, decimal TotalPaidToHospital,
    decimal TotalPayableToMember, decimal TotalPaidByMember, decimal TotalPaidToMember, decimal TotalExcess,
    decimal TotalReimburse, decimal MemberPaidAmount);

public sealed record MemberAccountHistoryItem(
    string MemberNumber, DateTime? PayorEffDate, DateTime? Admitted, DateTime? CaseRegistered,
    string CaseReference, string? DebitCreditRef, DateTime? DebitCreditDate, decimal PayableByMedix,
    string? ReceiptNo, DateTime? ReceiptDate, decimal ReceiptAmount);

public sealed record MemberNote(string MemberNumber, string Text);

public static partial class MemberNumber
{
    [GeneratedRegex(@"^[A-Za-z0-9*\- ]{1,30}$")]
    private static partial Regex Pattern();

    public const int MinSearchLength = 3;
    public const int SearchLimit = 200;

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value) && Pattern().IsMatch(value.Trim());
    public static bool IsValidSearch(string? value) => IsValid(value) && value!.Trim().Length >= MinSearchLength;
}

public interface IMembershipEnquiryRepository
{
    Task<MemberSearchPage> SearchAsync(string prefix, CancellationToken ct = default);
    Task<MemberDetail?> GetAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberAmendment>> GetAmendmentsAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberCase>> GetCasesAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberCase>> GetCaseHistoryAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberAccountSummary>> GetAccountSummaryAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberAccountHistoryItem>> GetAccountHistoryAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberNote>> GetExclusionsAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<MemberNote>> GetRemarksAsync(string number, CancellationToken ct = default);
}

