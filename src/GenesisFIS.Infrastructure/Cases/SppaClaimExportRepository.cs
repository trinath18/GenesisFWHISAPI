using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Case > Claims ASCII (To SPPA) / Claims ASCII Pymt (To SPPA). Reads the eight views the legacy procedure Case_ClaimAscii_FWHS selected from (the procedure
/// only appended the form's date condition to "select * from view"), with an ordinary parameterised date range instead of the procedure's text fragment.
/// The claim files (AH, AD, AA, AN, RD) are selected by bordereau date and the payment files (PH, PD, PS) by voucher date, as the legacy "To SPPA" form;
/// "by payment date" selects every file by voucher date, as the legacy "Pymt" form (which otherwise was an older copy: medical report fees 80/92 only,
/// a 2014 rule for older invoices and 30-character cheque numbers; the current rules are used here).
/// </summary>
internal sealed class SppaClaimExportRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ISppaClaimExportRepository
{
    private const int Chunk = 1000;
    private const int Timeout = 300;

    private Task<List<IDictionary<string, object?>>> ViewAsync(string view, string dateColumn, DateTime from, DateTime to, string order, CancellationToken ct) =>
        QueryAsync(GenesisDb.Worksheet, $"SELECT * FROM {view} WHERE {dateColumn} >= @from AND {dateColumn} < DATEADD(day, 1, @to) ORDER BY {order}", new { from, to }, ct, timeoutSeconds: Timeout);

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await QueryAsync(GenesisDb.Worksheet, sql, new { keys = chunk }, ct, timeoutSeconds: Timeout));
        return all;
    }

    private static (string, int) Key(string? no, object? version) => ((no ?? "").Trim().ToUpperInvariant(), (int)(SppaClaimExport.Num(version) ?? 0));

    public async Task<SppaExport?> ExportAsync(SppaExportRequest r, string user, CancellationToken ct = default)
    {
        if (r.From is null) throw new BusinessRuleException(r.ByPaymentDate ? "Please Enter PV Date Fr " : "Please Enter Bordx Date Fr ", field: "from");
        if (r.To is null) throw new BusinessRuleException(r.ByPaymentDate ? "Please Enter PV Date To " : "Please Enter Bordx Date To ", field: "to");
        if (r.To < r.From) throw new BusinessRuleException("The end date is before the start date.", field: "to");
        var from = r.From.Value.ToDateTime(TimeOnly.MinValue); var to = r.To.Value.ToDateTime(TimeOnly.MinValue);
        var claimDate = r.ByPaymentDate ? "PVHOSDate" : "CLMBordxDate";

        var claims = await ViewAsync("View_Claims_ASCII_FWHSAH", claimDate, from, to, "CASNumber, ClaimVersion, InvNo", ct);
        if (claims.Count == 0) return null;
        var details = await ViewAsync("View_Claims_ASCII_FWHSAD", claimDate, from, to, "CASNumber, ClaimVersion, InvNo", ct);
        var admissions = await ViewAsync("View_Claims_ASCII_FWHSAA", claimDate, from, to, "CASNumber", ct);
        var notes = await ViewAsync("View_Claims_ASCII_FWHSAN", claimDate, from, to, "CASNumber, ClaimVersion", ct);
        var requests = await ViewAsync("View_Claims_ASCII_FWHSRD", claimDate, from, to, "CASNumber", ct);
        var groups = await ViewAsync("View_Claims_ASCII_FWHSPH_Group", "PVHOSDate", from, to, "PVHOSNo", ct);
        var payments = await ViewAsync("View_Claims_ASCII_FWHSPD", "PVHOSDate", from, to, "PVHOSNo, CASNumber, InvNo", ct);
        var slips = await ViewAsync("View_Claims_ASCII_FWHSPS", "PVHOSDate", from, to, "PVHOSNo", ct);

        // The legacy form looked these up row by row; here they are read once per export.
        var ahCount = (await InChunksAsync("SELECT CASNumber, ClaimVersion, COUNT(*) AS N FROM View_Claims_ASCII_FWHSAH WHERE CASNumber IN @keys GROUP BY CASNumber, ClaimVersion",
            claims.Select(c => S(c, "CASNumber")).OfType<string>(), ct)).ToDictionary(x => Key(S(x, "CASNumber"), Get(x, "ClaimVersion")), x => I(x, "N") ?? 0);
        var adCount = (await InChunksAsync("SELECT CASNumber, ClaimVersion, COUNT(*) AS N FROM View_Claims_ASCII_FWHSAD WHERE CASNumber IN @keys GROUP BY CASNumber, ClaimVersion",
            details.Select(c => S(c, "CASNumber")).OfType<string>(), ct)).ToDictionary(x => Key(S(x, "CASNumber"), Get(x, "ClaimVersion")), x => I(x, "N") ?? 0);
        var byVoucher = (await InChunksAsync("SELECT * FROM View_Claims_ASCII_FWHSPD WHERE PVHOSNo IN @keys ORDER BY PVHOSNo, CASNumber, INVAmount",
            groups.Select(g => S(g, "PVHOSNo")).OfType<string>(), ct)).GroupBy(x => (S(x, "PVHOSNo") ?? "").ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IDictionary<string, object?>>)g.OrderBy(x => S(x, "PVHOSNo")).ThenBy(x => S(x, "CASNumber")).ThenBy(x => Dec(x, "INVAmount")).ToList());
        var caseRows = await InChunksAsync("SELECT CASNumber, InvNo, INVAmount FROM View_Claims_ASCII_FWHSPD WHERE CASNumber IN @keys",
            payments.Select(p => S(p, "CASNumber")).OfType<string>(), ct);
        var caseCount = caseRows.GroupBy(x => (S(x, "CASNumber") ?? "").ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var invoiceAmount = caseRows.GroupBy(x => ((S(x, "CASNumber") ?? "").ToUpperInvariant(), (S(x, "InvNo") ?? "").ToUpperInvariant())).ToDictionary(g => g.Key, g => Dec(g.Last(), "INVAmount"));

        var source = new SppaSource
        {
            Claims = claims, Details = details, Admissions = admissions, Notes = notes, Requests = requests, PaymentGroups = groups, Payments = payments, PaymentSlips = slips,
            ClaimRows = (no, v) => ahCount.GetValueOrDefault(Key(no, v), 1),
            DetailRows = (no, v) => adCount.GetValueOrDefault(Key(no, v), 1),
            VoucherPayments = pv => byVoucher.GetValueOrDefault(pv.ToUpperInvariant(), []),
            CasePaymentRows = no => caseCount.GetValueOrDefault(no.ToUpperInvariant(), 1),
            CaseInvoiceAmount = (no, inv) => invoiceAmount.GetValueOrDefault((no.ToUpperInvariant(), inv.ToUpperInvariant())),
        };

        // Legacy GetCLMAsciiMedBatch: the next number of the single CLMASCIIMedGen counter (shared with the other claim exports), taken only when there is a claim.
        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var current = await ws.ScalarAsync<string>("SELECT TOP 1 SEQBatchNo FROM CLMASCIIMedGen WITH (UPDLOCK, HOLDLOCK)", null, ct);
        var batch = ((int.TryParse(current?.Trim(), out var n) ? n : 0) + 1).ToString("000000");
        if (current is null) await ws.ExecuteAsync("INSERT INTO CLMASCIIMedGen (SEQBatchNo) VALUES (@batch)", new { batch }, ct);
        else await ws.ExecuteAsync("UPDATE CLMASCIIMedGen SET SEQBatchNo = @batch", new { batch }, ct);
        var export = SppaClaimExport.Build(source, batch)!;
        await ws.CommitAsync(ct);
        audit.Record(new AuditEntry("Claim.SppaExport", "CLMASCIIMedGen", batch, user, new Dictionary<string, object?>
        {
            ["from"] = r.From, ["to"] = r.To, ["byPaymentDate"] = r.ByPaymentDate, ["totals"] = export.Totals, ["files"] = export.Files.Select(f => f.Name).ToList(),
        }));
        return export;
    }
}
