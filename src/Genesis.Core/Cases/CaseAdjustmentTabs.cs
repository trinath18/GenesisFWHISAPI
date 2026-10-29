namespace Genesis.Core.Cases;

public sealed record CaseLgItem(string LgNumber, int LgNo, string Letter, DateOnly? Date, decimal? Amount, string PatientId, string? PatientName, DateOnly? Admitted, string? Hospital, string? IssuedBy, string? Remark, string? UserTime, string? SystemTime);

public sealed record CaseLgRemarkRequest(string? Remark);

public sealed record CaseWorksheetItem(
    int Version, decimal? TotalApproved, decimal? TotalActual, decimal? PayableToHospital, decimal? PayableToMember, string? BordxRefNo, DateOnly? BordxDate, decimal? BordxAmount, string? PayorApprovalStatus, DateOnly? PayorApprovalDate,
    string? PayorApprovalCode, string? BordxReversalIndicator, string? CartonNo, DateOnly? SentDate, string? DebitCreditRef, DateOnly? DebitCreditDate, string? ReversalBordxStatus);

/// <summary>The payor approval of one worksheet (claim) version, as the legacy Worksheet tab updates it.</summary>
public sealed record CaseWorksheetApprovalRequest(DateOnly? ApprovalDate, string? ApprovalCode, string? ReversalIndicator, string? ReversalStatus);

public static class CaseWorksheetRules
{
    public static (string Field, string Message)? Validate(CaseWorksheetApprovalRequest r, decimal bordxAmount)
    {
        if (r.ApprovalDate is null) return ("approvalDate", "Please Enter Approval Date");
        if (r.ApprovalDate < new DateOnly(1990, 1, 1) || r.ApprovalDate > DateOnly.FromDateTime(DateTime.Today).AddYears(1)) return ("approvalDate", "Please Enter a Valid Approval Date");
        var code = r.ApprovalCode?.Trim() ?? "";
        if (code.Length > 0)
        {
            if (code.Any(char.IsControl)) return ("approvalCode", "Please check approval code!");
            if (bordxAmount > 0 && !code.StartsWith("CS", StringComparison.Ordinal)) return ("approvalCode", "Please check approval code!");
            if (bordxAmount < 0 && !code.StartsWith("CR", StringComparison.Ordinal)) return ("approvalCode", "Please check approval code!");
            if (code.Length < 12) return ("approvalCode", "Approval Code must be 12 digits");
            if (code.Length > 20) return ("approvalCode", "The approval code is too long.");
        }
        if ((r.ReversalIndicator?.Length ?? 0) > 1 || (r.ReversalStatus?.Length ?? 0) > 1) return ("reversal", "A reversal code is one character.");
        return null;
    }
}

public interface ICaseAdjustmentTabsRepository
{
    Task<IReadOnlyList<CaseLgItem>> LgListAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Changes the remark of one guarantee letter (CASLG.LGRemark). Returns false when the LG does not exist.</summary>
    Task<bool> UpdateLgRemarkAsync(string caseNumber, int lgNo, string? remark, string user, CancellationToken ct = default);

    Task<IReadOnlyList<CaseWorksheetItem>> WorksheetsAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Marks the payor approval of one worksheet version (status Y, date, 12-digit code starting CS for payments and CR for credits, reversal flags). Returns false when the version does not exist.</summary>
    Task<bool> UpdateWorksheetApprovalAsync(string caseNumber, int version, CaseWorksheetApprovalRequest request, string user, CancellationToken ct = default);
}
