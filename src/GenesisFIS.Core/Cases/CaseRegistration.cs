namespace GenesisFIS.Core.Cases;

public sealed record CaseOption(string Code, string Name);

public sealed record CaseRegistrationOptions(
    IReadOnlyList<CaseOption> Claimability, IReadOnlyList<CaseOption> Hospitals, IReadOnlyList<CaseOption> PayTypes, IReadOnlyList<CaseOption> StayTypes, IReadOnlyList<CaseOption> Modalities,
    IReadOnlyList<CaseOption> Natures, IReadOnlyList<CaseOption> SpecialApprovals, IReadOnlyList<CaseOption> Icd, IReadOnlyList<CaseOption> Repudiation, IReadOnlyList<CaseOption> RepudiationSelections);

public sealed record CasePatient(string CoverId, string Name, string? IcBcPp, string Relation, DateOnly? BirthDate);

/// <summary>The policy a case is registered against, with the checks the legacy form makes when the member number is shown.</summary>
public sealed record CaseMemberView(
    string MemberNumber, string Name, string? IcBcPp, string? PolicyNo, string? Health, string? Insured, string? Plan, string? Payor, string? Status, DateOnly? BirthDate, DateOnly? EffectiveDate, DateOnly? ExpiryDate,
    decimal? AvailableLimit, bool TopUp, bool Terminated, string? PlanMessage, IReadOnlyList<CasePatient> Patients, string? BlockedReason, IReadOnlyList<string> Warnings,
    // The legacy header and General Info tab: renewal and take-over flags, the plan's annual limit and the member's address.
    string? Renewal = null, string? TakeOver = null, decimal? AnnualLimit = null, string? Address1 = null, string? Address2 = null, string? Address3 = null,
    string? PostCode = null, string? City = null, string? State = null);

public sealed record CaseDiagnosisInput(string? Code, string? Text, DateOnly? Date);

/// <summary>A procedure (surgical or non-surgical) with its CPT / OPCS code.</summary>
public sealed record CaseProcedureInput(string? Text, string? CptCode);

/// <summary>An investigation done and its investigation code.</summary>
public sealed record CaseInvestigationInput(string? Done, string? Code);

public sealed record CaseRegistrationRequest(
    string? MemberNumber, string? PatientId, string? Claimability, string? HospitalCode, string? HospitalName, DateOnly? DateAdmitted, DateOnly? DateDischarge, string? Pending, decimal? ReserveAmount, decimal? LgAmount,
    string? LgTime, string? PayType, string? StayType, string? Modality, string? Nature, string? SpecialApproval, string? TopUpLimit, string? Query, string? LinkCaseNo, string? ReferredBy, string? AdmissionReason,
    DateOnly? DocReceivedDate, int? PolicyYear, string? EspRefNo, string? TakeOverInd, decimal? TakeOverAmount, string? TakeOverPlan, string? IntExtTakeOver, string? PatientAge, string? AgeUnit,
    string? FirstCall, string? FirstTreated, string? PayPending, string? FollowUp, string? ConditionWillRecur, string? FollowUpRequired, IReadOnlyList<string>? Remarks,
    IReadOnlyList<CaseDiagnosisInput>? FirstDiagnosis, IReadOnlyList<CaseDiagnosisInput>? FinalDiagnosis, bool AcknowledgeLongStay, bool AcknowledgeRecentPolicy, bool AcknowledgeOpenCase,
    IReadOnlyList<string>? RepudiationCodes = null, string? Document = null, string? Personnel = null, string? Attention = null, bool Underlying = false, IReadOnlyList<CaseDiagnosisInput>? UnderlyingDiagnosis = null, decimal? DeductionAmount = null,
    IReadOnlyList<string>? AttendingDoctors = null, IReadOnlyList<string>? Anaesthetists = null, IReadOnlyList<CaseProcedureInput>? Procedures = null, IReadOnlyList<CaseProcedureInput>? NonSurgicalProcedures = null,
    IReadOnlyList<CaseInvestigationInput>? Investigations = null, IReadOnlyList<string>? MedicalManagement = null, DateOnly? FirstDocDate = null, DateOnly? SubsequentDocDate = null);

public sealed record CaseCoveredExclusion(string CoverId, string? Name, string? Exclusion);

