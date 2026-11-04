namespace GenesisFIS.Core.Cases;

/// <summary>
/// Worksheet > Generate Credit/Debit Note (legacy frmGenerateCNNote, USRAccess 65): a claim that pays the member (Claim.CLMPayableByMedix2M) gets a credit
/// note (paid to the member, "C" + payor + CRSeq) or a debit note (owed by the member, "D" + payor + DRSeq) when it has none yet. The worksheet save raises
/// the note itself; this screen fills in the claims saved without one.
/// </summary>
public sealed record ClaimNote(
    string CaseNumber, int Version, decimal Actual, decimal Approved, decimal PayableToHospital, decimal PayableToMember, string? NoteNo, DateOnly? NoteDate, string? Problem)
{
    /// <summary>"Credit", "Debit" or null, from the note number or (before one is generated) from what the claim pays the member.</summary>
    public string? Kind => CreditDebitNoteRules.Kind(NoteNo, PayableToMember);
}

public sealed record ClaimNoteListing(IReadOnlyList<ClaimNote> Claims, bool Truncated);

public static class CreditDebitNoteRules
{
    public const int MaxMissing = 1000;

    public static string? Kind(string? noteNo, decimal payableToMember) =>
        noteNo?.Trim() is { Length: > 0 } n ? (n.StartsWith('D') ? "Debit" : n.StartsWith('C') ? "Credit" : null)
        : payableToMember > 0 ? "Credit" : payableToMember < 0 ? "Debit" : null;

    /// <summary>Why no note can be generated (legacy cmdUpdate was enabled only for a claim paying the member without a note), or null.</summary>
    public static string? Problem(string? noteNo, decimal payableToMember) =>
        !string.IsNullOrWhiteSpace(noteNo) ? $"The claim already has note {noteNo.Trim()}."
        : payableToMember == 0 ? "The claim pays nothing to the member: no credit or debit note is needed." : null;
}

public interface ICreditDebitNoteRepository
{
    Task<ClaimNote?> FindAsync(string caseNumber, int version, CancellationToken ct = default);

    /// <summary>The claims paying the member that have no note yet (not in the legacy form: it found them one by one).</summary>
    Task<ClaimNoteListing> MissingAsync(CancellationToken ct = default);

    Task<ClaimNote> GenerateAsync(string caseNumber, int version, string user, CancellationToken ct = default);
}
