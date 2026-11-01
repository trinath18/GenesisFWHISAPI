using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class McoCorrectionRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoCorrectionRepository
{
    private const int Max = 2000;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d ? DateOnly.FromDateTime(d) : null;

    private const string Where = """
        m.PAYCode = @payor AND (@memFrom IS NULL OR m.MBMNumber >= @memFrom) AND (@memTo IS NULL OR m.MBMNumber <= @memTo)
        AND (@bordxFrom IS NULL OR m.MBMBordxDate >= @bordxFrom) AND (@bordxTo IS NULL OR m.MBMBordxDate < DATEADD(day, 1, @bordxTo))
        AND (@batchFrom IS NULL OR m.MBMBatchNo >= @batchFrom) AND (@batchTo IS NULL OR m.MBMBatchNo <= @batchTo)
        """;

    private static McoCorrectionRow Map(IDictionary<string, object?> r) => new(S(r, "MemberNumber")?.Trim() ?? "", S(r, "CoverId")?.Trim() ?? "", S(r, "Name")?.Trim(), Day(r, "BordxDate"), I(r, "BatchNo"),
        Blank(S(r, "InvoiceNo")), Day(r, "InvoiceDate"), Dec(r, "Mco"), Day(r, "PayorEff"), Day(r, "PayorExp"));

    public async Task<McoCorrectionListing> SearchAsync(McoManualFilter f, CancellationToken ct = default)
    {
        if (McoManualRules.ValidateFilter(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var args = new { payor = f.Payor!.Trim(), memFrom = Blank(f.MemberFrom), memTo = Blank(f.MemberTo), bordxFrom = At(f.BordxFrom), bordxTo = At(f.BordxTo), batchFrom = f.BatchFrom, batchTo = f.BatchTo };
        var principals = await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max + 1}) m.MBMNumber AS MemberNumber, m.MBMCOVID AS CoverId, m.MBMName AS Name, m.MBMBordxDate AS BordxDate, m.MBMBatchNo AS BatchNo, o.MBMInvoiceNo AS InvoiceNo, o.MBMInvoiceDate AS InvoiceDate,
                   o.MBMMCOFee AS Mco, m.MBMPayorEffDate AS PayorEff, m.MBMPayorExpDate AS PayorExp
            FROM MBM m LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber WHERE {Where} ORDER BY m.MBMNumber
            """, args, ct);
        var covered = await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max + 1}) c.MBMCNumber AS MemberNumber, c.MBMCCoverID AS CoverId, c.MBMCName AS Name, c.MBMCBordxDate AS BordxDate, c.MBMCBatchNo AS BatchNo, c.MBMInvoiceNo AS InvoiceNo, c.MBMInvoiceDate AS InvoiceDate,
                   c.MBMCMCO AS Mco, m.MBMPayorEffDate AS PayorEff, m.MBMPayorExpDate AS PayorExp
            FROM MBMCoveredPersons c JOIN MBM m ON m.MBMNumber = c.MBMCNumber WHERE {Where} ORDER BY c.MBMCNumber, c.MBMCCoverID
            """, args, ct);
        var rows = principals.Concat(covered).Select(Map).OrderBy(r => r.MemberNumber, StringComparer.Ordinal).ThenBy(r => r.CoverId, StringComparer.Ordinal).ToList();
        var truncated = principals.Count() > Max || covered.Count() > Max || rows.Count > Max;
        return new McoCorrectionListing(rows.Take(Max).ToList(), truncated);
    }

    public async Task<McoCorrectionResult> UpdateAsync(McoCorrectionRequest r, string user, CancellationToken ct = default)
    {
        if (McoCorrectionRules.Validate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var fee = Math.Round(r.Fee!.Value, 4);
        var changes = new List<object>();
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            foreach (var item in r.Items!.Select(i => (Number: i.MemberNumber!.Trim(), Cover: i.CoverId!.Trim())))
            {
                var principal = item.Cover == "00";
                var row = (await s.QueryAsync(principal
                    ? "SELECT MBMInvoiceNo AS InvoiceNo, MBMMCOFee AS Mco FROM MBMOthers WHERE MBMNumber = @n"
                    : "SELECT MBMInvoiceNo AS InvoiceNo, MBMCMCO AS Mco FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = @c", new { n = item.Number, c = item.Cover }, ct)).FirstOrDefault()
                    ?? throw BusinessRuleException.NotFound($"{item.Number} / {item.Cover} does not exist.");
                if (Blank(S(row, "InvoiceNo")) is { } inv) throw BusinessRuleException.Conflict($"Invoice has been generated! {item.Number} / {item.Cover} is on invoice {inv}; nothing was changed.");
                await s.ExecuteAsync(principal ? "UPDATE MBMOthers SET MBMMCOFee = @fee WHERE MBMNumber = @n" : "UPDATE MBMCoveredPersons SET MBMCMCO = @fee WHERE MBMCNumber = @n AND MBMCCoverID = @c", new { fee, n = item.Number, c = item.Cover }, ct);
                changes.Add(new { member = item.Number, cover = item.Cover, before = Dec(row, "Mco"), after = fee });
            }
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Mco.Correction", "MBMOthers", $"{changes.Count} row(s)", user, new Dictionary<string, object?> { ["fee"] = fee, ["changes"] = changes }));
        return new McoCorrectionResult(changes.Count, fee);
    }
}
