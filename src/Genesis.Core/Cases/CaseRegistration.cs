namespace Genesis.Core.Cases;

public sealed record CaseOption(string Code, string Name);

public sealed record CaseRegistrationOptions(
    IReadOnlyList<CaseOption> Claimability, IReadOnlyList<CaseOption> Hospitals, IReadOnlyList<CaseOption> PayTypes, IReadOnlyList<CaseOption> StayTypes, IReadOnlyList<CaseOption> Modalities,
    IReadOnlyList<CaseOption> Natures, IReadOnlyList<CaseOption> SpecialApprovals, IReadOnlyList<CaseOption> Icd);

public sealed record CasePatient(string CoverId, string Name, string? IcBcPp, string Relation, DateOnly? BirthDate);

/// <summary>The policy a case is registered against, with the checks the legacy form makes when the member number is shown.</summary>
public sealed record CaseMemberView(
    string MemberNumber, string Name, string? IcBcPp, string? PolicyNo, string? Health, string? Insured, string? Plan, string? Payor, string? Status, DateOnly? BirthDate, DateOnly? EffectiveDate, DateOnly? ExpiryDate,
    decimal? AvailableLimit, bool TopUp, bool Terminated, string? PlanMessage, IReadOnlyList<CasePatient> Patients, string? BlockedReason, IReadOnlyList<string> Warnings);

public sealed record CaseDiagnosisInput(string? Code, string? Text, DateOnly? Date);

public sealed record CaseRegistrationRequest(
    string? MemberNumber, string? PatientId, string? Claimability, string? HospitalCode, string? HospitalName, DateOnly? DateAdmitted, DateOnly? DateDischarge, string? Pending, decimal? ReserveAmount, decimal? LgAmount,
    string? LgTime, string? PayType, string? StayType, string? Modality, string? Nature, string? SpecialApproval, string? TopUpLimit, string? Query, string? LinkCaseNo, string? ReferredBy, string? AdmissionReason,
    DateOnly? DocReceivedDate, int? PolicyYear, string? EspRefNo, string? TakeOverInd, decimal? TakeOverAmount, string? TakeOverPlan, string? IntExtTakeOver, string? PatientAge, string? AgeUnit,
    string? FirstCall, string? FirstTreated, string? PayPending, string? FollowUp, string? ConditionWillRecur, string? FollowUpRequired, IReadOnlyList<string>? Remarks,
    IReadOnlyList<CaseDiagnosisInput>? FirstDiagnosis, IReadOnlyList<CaseDiagnosisInput>? FinalDiagnosis, bool AcknowledgeLongStay, bool AcknowledgeRecentPolicy, bool AcknowledgeOpenCase);

public sealed record CaseRegistrationResult(string CaseNumber, string MemberNumber, string PatientId, string Message);

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
        if (Repudiating.Contains(claim)) return ("claimability", "Repudiated claimability statuses need the repudiation selection, which is not migrated yet; use the legacy application for this case.");
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
        foreach (var (name, list) in new[] { ("firstDiagnosis", r.FirstDiagnosis), ("finalDiagnosis", r.FinalDiagnosis) })
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

    /// <summary>
    /// Registers a new case: reserves the next case number of the payor (CAQ), then writes CAS, the remarks, the first and final diagnoses and the CASOthers rows in one transaction,
    /// and the cross reference in the maintenance database. Repudiated claimability, deduction amounts and the letters are not part of this slice.
    /// </summary>
    Task<CaseRegistrationResult> RegisterAsync(CaseRegistrationRequest request, string user, CancellationToken ct = default);
}
