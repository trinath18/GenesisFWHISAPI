using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Cases;

public enum CaseSearchBy { MemberNo, PolicyNo, Name, Ic, CaseNo, EspRefNo, ClaimId }

public sealed record CaseSearchResult(
    string CaseNumber, string MemberNumber, string? CoverId, string? PolicyNo, string? MemberName, string? Insurer,
    string? PlanCode, string? HealthCode, string? HospitalCode, DateTime? Admitted, DateTime? Discharged,
    decimal? ReserveAmount, string? Status, string? PayorCode, string? ClaimId, string? EspRefNo);

public sealed record CaseSearchPage(IReadOnlyList<CaseSearchResult> Items, bool Truncated);

public sealed record CaseHeader(
    string CaseNumber, string MemberNumber, string? CoverId, string? PatientName, DateTime? Admitted, DateTime? Discharged,
    DateTime? Registered, string? HospitalName, string? Status, string? Claimability, string? Pending, string? StayType,
    string? PayType, string? Modality, string? ClaimNature, string? AdmissionReason, string? ReferredBy, decimal? LgAmount,
    string? LgTime, decimal? ReserveAmount, string? Query, string? LinkCaseNumber, string? TopupLimit, string? CaseIndicator,
    string? EspRefNo, string? ClaimId, int? PolicyYear, int? RoomBoardDays, decimal? TakeOverAmount, string? TakeOverIndicator,
    string? TakeOverPlan, string? InternalExternalTakeOver);

public sealed record CaseMember(
    string? PolicyNo, string? Name, string? IcBcPp, DateTime? DateOfBirth, string? PayorCode, string? InsurerCode, string? PlanCode,
    string? HealthCode, string? ProductName, string? Status, string? ExpiredStatus, decimal? AvailableLimit, DateTime? PayorEffDate,
    DateTime? PayorExpDate, string? GroupCompany, string? MemberType, string? AdultChild, string? Address);

public sealed record CaseProcedure(string? Procedure, string? CptCode, decimal? SurgeonFees, decimal? AnaesthetistFees, decimal? NonSurgicalFees, bool NonSurgical);

public sealed record CaseClinical(
    IReadOnlyList<CaseProcedure> Procedures, IReadOnlyList<string> InvestigationsDone, IReadOnlyList<string> MedicalSurgicalManagement,
    IReadOnlyList<string> MedicationOnDischarge, IReadOnlyList<string> InvestigationCodes, string? ConditionWillRecur,
    string? FollowUpRequired, string? FollowUp, string? FirstCall, string? FirstTreated, decimal? ReductionAmount, string? PayPending,
    IReadOnlyList<string> AttendingDoctors, IReadOnlyList<string> Anaesthetists, DateTime? FirstDoctorDate, DateTime? SubsequentDoctorDate,
    string? PatientAge, string? PatientAgeUnit);

public sealed record CaseDiagnosis(string? IcdCode, string? Description, DateTime? DateOfService);

public sealed record CaseUnderlying(string? Code, string? Reason, DateTime? DateOfService);

public sealed record CaseComplaint(DateTime? Date, string? Issue, string? Complainant, string? Action);

public sealed record CaseRemarks(
    string? Management, string? ManagementTwo, string? Page3, string? Page4, string? Page5, string? Internal);

public sealed record CaseDetail(
    CaseHeader Header, CaseMember Member, CaseClinical Clinical, IReadOnlyList<CaseDiagnosis> FirstDiagnoses,
    IReadOnlyList<CaseDiagnosis> FinalDiagnoses, IReadOnlyList<CaseUnderlying> Underlying, CaseComplaint? Complaint,
    CaseRemarks Remarks, bool HasScannedDocuments);

public sealed record CaseLetterOfGuarantee(string? Number, DateTime? Date, decimal? Amount, string? IssuedBy, string? Remark);
public sealed record CaseFollowUp(string? Number, decimal? Amount, string? IssuedBy, DateTime? IssuedDate, DateTime? FollowUpDate);
public sealed record CaseGuarantees(IReadOnlyList<CaseLetterOfGuarantee> LetterOfGuarantees, IReadOnlyList<CaseFollowUp> FollowUps);

public sealed record CaseWorksheet(
    int Version, decimal? TotalApproved, decimal? TotalActual, string? Pending, decimal? PayableToHospital, decimal? PayableToMember,
    string? BordxRefNo, DateTime? ClaimCloseDate, DateTime? BordxDate, decimal? BordxAmount, string? PayorApprovalStatus,
    DateTime? PayorApprovalDate, string? PayorApprovalCode, string? BordxReversal, DateTime? SentDate, string? CartonNo,
    string? DebitCreditRef, DateTime? DebitCreditDate);

public sealed record CaseInvoice(
    string? InvoiceNo, DateTime? InvoiceDate, string? Payee, string? PayTo, string? WorksheetVersion, decimal? Amount, string? BordxRefNo,
    DateTime? BordxDate, string? Pending);

public sealed record CasePayment(string? Reference, int? Version, DateTime? Date, string? ChequeNo, DateTime? ChequeDate, decimal? Amount, string? Status, string? Remark);

