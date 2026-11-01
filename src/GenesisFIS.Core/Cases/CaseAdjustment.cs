namespace GenesisFIS.Core.Cases;

/// <summary>A registered case as the Case Adjustment form shows it: the same fields as Case Registration, filled from the saved case.</summary>
public sealed record CaseAdjustmentView(
    string CaseNumber, string Status, CaseMemberView? Member, string PatientName, CaseRegistrationRequest Form, string? HospitalName, string? LastUpdatedBy, DateOnly? LastUpdated, DateOnly? Registered, string? OpenedBy);

/// <summary>The edited case. Member and patient come from the saved case; the status is O (open), C (closed) or D (deleted).</summary>
public sealed record CaseAdjustmentRequest(string? Status, CaseRegistrationRequest? Form);

public static class CaseAdjustmentRules
{
    public static (string Field, string Message)? Validate(CaseAdjustmentRequest r, string memberNumber, string patientId, DateOnly today)
    {
        if (r.Form is null) return ("form", "Please enter the case details.");
        if (r.Status is not ("O" or "C" or "D")) return ("status", "Please Select Status!");
        return CaseRegistrationRules.Validate(r.Form with { MemberNumber = memberNumber, PatientId = patientId }, today);
    }
}

public interface ICaseAdjustmentRepository
{
    Task<CaseAdjustmentView?> GetAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>The next (or previous) case number in case-number order, or null at either end (legacy Next / Pre buttons).</summary>
    Task<string?> NeighbourAsync(string caseNumber, bool next, CancellationToken ct = default);

    /// <summary>
    /// Saves the edited case like the legacy Update: CAS (status, claimability, dates, amounts, flags), hospital, remarks, first / final / underlying diagnoses, CASOthers and CASOthersTwo, the repudiation
    /// (for repudiated claimability) and the cross reference in the maintenance database. Member, patient, case opener, registration date, policy year and the member limit are not changed.
    /// </summary>
    Task UpdateAsync(string caseNumber, CaseAdjustmentRequest request, string user, CancellationToken ct = default);
}