public sealed record CaseSurgicalSchedule(string Payor, string Plan, string MainCategory, string SubCategory, string Percent);

/// <summary>Warranty / exclusions, the management note and the surgical schedule of the plan shown on the Case Registration tabs.</summary>
public sealed record CaseMemberExtras(string? PrincipalExclusion, IReadOnlyList<CaseCoveredExclusion> CoveredExclusions, string? ManagementNote, IReadOnlyList<CaseSurgicalSchedule> SurgicalSchedule);

public sealed record CaseRegistrationResult(string CaseNumber, string MemberNumber, string PatientId, string Message, IReadOnlyList<string> Letters);

public static class CaseRegistrationRules
{
    public static readonly IReadOnlyList<CaseOption> Claimability =
    [
        new("AA", "Accepted"), new("RR", "Repudiated"), new("CC", "Cancelled"), new("SA", "Special Approval"), new("PP", "Pay & Claim"), new("NN", "Not Decided"), new("NA", "Not Decided/Accepted"),
        new("NR", "Not Decided/Repudiated"), new("PA", "Pay&Claim/Accepted"), new("PR", "Pay&Claim/Rejected"), new("AR", "Accepted/Rejected"), new("RA", "Rejected/Accepted"), new("BR", "BTBG/Rejected"),
        new("ER", "Expired/Rejected"), new("AI", "Accepted/Insurance")
    ];

    /// <summary>Claimability codes that start the repudiation flow (repudiation codes, letters); not part of this slice.</summary>
    public static readonly string[] Repudiating = ["RR", "NR", "PR", "AR", "BR", "ER"];

    /// <summary>Hospital rows that stand for a kind of provider: the real name must be typed.</summary>
    public static readonly string[] GenericHospitals = ["DENTAL CLINIC", "DIALYSIS CENTRE", "FOREIGN HOSPITAL", "GENERAL PRACTITIONER", "GOVERNMENT HOSPITAL", "NON-PANEL HOSPITAL", "SPECIALIST CLINIC"];

    public const decimal MaxAmount = 922337203685477m;

    private static bool Bad(string? s, int max) => (s?.Trim().Length ?? 0) > max || (s?.Any(char.IsControl) ?? false);
    private static bool YesNo(string? s) => string.IsNullOrEmpty(s) || s is "Y" or "N";