public sealed record CasePayments(
    IReadOnlyList<CasePayment> ToHospital, IReadOnlyList<CasePayment> ToMember, IReadOnlyList<CasePayment> ToMemberReissued,
    IReadOnlyList<CasePayment> ReceiptsFromMember, IReadOnlyList<CasePayment> ReceiptsFromPayor, IReadOnlyList<CasePayment> ReceiptsFromMnrb);

public sealed record CaseReminder(
    string? Number, string? Version, DateTime? Date, string? Rmf, string? OriginalInvoice, string? Report, string? ProposalForm,
    string? ReferralLetter, string? SubrogationForm, string? Receipt, string? Student, string? Police, string? Others1, string? Others2,
    string? RegisteredBy, DateTime? RegisteredDate);

public sealed record CaseRepudiationReason(string? Code, string? Description, string? Reason);

public sealed record CaseRepudiation(
    IReadOnlyList<CaseRepudiationReason> Reasons, string? IssuedBy, DateTime? Date, string? Document, string? Personnel, string? Attention);

public sealed record CaseCarton(
    string? Kind, int? Version, string? CartonNo, string? CartonStatus, string? FileStatus, string? RequestedBy, DateTime? RequestDate,
    DateTime? DeliveryDate, DateTime? ReturnDate, string? Remarks, string? UpdatedBy, DateTime? UpdatedDate);

public sealed record CaseBenefit(string Code, string? Description, int NoOfDays, string? ClaimableAmount, string? RmPerDisability);
public sealed record CaseBenefits(string? PlanCode, DateTime? EffectiveDate, IReadOnlyList<CaseBenefit> Items);

public sealed record CaseExclusions(IReadOnlyList<string> Principal, IReadOnlyList<string> Supplementary);

public sealed record SurgicalScheduleItem(string? PayorCode, string? PlanCode, string? MainCategory, string? SubCategory, int? Percentage);

public sealed record CaseAccountSummary(
    decimal TotalApproved, decimal TotalReimburse, decimal TotalExcess, decimal TotalPayableToMember, decimal TotalPayableToHospital,
    decimal TotalPaidByMember, decimal FinalApproved, decimal TotalPaidToMember, decimal TotalPaidToHospital);

public sealed record CaseDocumentFile(string Name, long Size);
public sealed record CaseDocumentListing(string Area, bool StoreConfigured, IReadOnlyList<CaseDocumentFile> Files);

public enum CaseDocumentArea { Scanned, Internal, Emails }

public sealed record CaseDocumentContent(Stream Content, string ContentType, string FileName);

/// <summary>Case-level documents: scanned claim documents, internal documents and e-mails. A share today, blob storage in the cloud.</summary>
public interface ICaseDocumentStore
{
    bool IsConfigured(CaseDocumentArea area);
    IReadOnlyList<CaseDocumentFile> List(CaseDocumentArea area, string caseNumber);
    CaseDocumentContent? Open(CaseDocumentArea area, string caseNumber, string fileName);
}

public static partial class CaseNumber
{
    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,20}$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9*\- ]{1,50}$")]
    private static partial Regex SearchPattern();

    public const int MinSearchLength = 3;
    public const int SearchLimit = 200;

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value) && Pattern().IsMatch(value.Trim());

    public static bool IsValidSearch(CaseSearchBy by, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return by switch
        {
            CaseSearchBy.Name => v.Length >= MinSearchLength && Regex.IsMatch(v, @"^[A-Za-z0-9 .,'/\-()@]{1,60}$"),
            CaseSearchBy.PolicyNo or CaseSearchBy.Ic or CaseSearchBy.EspRefNo or CaseSearchBy.ClaimId => SearchPattern().IsMatch(v),
            _ => v.Length >= MinSearchLength && SearchPattern().IsMatch(v),
        };
    }
}

public interface ICaseInquiryRepository
{
    Task<CaseSearchPage> SearchAsync(CaseSearchBy by, string value, string? planCode, CancellationToken ct = default);
    Task<CaseDetail?> GetAsync(string caseNumber, CancellationToken ct = default);
    Task<string?> GetMemberNumberAsync(string caseNumber, CancellationToken ct = default);
    Task<CaseGuarantees> GetGuaranteesAsync(string caseNumber, CancellationToken ct = default);
    Task<IReadOnlyList<CaseWorksheet>> GetWorksheetsAsync(string caseNumber, CancellationToken ct = default);
    Task<IReadOnlyList<CaseInvoice>> GetInvoicesAsync(string caseNumber, CancellationToken ct = default);
    Task<CasePayments> GetPaymentsAsync(string caseNumber, CancellationToken ct = default);
    Task<CaseAccountSummary?> GetAccountSummaryAsync(string caseNumber, CancellationToken ct = default);
    Task<IReadOnlyList<CaseReminder>> GetRemindersAsync(string caseNumber, CancellationToken ct = default);
    Task<CaseRepudiation?> GetRepudiationAsync(string caseNumber, CancellationToken ct = default);
    Task<IReadOnlyList<CaseCarton>> GetCartonsAsync(string caseNumber, CancellationToken ct = default);
    Task<CaseBenefits?> GetBenefitsAsync(string caseNumber, CancellationToken ct = default);
    Task<CaseExclusions?> GetExclusionsAsync(string caseNumber, CancellationToken ct = default);
    Task<IReadOnlyList<SurgicalScheduleItem>> GetSurgicalScheduleAsync(string caseNumber, CancellationToken ct = default);
}
