namespace GenesisFIS.Core.Cases;

/// <summary>
/// Search criteria shared by Case > Send to Claims (Rep) (FrmCSRepSendtoClaim) and Case > CRU Send to Claims (FrmCRUPassOut).
/// Codes keep the legacy combo values: case status 0 Open, 1 Delete, 2 Open &amp; Close (the legacy default); claimability A / R;
/// department stage 0 / 1 (2 = both). Versions only apply to CRU, whose rows are claim versions.
/// </summary>
public sealed record SendToClaimsSearch(
    string? CaseNoFrom, string? CaseNoTo, bool ExactCase, string? CaseStatus, string? Claimability, string? Department, string? PassedBy, int? VersionFrom, int? VersionTo,
    DateOnly? RegFrom, DateOnly? RegTo, DateOnly? AdmFrom, DateOnly? AdmTo, DateOnly? DisFrom, DateOnly? DisTo, DateOnly? DpoFrom, DateOnly? DpoTo,
    bool NoRegDate, bool NoAdmissionDate, bool NoDischargeDate, bool NoDpo);

/// <summary>One row of the legacy list. <see cref="Version"/> is null on the Rep screen (it works per case); <see cref="Batch"/> is null on the CRU screen.</summary>
public sealed record SendToClaimsRow(
    bool Passed, string CaseNumber, int? Version, string? MemberNumber, string? PatientName, DateOnly? Registered, DateOnly? Admitted, DateOnly? Discharged, DateOnly? Dpo, int? Days,
    string? Batch, string? PassedBy, string? OpenedBy, string? Hospital, string? StayType, string? CashType, decimal? Amount, string? Claimability, string? DeptStage,
    string? CannotPass);   // why the legacy form would not let the row be ticked (Rep screen only), or null

public sealed record SendToClaimsResult(IReadOnlyList<SendToClaimsRow> Rows, bool Truncated);

public sealed record SendToClaimsItem(string? CaseNumber, int? Version);

public sealed record SendToClaimsPassResult(int Count, string? Batch, string Message);

public static class SendToClaimsRules
{
    public const int MaxRows = 3000, MaxPass = 500;

    /// <summary>The legacy Rep form refused to tick a case with no registration, admission or discharge date or no hospital, or one already sent.</summary>
    public static string? RepCannotPass(DateOnly? registered, DateOnly? admitted, DateOnly? discharged, string? hospital, bool passed) =>
        registered is null ? "Date of Register is Empty."
        : admitted is null ? "Date of Admitted is Empty."
        : discharged is null ? "Date of Discharge cannot be empty!"
        : string.IsNullOrWhiteSpace(hospital) ? "Hospital Name is Empty."
        : passed ? "File has been send out."
        : null;
}

public interface ICaseSendToClaimsRepository
{
    /// <summary>The "Passed by" list.</summary>
    Task<IReadOnlyList<string>> PassedByAsync(CancellationToken ct = default);
    Task<SendToClaimsResult> SearchAsync(SendToClaimsSearch request, CancellationToken ct = default);
}

/// <summary>Case > Send to Claims (Rep): passes repudiated cases to Claims in a numbered daily batch of the user (CASPASSSeq), through the legacy procedures.</summary>
public interface ICaseSendToClaimsRepRepository : ICaseSendToClaimsRepository
{
    Task<SendToClaimsPassResult> PassAsync(IReadOnlyList<string> caseNumbers, string user, CancellationToken ct = default);
}

/// <summary>Case > CRU Send to Claims: passes claim versions from the CRU to Claims with the "Date Pass Out" the user enters.</summary>
public interface ICaseCruSendToClaimsRepository : ICaseSendToClaimsRepository
{
    Task<SendToClaimsPassResult> PassAsync(IReadOnlyList<SendToClaimsItem> items, DateOnly passOutDate, string user, CancellationToken ct = default);
}

/// <summary>Case > Claim ASCII: Accepted (by bordx date; New or Reprint; worksheet status 0 New &amp; Amendment or 1 Deleted) or Rejected (by registration date).</summary>
public sealed record ClaimAsciiRequest(string? Payor, DateOnly? From, DateOnly? To, bool Rejected, bool Reprint, bool Deleted);

/// <summary>Case > Case ASCII: cases registered in the date range; endorsement status 0 New, 1 Deleted, 2 Amendment.</summary>
public sealed record CaseAsciiRequest(string? Payor, DateOnly? From, DateOnly? To, bool Reprint, string? Endorsement);

public sealed record AsciiExport(AsciiFile File, string? SavedTo);

public interface ICaseAsciiRepository
{
    Task<IReadOnlyList<CaseOption>> PayorsAsync(CancellationToken ct = default);
    Task<AsciiExport> ClaimAsync(ClaimAsciiRequest request, string user, CancellationToken ct = default);
    Task<AsciiExport> CaseAsync(CaseAsciiRequest request, string user, CancellationToken ct = default);
}
