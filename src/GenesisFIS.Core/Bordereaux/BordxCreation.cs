namespace GenesisFIS.Core.Bordereaux;

/// <summary>
/// Claim Bordx > Worksheet > Insert Worksheet (legacy FrmWksBordx, USRAccess 30): closed claims not yet on a bordereau (Claim.ClaimStatus 'C',
/// ClaimBordxStatus 0, legacy VIEW_WS_PendingBordx2) are put on a new bordereau, one for all the ticked claims or one per claim. The bordereau number is
/// the payor's health type + payor + its next CLAIM_BordxSEQ number (legacy GenerateCLMBordxNo), e.g. NLP02527.
/// </summary>
public sealed record PendingBordxSearch(string? ClaimNo = null, int? Version = null, DateOnly? AdmittedFrom = null, DateOnly? AdmittedTo = null, DateOnly? DischargedFrom = null,
    DateOnly? DischargedTo = null, bool NoAdmitDate = false, bool NoDischargeDate = false);

/// <summary>A claim waiting for a bordereau, with what the legacy tick checks look at (<see cref="Problem"/> says why it cannot be ticked).</summary>
public sealed record PendingBordxClaim(
    string CaseNumber, int Version, string? Payor, string? PayTo, decimal PayableToHospital, decimal TotalApproved, decimal BordxAmount, DateOnly? Admitted, DateOnly? Discharged,
    string? Patient, string? Hospital, string? GroupCompany, string? Plan, string? Icd, string Btbg, string? Claimability, DateOnly? ClaimDate, string? Problem);

public sealed record PendingBordxListing(IReadOnlyList<PendingBordxClaim> Claims, decimal TotalApproved, bool Truncated);

/// <summary>Multiple: one bordereau for all the ticked claims (the legacy default); Single: one bordereau per claim.</summary>
public enum BordxOption { Multiple, Single }

public sealed record BordxClaimKey(string CaseNumber, int Version);

public sealed record CreateBordxRequest(BordxOption Option, IReadOnlyList<BordxClaimKey> Claims);

/// <summary>A claim as put on a bordereau (legacy lstBordxListing: claim, bordereau no, date, amount).</summary>
public sealed record CreatedBordxClaim(string CaseNumber, int Version, string BordxNo, DateOnly BordxDate, decimal Amount);

public sealed record CreatedBordx(string BordxNo, string Payor, string Status, int Claims, decimal Amount);

public sealed record CreateBordxResult(IReadOnlyList<CreatedBordx> Bordereaux, IReadOnlyList<CreatedBordxClaim> Claims);

/// <summary>
/// Claim Bordx > Worksheet > Remove Worksheet (legacy FrmBordxRevise "Bordereaux Revise", USRAccess 31): a bordereau's claims, any of which can be taken off
/// it before it is sent; a removed claim goes back to the Insert Worksheet list.
/// </summary>
public sealed record BordxReviseClaim(
    string CaseNumber, int Version, DateOnly? OpenDate, string? MemberNumber, string? Patient, DateOnly? Admitted, DateOnly? Discharged, decimal TotalApproved, string? CaseStatus,
    decimal BordxAmount);

/// <summary>The bordereau and its claims; <see cref="Sent"/> (sent to the payor or MNRB) stops claims being removed.</summary>
public sealed record BordxRevise(string BordxNo, DateOnly? BordxDate, DateOnly? PrintedDate, decimal Amount, int NoOfClaims, bool Sent, IReadOnlyList<BordxReviseClaim> Claims);

public sealed record RemoveFromBordxRequest(IReadOnlyList<BordxClaimKey> Claims);

public static class BordxRules
{
    public const int MaxClaims = 5000;

    /// <summary>
    /// The claim's amount on the bordereau (legacy CmdBordx_Click): for a BTBG member what the claim pays the hospital or the member, otherwise the total
    /// approved; plus the case management fees.
    /// </summary>
    public static decimal Amount(string? btbg, string? payTo, decimal payableToHospital, decimal payableToMember, decimal totalApproved, decimal caseMgmtFees) =>
        (btbg == "Y" && payTo == "H" ? payableToHospital : btbg == "Y" && payTo == "M" ? payableToMember : totalApproved) + caseMgmtFees;

    /// <summary>The legacy lstWorksheetTrackList_ItemCheck checks, in its order; the scanned-document check comes last (<see cref="ScanProblem"/>).</summary>
    public static string? Problem(DateOnly? discharged, string? hospital, string? icd, string? claimability)
    {
        if (discharged is null) return "Date of Discharge is Empty.";
        if (string.IsNullOrWhiteSpace(hospital)) return "Hospital cannot be empty!";
        if (string.IsNullOrWhiteSpace(icd)) return "Final ICD Code cannot be empty!";
        if (claimability?.Trim() is "NN" or "N") return "Claimability is Not Decided!";
        return null;
    }

    public const string NoScannedDocuments = "Please ensure scanned documents are in system!";

    /// <summary>
    /// Legacy addTheFiles / LoadDoc: a claim can only go on a bordereau when its case folder (DocPath\FWScannedDoc\&lt;case&gt;) holds a scanned image
    /// (legacy .jpg / .tif / .gif; here also .jpeg, .tiff, .png and .pdf). Only checked where the scanned-documents store is configured.
    /// </summary>
    public static string? ScanProblem(IEnumerable<string> fileNames) =>
        fileNames.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".gif" or ".png" or ".pdf") ? null : NoScannedDocuments;

    /// <summary>Whether the bordereau went to the payor or MNRB (Bordx.BordxPAYToStatus / BordxMNRBToStatus set; "0" or empty when not).</summary>
    public static bool Sent(string? payToStatus, string? mnrbToStatus) =>
        payToStatus?.Trim() is { Length: > 0 } p && p != "0" || mnrbToStatus?.Trim() is { Length: > 0 } m && m != "0";

    /// <summary>Bordereau status: C (claims paid to the hospital) or R (paid to the member), from the claims' invoice pay-to (legacy SaveCLMBordx).</summary>
    public static string Status(string? payTo) => payTo == "H" ? "C" : "R";

    /// <summary>Legacy GenerateCLMBordxNo: health type + payor + the sequence number as 5 digits.</summary>
    public static string BordxNo(string healthType, string payor, int sequence) => $"{healthType.Trim()}{payor.Trim()}{sequence:00000}";

    /// <summary>The case remark line the legacy save appended ("dd/mm/yyyy hh:mm:ss         BORDX DONE BY user -  NLP02527 ... user"), "~" separated.</summary>
    public static string CaseRemark(DateTime now, string user, string bordxNo) =>
        $"~{now:dd/MM/yyyy HH:mm:ss}         BORDX DONE BY {user} -  {bordxNo}{new string(' ', 44)}{user} ";
}

public interface IBordxCreationRepository
{
    Task<PendingBordxListing> PendingAsync(PendingBordxSearch search, CancellationToken ct = default);
    Task<CreateBordxResult> CreateAsync(CreateBordxRequest request, string user, CancellationToken ct = default);
    Task<BordxRevise?> ReviseAsync(string bordxNo, CancellationToken ct = default);
    Task<BordxRevise> RemoveAsync(string bordxNo, RemoveFromBordxRequest request, string user, CancellationToken ct = default);
}
