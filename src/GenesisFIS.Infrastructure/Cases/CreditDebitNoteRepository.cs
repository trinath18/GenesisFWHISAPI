using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>Worksheet > Generate Credit/Debit Note (legacy frmGenerateCNNote); the note is numbered by the worksheet save's routine (legacy GenerateCreditNo / GenerateDebitNo).</summary>
internal sealed class CreditDebitNoteRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICreditDebitNoteRepository
{
    private const string Columns = "CASNumber, ClaimVersion, CLMTotalActual, CLMBordxAmt, CLMPayableByMedix2H, CLMPayableByMedix2M, DebitCreditRefNo, DebitCreditDate";

    private static ClaimNote ToNote(IDictionary<string, object?> r)
    {
        var note = S(r, "DebitCreditRefNo"); var payM = Dec(r, "CLMPayableByMedix2M") ?? 0;
        return new ClaimNote(S(r, "CASNumber")!, I(r, "ClaimVersion") ?? 0, Dec(r, "CLMTotalActual") ?? 0, Dec(r, "CLMBordxAmt") ?? 0, Dec(r, "CLMPayableByMedix2H") ?? 0, payM,
            note, D(r, "DebitCreditDate") is { } d ? DateOnly.FromDateTime(d) : null, CreditDebitNoteRules.Problem(note, payM));
    }

    public async Task<ClaimNote?> FindAsync(string caseNumber, int version, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Worksheet, $"SELECT {Columns} FROM Claim WHERE CASNumber = @no AND ClaimVersion = @version", new { no = caseNumber.Trim(), version }, ct))
        .Select(ToNote).FirstOrDefault();

    public async Task<ClaimNoteListing> MissingAsync(CancellationToken ct = default)
    {
        var rows = await QueryAsync(GenesisDb.Worksheet, $"""
            SELECT TOP (@take) {Columns} FROM Claim
            WHERE ISNULL(DebitCreditRefNo, '') = '' AND ISNULL(CLMPayableByMedix2M, 0) <> 0
            ORDER BY ClaimCloseDate DESC, CASNumber, ClaimVersion
            """, new { take = CreditDebitNoteRules.MaxMissing + 1 }, ct);
        return new ClaimNoteListing(rows.Take(CreditDebitNoteRules.MaxMissing).Select(ToNote).ToList(), rows.Count > CreditDebitNoteRules.MaxMissing);
    }

    public async Task<ClaimNote> GenerateAsync(string caseNumber, int version, string user, CancellationToken ct = default)
    {
        var no = caseNumber.Trim();
        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var row = (await ws.QueryAsync($"SELECT {Columns} FROM Claim WITH (UPDLOCK) WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound("Record Not Found");
        var claim = ToNote(row);
        if (claim.NoteNo is not null) throw BusinessRuleException.Conflict(claim.Problem!);
        if (claim.Problem is { } problem) throw new BusinessRuleException(problem, field: "claim");
        // Legacy: the payor is the first two letters of the claim number.
        var note = await WorksheetRepository.RaiseNoteAsync(ws, claim.CaseNumber, claim.Version, claim.CaseNumber[..2].ToUpperInvariant(), claim.PayableToMember, ct);
        await ws.CommitAsync(ct);
        audit.Record(new AuditEntry("Claim.CreditDebitNote", "Claim", $"{claim.CaseNumber}-{claim.Version}", user, new Dictionary<string, object?> { ["note"] = note, ["payableToMember"] = claim.PayableToMember }));
        return (await FindAsync(claim.CaseNumber, claim.Version, ct))!;
    }
}
