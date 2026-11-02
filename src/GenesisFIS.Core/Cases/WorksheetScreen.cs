namespace GenesisFIS.Core.Cases;

public sealed record WorksheetDiagnosis(string? Description, DateOnly? Onset, string? IcdCode);

public sealed record WorksheetNotCoveredItem(string Key, string Label, decimal Amount);

/// <summary>
/// The "Invoices &amp; Diagnosis" tab and the header boxes of the legacy worksheet forms: the member, policy and case, the member's address (for "Get Add"),
/// the final diagnoses, every invoice of the case and, for the version on screen, the reimbursement items that are not covered and, once saved, the status,
/// remarks, payee and address it was saved with.
/// </summary>
public sealed record WorksheetCaseDetail(
    string CaseNumber, string? CaseStatus, string? Claimability, string? MemberNumber, string? MemberName, string? PatientName, string? PatientId, string? MemberType,
    string? PolicyNo, string? PlanCode, string? Insurer, string? Health, DateOnly? PolicyEffective, DateOnly? PolicyExpiry, string? Payor, string? GroupCompany,
    string? Hospital, DateOnly? Admitted, DateOnly? Discharged, string? CaseRemark, string? Disability, string? IcdCodes,
    string? Address1, string? Address2, string? Address3, string? PostCode, string? City, string? State,
    IReadOnlyList<WorksheetDiagnosis> Diagnoses, IReadOnlyList<InvoiceEntry> Invoices, int Version, IReadOnlyList<WorksheetNotCoveredItem> NotCovered, DateOnly? LastDocDate,
    WorksheetSaveRequest? Saved = null);

public sealed record WorksheetBank(string Name, int? AccountDigits);

/// <summary>The drop-down lists of the legacy worksheet forms: worksheet status, banks (with the account number length), payment modes and states.</summary>
public sealed record WorksheetOptions(IReadOnlyList<CaseOption> Statuses, IReadOnlyList<WorksheetBank> Banks, IReadOnlyList<string> PaymentModes, IReadOnlyList<CaseOption> States);

public static class WorksheetOptionLists
{
    /// <summary>Legacy cboPaymentMode.</summary>
    public static readonly IReadOnlyList<string> PaymentModes = ["BY POST", "BANK-IN"];
}