    public static (string Field, string Message)? Validate(CaseRegistrationRequest r, DateOnly today)
    {
        var payType = r.PayType?.Trim() ?? "";
        var claim = r.Claimability?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(r.MemberNumber) || Bad(r.MemberNumber, 15)) return ("memberNumber", "Please Enter Membership Information");
        if (string.IsNullOrWhiteSpace(r.PatientId) || r.PatientId.Trim().Length != 2) return ("patientId", "Please Enter Patient Information");
        if (claim.Length == 0) return ("claimability", "Please Enter Claimability Status");
        if (!Claimability.Any(c => c.Code == claim)) return ("claimability", "Unknown claimability status.");
        if (string.IsNullOrWhiteSpace(r.HospitalCode) || Bad(r.HospitalCode, 10)) return ("hospitalCode", "Please Enter Hospital Information");
        if (r.DateAdmitted is null) return ("dateAdmitted", "Please Enter Admission Date");
        if (r.DateAdmitted < new DateOnly(1990, 1, 1) || r.DateAdmitted > today.AddYears(1)) return ("dateAdmitted", "Please Enter a Valid Admission Date");
        if (r.ReserveAmount is null) return ("reserveAmount", "Please Enter Reserve Amount");
        if (r.LgAmount is null) return ("lgAmount", "Please Enter LG Amount");
        if (r.ReserveAmount < 0 || r.ReserveAmount > MaxAmount || r.LgAmount < 0 || r.LgAmount > MaxAmount) return ("reserveAmount", "Amounts must be zero or more.");
        if (payType is not ("C" or "R")) return ("payType", "Please Enter Pay Type");
        if (payType == "C" && string.IsNullOrWhiteSpace(r.FirstDiagnosis?.FirstOrDefault()?.Code)) return ("firstDiagnosis", "Please Enter ICD Code");
        if (r.DateDischarge is { } d && (d < r.DateAdmitted || d > today.AddYears(1))) return ("dateDischarge", "Please Enter a Valid Discharge Date");
        if (string.IsNullOrWhiteSpace(r.TopUpLimit) || !YesNo(r.TopUpLimit)) return ("topUpLimit", "Please Enter Topup Limit Indicator");
        if (r.IntExtTakeOver == "I" && string.IsNullOrWhiteSpace(r.TakeOverPlan)) return ("takeOverPlan", "Please Enter Takeover Plan");
        if (payType == "C" && r.PolicyYear is null) return ("policyYear", "Please Enter Policy Year");
        if (string.IsNullOrWhiteSpace(r.PatientAge)) return ("patientAge", "Please Enter Patient's Age");
        if (r.PatientAge.Trim().Length > 2 || !r.PatientAge.Trim().All(char.IsDigit)) return ("patientAge", "The patient's age must be one or two digits.");
        if (r.AgeUnit is not ("W" or "M" or "Y")) return ("ageUnit", "Please Select Week/Month/Year");
        if (payType == "R" && string.IsNullOrWhiteSpace(r.EspRefNo)) return ("espRefNo", "Please enter ESP Ref. No");
        if (Bad(r.EspRefNo, 20) || Bad(r.LinkCaseNo, 15) || Bad(r.ReferredBy, 100) || Bad(r.AdmissionReason, 500) || Bad(r.TakeOverPlan, 4) || Bad(r.HospitalName, 100) || Bad(r.FirstCall, 10) || Bad(r.FirstTreated, 10) || Bad(r.LgTime, 5))
            return ("text", "A text field is too long or has invalid characters.");
        if (!YesNo(r.Pending) || !YesNo(r.Query) || !YesNo(r.TakeOverInd) || !YesNo(r.PayPending) || !YesNo(r.FollowUp) || !YesNo(r.ConditionWillRecur) || !YesNo(r.FollowUpRequired)) return ("indicators", "An indicator must be Y or N.");
        if (r.IntExtTakeOver is not (null or "" or "I" or "E")) return ("intExtTakeOver", "Take over must be Internal or External.");
        if (!string.IsNullOrEmpty(r.LgTime) && !System.Text.RegularExpressions.Regex.IsMatch(r.LgTime, @"^([01]\d|2[0-3]):[0-5]\d$")) return ("lgTime", "Please enter the LG time as hh:mm.");
        if (r.TakeOverAmount is < 0 or > MaxAmount) return ("takeOverAmount", "The take over amount must be zero or more.");
        if (r.Remarks is not null && (r.Remarks.Count > 5 || r.Remarks.Any(x => (x?.Length ?? 0) > 3900))) return ("remarks", "Remarks are limited to 3,900 characters each.");
        if (Repudiating.Contains(claim))
        {
            var codes = (r.RepudiationCodes ?? []).Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).ToList();
            if (codes.Count == 0) return ("repudiationCodes", "Please Enter Repudiate Code");
            if (codes.Count > 3 || codes.Any(c => c.Length > 10 || c.Any(char.IsControl)) || codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count) return ("repudiationCodes", "Up to three different repudiation codes are allowed.");
            if (string.IsNullOrWhiteSpace(r.Document) || string.IsNullOrWhiteSpace(r.Personnel) || string.IsNullOrWhiteSpace(r.Attention)) return ("document", "Please Enter Repudiation Selection Reason");
            if (Bad(r.Document, 5) || Bad(r.Personnel, 5) || Bad(r.Attention, 5)) return ("document", "A repudiation selection code is invalid.");
        }
        if (r.DeductionAmount is < 0 or > MaxAmount) return ("deductionAmount", "The deduction amount must be zero or more.");
        if ((r.AttendingDoctors?.Count ?? 0) > 5 || (r.Anaesthetists?.Count ?? 0) > 3) return ("doctors", "At most five attending doctors and three anaesthetists are allowed.");
        if (r.AttendingDoctors?.Select((n, i) => (n ?? "").Length > (i == 0 ? 100 : 30) || (n?.Any(char.IsControl) ?? false)).Any(x => x) == true || r.Anaesthetists?.Any(n => (n ?? "").Length > 30 || (n?.Any(char.IsControl) ?? false)) == true) return ("doctors", "A doctor name is too long (30 characters; the first attending doctor 100).");
        foreach (var (name, list, max) in new[] { ("procedures", r.Procedures, 500), ("nonSurgicalProcedures", r.NonSurgicalProcedures, 200) })
            if (list is not null && (list.Count > 6 || list.Any(p => (p.Text?.Length ?? 0) > max || (p.CptCode?.Trim().Length ?? 0) > 6 || (p.Text?.Any(char.IsControl) ?? false) || (p.CptCode?.Any(char.IsControl) ?? false)))) return (name, "At most six procedures; text up to " + max + " characters and a CPT code of up to 6 characters.");
        if (r.Investigations is not null && (r.Investigations.Count > 3 || r.Investigations.Any(i => (i.Done?.Length ?? 0) > 500 || (i.Code?.Trim().Length ?? 0) > 10 || (i.Done?.Any(char.IsControl) ?? false)))) return ("investigations", "At most three investigations (text up to 500, code up to 10 characters).");
        if (r.MedicalManagement is not null && (r.MedicalManagement.Count > 3 || r.MedicalManagement.Any(m => (m?.Length ?? 0) > 500 || (m?.Any(char.IsControl) ?? false)))) return ("medicalManagement", "At most three medical management entries of up to 500 characters.");
        if (r.FirstDocDate is { } fd && fd < new DateOnly(1900, 1, 1) || r.SubsequentDocDate is { } sd && sd < new DateOnly(1900, 1, 1)) return ("firstDocDate", "A doctor date is invalid.");
        foreach (var (name, list) in new[] { ("firstDiagnosis", r.FirstDiagnosis), ("finalDiagnosis", r.FinalDiagnosis), ("underlyingDiagnosis", r.UnderlyingDiagnosis) })
        {
            if (list is null) continue;
            if (list.Count > 3) return (name, "At most three diagnoses are allowed.");
            if (list.Any(x => (x.Code?.Trim().Length ?? 0) > 5 || (x.Text?.Length ?? 0) > 150 || (x.Text?.Any(char.IsControl) ?? false) || x.Date is { } dd && dd < new DateOnly(1900, 1, 1))) return (name, "A diagnosis is invalid (code up to 5 characters, text up to 150).");
        }
        return null;
    }
}

