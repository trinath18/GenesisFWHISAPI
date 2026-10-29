namespace Genesis.Core.Cases;

/// <summary>A member (principal 00 or a covered person) as the transfer screens show them.</summary>
public sealed record CaseTransferParty(string MemberNumber, string CoverId, string? PolicyNo, string? Status, string? IcNo, string? Name, DateOnly? EffectiveDate, DateOnly? ExpiryDate);

public sealed record CaseTransferRequest(string? MemberNumber, string? CoverId);

public sealed record CaseTransferResult(string CaseNumber, string NewCaseNumber, string Message, bool TemporaryMemberRemoved);

public static class CaseTransferRules
{
    public static (string Field, string Message)? Validate(string caseNumber, CaseTransferRequest r, CaseTransferParty? target, CaseTransferParty current, bool temporary)
    {
        var member = r.MemberNumber?.Trim() ?? "";
        var cover = r.CoverId?.Trim() ?? "";
        if (member.Length == 0) return ("memberNumber", "Please Keyin transfer Member No. !");
        if (cover.Length == 0) return ("coverId", "Please Keyin transfer Cover ID !");
        if (member.Length > 15 || cover.Length != 2 || member.Any(char.IsControl)) return ("memberNumber", "Invalid member number or cover ID.");
        if (temporary && member.StartsWith('Z')) return ("memberNumber", "Cannot transfer to the Temporary Membership !");
        if (!temporary && member.Length >= 2 && caseNumber.Length >= 2 && !string.Equals(member[..2], caseNumber[..2], StringComparison.OrdinalIgnoreCase)) return ("memberNumber", "Different Payor Code is Not Transferable !");
        if (target is null || string.IsNullOrWhiteSpace(target.PolicyNo)) return ("memberNumber", "Member No not found !");
        if (string.IsNullOrWhiteSpace(target.Name)) return ("coverId", "Patient not found !");
        if (target.Status == "D") return ("memberNumber", "Member status - 'DELETE' !");
        if (string.Equals(current.MemberNumber, target.MemberNumber, StringComparison.OrdinalIgnoreCase) && current.CoverId == target.CoverId) return ("memberNumber", "Same Membership ! No transformation !");
        return null;
    }
}

public interface ICaseTransferRepository
{
    /// <summary>The member and patient the case is registered under (the Go button). For temporary transfers it also checks the case is a temporary one (Z...), under a temporary member, not bordx'd and alone under the member.</summary>
    Task<CaseTransferParty?> CurrentAsync(string caseNumber, bool temporary, CancellationToken ct = default);

    /// <summary>A transfer target by member number and cover ID (00 for the principal).</summary>
    Task<CaseTransferParty?> PartyAsync(string memberNumber, string coverId, CancellationToken ct = default);

    /// <summary>
    /// Case Transfer: moves the case to another member / patient of the same payor, appends a TransRemarks line, gives the claims' approved amount back to the old member's limits and takes it from the new member's
    /// (annual limit, covered-person limit and cancer / kidney / home nursing benefit balances).
    /// </summary>
    Task<CaseTransferResult> TransferAsync(string caseNumber, CaseTransferRequest request, string user, CancellationToken ct = default);

    /// <summary>
    /// Temp Case Transfer: gives a temporary case (Z...) of a temporary member a real case number of the target member's payor, renames the case in every case, claim and guarantee table, moves the limits
    /// and removes the temporary membership when it has no other case.
    /// </summary>
    Task<CaseTransferResult> TempTransferAsync(string caseNumber, CaseTransferRequest request, string user, CancellationToken ct = default);
}

public sealed record CasePayClaimRecord(
    DateOnly? Date, bool Lab, bool ConfInfo, bool InfoInv, bool CovgSub, bool NonPan, bool NonCash, bool OthersChecked, string? Others, string? RegisteredBy, DateOnly? Registered, string? UpdatedBy, DateOnly? Updated);

public sealed record CasePayClaimView(string CaseNumber, string MemberNumber, string PrincipalName, string? PolicyNo, string PatientName, DateOnly? Admitted, DateOnly? Discharged, CasePayClaimRecord? Record);

public sealed record CasePayClaimRequest(DateOnly? Date, bool Lab, bool ConfInfo, bool InfoInv, bool CovgSub, bool NonPan, bool NonCash, bool OthersChecked, string? Others);

public static class CasePayClaimRules
{
    public static (string Field, string Message)? Validate(CasePayClaimRequest r, DateOnly today)
    {
        if (r.Date is null) return ("date", "Please enter Pay&Claim Date");
        if (r.Date < new DateOnly(1990, 1, 1) || r.Date > today.AddYears(1)) return ("date", "Invalid Date Format!");
        if (r.OthersChecked && string.IsNullOrWhiteSpace(r.Others)) return ("others", "Please enter Remarks");
        if ((r.Others?.Length ?? 0) > 50 || (r.Others?.Any(char.IsControl) ?? false)) return ("others", "The remarks can be at most 50 characters.");
        return null;
    }
}

public interface ICasePayClaimRepository
{
    Task<CasePayClaimView?> GetAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Records the Pay &amp; Claim reasons of a case (adds the first time, updates afterwards). Returns whether it was an update.</summary>
    Task<bool> SaveAsync(string caseNumber, CasePayClaimRequest request, string user, CancellationToken ct = default);
}
