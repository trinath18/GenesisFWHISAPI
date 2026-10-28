using Genesis.Core.Data;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class McoManualInvoiceRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoManualInvoiceRepository
{
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private const string Principals = """
        SELECT TOP ({0}) m.MBMNumber, m.MBMCOVID AS CoverId, m.MBMBordxDate AS BordxDate, m.MBMBatchNo AS BatchNo, ISNULL(o.MBMMCOFee, 0) AS Mco
        FROM MBM m JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
        WHERE (o.MBMInvoiceNo IS NULL OR o.MBMInvoiceNo = '') AND m.PAYCode = @payor
          AND (@memFrom IS NULL OR m.MBMNumber >= @memFrom) AND (@memTo IS NULL OR m.MBMNumber <= @memTo)
          AND (@bordxFrom IS NULL OR m.MBMBordxDate >= @bordxFrom) AND (@bordxTo IS NULL OR m.MBMBordxDate < DATEADD(day, 1, @bordxTo))
          AND (@batchFrom IS NULL OR m.MBMBatchNo >= @batchFrom) AND (@batchTo IS NULL OR m.MBMBatchNo <= @batchTo)
        """;

    private const string Covered = """
        SELECT TOP ({0}) c.MBMCNumber AS MBMNumber, c.MBMCCoverID AS CoverId, c.MBMCBordxDate AS BordxDate, c.MBMCBatchNo AS BatchNo, ISNULL(c.MBMCMCO, 0) AS Mco
        FROM MBMCoveredPersons c
        WHERE (c.MBMInvoiceNo IS NULL OR c.MBMInvoiceNo = '') AND SUBSTRING(c.MBMCNumber, 1, 2) = @payor
          AND (@memFrom IS NULL OR c.MBMCNumber >= @memFrom) AND (@memTo IS NULL OR c.MBMCNumber <= @memTo)
          AND (@bordxFrom IS NULL OR c.MBMCBordxDate >= @bordxFrom) AND (@bordxTo IS NULL OR c.MBMCBordxDate < DATEADD(day, 1, @bordxTo))
          AND (@batchFrom IS NULL OR c.MBMCBatchNo >= @batchFrom) AND (@batchTo IS NULL OR c.MBMCBatchNo <= @batchTo)
        """;

    private static object Args(McoManualFilter f) => new { payor = f.Payor!.Trim(), memFrom = Blank(f.MemberFrom), memTo = Blank(f.MemberTo), bordxFrom = At(f.BordxFrom), bordxTo = At(f.BordxTo), batchFrom = f.BatchFrom, batchTo = f.BatchTo };

    private static McoManualListing Build(IEnumerable<IDictionary<string, object?>> data, int max)
    {
        var rows = data.Select(r => new McoManualRow(S(r, "MBMNumber")?.Trim() ?? "", S(r, "CoverId")?.Trim() ?? "", D(r, "BordxDate") is { } d ? DateOnly.FromDateTime(d) : null, I(r, "BatchNo"), Dec(r, "Mco") ?? 0))
            .OrderBy(r => r.MemberNumber, StringComparer.Ordinal).ThenBy(r => r.CoverId, StringComparer.Ordinal).ToList();
        var truncated = rows.Count > max; if (truncated) rows = rows.Take(max).ToList();
        return new McoManualListing(rows, rows.Sum(r => r.Mco), rows.Any(r => r.Mco == 0), truncated);
    }

    public async Task<McoManualListing> ListAsync(McoManualFilter f, CancellationToken ct = default)
    {
        if (McoManualRules.ValidateFilter(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var max = McoManualRules.MaxRows; var a = Args(f);
        var data = (await QueryAsync(GenesisDb.Main, string.Format(Principals, max + 1), a, ct)).Concat(await QueryAsync(GenesisDb.Main, string.Format(Covered, max + 1), a, ct));
        return Build(data, max);
    }

    public async Task<McoManualUpdateResult> UpdateAsync(McoManualUpdateRequest r, string user, CancellationToken ct = default)
    {
        if (McoManualRules.ValidateUpdate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var f = r.Filter!; var max = McoManualRules.MaxRows; var a = Args(f);
        var (invoiceNo, invoiceDate) = (r.InvoiceNo!.Trim(), At(r.InvoiceDate)!.Value);
        int updated;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var found = Build((await s.QueryAsync(string.Format(Principals, max + 1), a, ct)).Concat(await s.QueryAsync(string.Format(Covered, max + 1), a, ct)), max);
            if (found.Rows.Count == 0) throw new BusinessRuleException("No record found !");
            if (found.Truncated) throw new BusinessRuleException($"More than {max} records match; narrow the search and update in parts.");
            if (found.HasZeroMco) throw new BusinessRuleException("Zero MCO found ! Please refer to IT department !");
            var p0 = found.Rows.Where(x => x.CoverId == "00").Select(x => x.MemberNumber).ToList();
            var cov = found.Rows.Where(x => x.CoverId != "00").ToList();
            updated = 0;
            foreach (var chunk in p0.Chunk(500))
                updated += await s.ExecuteAsync("UPDATE MBMOthers SET MBMInvoiceNo = @invoiceNo, MBMInvoiceDate = @invoiceDate, MBMInvoiceBy = @user WHERE MBMNumber IN @numbers AND (MBMInvoiceNo IS NULL OR MBMInvoiceNo = '')", new { invoiceNo, invoiceDate, user, numbers = chunk }, ct);
            foreach (var c in cov)
                updated += await s.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMInvoiceNo = @invoiceNo, MBMInvoiceDate = @invoiceDate, MBMInvoiceBy = @user WHERE MBMCNumber = @n AND MBMCCoverID = @cover AND (MBMInvoiceNo IS NULL OR MBMInvoiceNo = '')", new { invoiceNo, invoiceDate, user, n = c.MemberNumber, cover = c.CoverId }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Mco.ManualInvoice", "MBMOthers", invoiceNo, user, new Dictionary<string, object?> { ["filter"] = f, ["invoiceDate"] = r.InvoiceDate, ["updated"] = updated }));
        return new McoManualUpdateResult(updated, invoiceNo);
    }
}