public interface ICaseRegistrationRepository
{
    Task<CaseRegistrationOptions> OptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CaseOption>> HospitalsAsync(string? search, CancellationToken ct = default);

    /// <summary>The policy, its patients (principal 00 and covered persons) and the legacy checks (cancelled, not yet activated, limit exceeded; warnings for a recent policy, low limit, termination).</summary>
    Task<CaseMemberView?> MemberAsync(string memberNumber, CancellationToken ct = default);

    Task<CaseMemberExtras?> ExtrasAsync(string memberNumber, CancellationToken ct = default);

    /// <summary>
    /// Registers a new case: reserves the next case number of the payor (CAQ), then writes CAS, the remarks, the first, final and underlying diagnoses, the CASOthers rows, the repudiation (for repudiated
    /// claimability) and the limit deduction (member available limit) in one transaction, and the cross reference in the maintenance database. Repudiated claimability, deduction amounts and the letters are not part of this slice.
    /// </summary>
    Task<CaseRegistrationResult> RegisterAsync(CaseRegistrationRequest request, string user, CancellationToken ct = default);

    /// <summary>
    /// Issues a guarantee letter for an open case: checks the amount against the balance of the annual limit (annual limit minus claims bordereaux amount, by supplementary limit type), takes the next LG number of the case (LGSeq),
    /// writes CASLG in the maintenance database. Lifetime-limit plans and top-up policies are refused because their limit logic is not migrated.
    /// </summary>
    Task<CaseLgResult> IssueLgAsync(string caseNumber, CaseLgRequest request, string user, CancellationToken ct = default);

    /// <summary>The data of a case letter of the given kind (see CaseLetterKinds), or null when the case does not exist.</summary>
    Task<CaseLetterData?> LetterAsync(string caseNumber, string kind, string user, CancellationToken ct = default, int? lgNo = null);

    /// <summary>A letter printed from the form (see CaseLetterKinds.Drafts), with the legacy checks: member, patient, hospital and admission date are needed.</summary>
    Task<CaseLetterData> DraftLetterAsync(string kind, CaseDraftLetterRequest request, string user, CancellationToken ct = default);
}





