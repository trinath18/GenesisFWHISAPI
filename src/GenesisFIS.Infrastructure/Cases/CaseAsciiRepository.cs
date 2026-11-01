using System.Text;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Case > Claim ASCII (frmNewClaimAscii) and Case > Case ASCII (frmCaseASCII). Rows come from the legacy procedure Case_ClaimAscii (worksheet database)
/// with a fragment built by <see cref="LegacyFilter"/>; the file layout is <see cref="ClaimAsciiLayout"/>. Batch numbers come from CLMAsciiSEQ (worksheet,
/// claim files, 6 digits) and CASAsciiSEQ (main, case files, 5 digits), one counter per payor and day as before. The file is returned for download and,
/// when FileStore:AsciiExportRoot is set, a copy is written to its ClaimASCII / CaseASCII sub-folder (the legacy "File on Server").
/// </summary>
internal sealed class CaseAsciiRepository(IDbConnectionFactory connections, IConfiguration configuration, IAuditLog audit) : SqlRepository(connections), ICaseAsciiRepository
{
    private const int MaxRecords = 50_000;

    /// <summary>A procedure row by column name; when a name repeats (joined tables), the first one wins, as ADO's rs!Name did.</summary>
    private static AsciiRow ToRow(IDictionary<string, object?> raw)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in raw) d.TryAdd(k, v);
        return new AsciiRow(d);
    }

    public async Task<IReadOnlyList<CaseOption>> PayorsAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY ORDER BY PAYCode", null, ct)).Select(r => new CaseOption(S(r, "PAYCode") ?? "", S(r, "PAYCompanyName") ?? "")).ToList();

    private static (string Payor, DateOnly From, DateOnly To) Check(string? payor, DateOnly? from, DateOnly? to, string dateName)
    {
        if (string.IsNullOrWhiteSpace(payor)) throw new BusinessRuleException("Please Enter Payor Code", field: "payor");
        var p = LegacyFilter.Value(payor, "payor");
        if (p.Length != 2) throw new BusinessRuleException("Please Enter Payor Code", field: "payor");
        if (from is null) throw new BusinessRuleException($"Please Enter {dateName} Fr ", field: "from");
        if (to is null) throw new BusinessRuleException($"Please Enter {dateName} To ", field: "to");
        if (from > to) throw new BusinessRuleException("The from date is after the to date.", field: "to");
        return (p, from.Value, to.Value);
    }

    private Task<List<IDictionary<string, object?>>> AsciiRowsAsync(string fragment, string listCase, CancellationToken ct) =>
        QueryAsync(GenesisDb.Worksheet, "EXEC Case_ClaimAscii @StrAppend, @StrAppendCase", new { StrAppend = fragment, StrAppendCase = listCase }, ct, timeoutSeconds: 300);

    /// <summary>The legacy GetCLMAsciiBatch / GetCASAsciiBatch: next number for the payor and day, zero-padded.</summary>
    private static async Task<string> NextBatchAsync(IDbSession s, string table, string payor, DateTime day, int digits, CancellationToken ct)
    {
        var current = (await s.QueryAsync($"SELECT TOP 1 SEQBatchNo FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE SEQPayCode = @payor AND SEQBatchDate = @day", new { payor, day }, ct)).Select(r => S(r, "SEQBatchNo")).FirstOrDefault();
        if (current is null)
        {
            var first = FixedWidth.Zero(1, digits);
            await s.ExecuteAsync($"INSERT INTO {table} (SEQPayCode, SEQBatchDate, SEQBatchNo) VALUES (@payor, @day, @first)", new { payor, day, first }, ct);
            return first;
        }
        var next = FixedWidth.Zero((int.TryParse(current, out var n) ? n : 0) + 1, digits);
        await s.ExecuteAsync($"UPDATE {table} SET SEQBatchNo = @next WHERE SEQPayCode = @payor AND SEQBatchDate = @day", new { next, payor, day }, ct);
        return next;
    }

    private string? SaveCopy(string folder, string fileName, byte[] content)
    {
        var root = configuration["FileStore:AsciiExportRoot"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, content);
        return Path.Combine(folder, fileName);
    }

    public async Task<AsciiExport> ClaimAsync(ClaimAsciiRequest r, string user, CancellationToken ct = default)
    {
        var (payor, from, to) = Check(r.Payor, r.From, r.To, r.Rejected ? "Case Reg. Date" : "Bordx Date");
        string fragment, listCase;
        if (r.Rejected)
        {
            listCase = "4";
            fragment = new LegacyFilter().Date("CaseRegDate", ">=", from).Date("CaseRegDate", "<=", to).Text("PAYCode", "=", payor, "payor").ToString();
        }
        else
        {
            // "0 - New & Amendment" used list case 1 plus the worksheet-status filter; "1 - Deleted" left the case empty, as here.
            listCase = r.Deleted ? "" : "1";
            var f = new LegacyFilter().Date("BordxDate", ">=", from).Date("BordxDate", "<=", to).Text("PAYCode", "=", payor, "payor").Constant(r.Reprint ? "AND CLMBordxWSASCII = 'Y'" : "AND CLMBordxWSASCII = 'N'");
            if (!r.Deleted) f.Constant("AND (CLMBordxWSStatus = 'A' or CLMBordxWSStatus = 'N')");
            fragment = f.ToString();
        }
        var rows = await AsciiRowsAsync(fragment, listCase, ct);
        if (rows.Count == 0) throw BusinessRuleException.NotFound("No record found!");
        if (rows.Count > MaxRecords) throw new BusinessRuleException($"More than {MaxRecords:N0} records; please use a shorter date range.");

        var now = DateTime.Now; var day = DateTime.Today;
        await using var w = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var batch = await NextBatchAsync(w, "CLMAsciiSEQ", payor, day, 6, ct);
        var content = new StringBuilder(ClaimAsciiLayout.Header(payor, batch, now));
        var marked = new List<string>();
        foreach (var raw in rows)
        {
            var row = ToRow(raw);
            content.Append(ClaimAsciiLayout.ClaimLine(row, r.Rejected));
            if (r.Rejected) continue;
            // Legacy UpdateWSAscii: when the claim version is still listed by case 2, mark the case as exported with Case_CLMAscUpdate.
            var no = LegacyFilter.Value(row.S("CasNumber"), "caseNumber");
            var version = int.TryParse(row.S("ClaimVersion"), out var v) ? v : (int?)null;
            var check = new LegacyFilter().Text("CASNumber", "=", no, "caseNumber").Number("ClaimVersion", "=", version).ToString();
            if ((await w.QueryAsync("EXEC Case_ClaimAscii @StrAppend, '2'", new { StrAppend = check }, ct)).Count > 0)
            {
                await w.ExecuteAsync("EXEC Case_CLMAscUpdate @no, 'Y'", new { no }, ct);
                marked.Add($"{no}-{version}");
            }
        }
        content.Append(ClaimAsciiLayout.Trailer(rows.Count));
        var fileName = ClaimAsciiLayout.FileName(payor, now, batch);
        var bytes = FixedWidth.Bytes(content.ToString());
        await w.CommitAsync(ct);
        var saved = SaveCopy("ClaimASCII", fileName, bytes);
        audit.Record(new AuditEntry("Case.ClaimAscii", "CLMAsciiSEQ", $"{payor}-{batch}", user, new Dictionary<string, object?>
            { ["request"] = r, ["records"] = rows.Count, ["markedExported"] = marked.Count, ["file"] = fileName, ["savedTo"] = saved }));
        return new AsciiExport(new AsciiFile(fileName, bytes, rows.Count, batch), saved);
    }

    public async Task<AsciiExport> CaseAsync(CaseAsciiRequest r, string user, CancellationToken ct = default)
    {
        var (payor, from, to) = Check(r.Payor, r.From, r.To, "Bordx Date");
        if (r.Endorsement is not ("0" or "1" or "2")) throw new BusinessRuleException("Please Enter Endt Status ", field: "endorsement");
        var f = new LegacyFilter().Date("CaseRegDate", ">=", from).Date("CaseRegDate", "<=", to).Text("PAYCode", "=", payor, "payor");
        if (r.Reprint) f.Constant("AND CLMBordxWSASCII = 'Y'");
        var rows = await AsciiRowsAsync(f.ToString(), r.Endorsement == "0" ? "5" : "", ct);
        if (rows.Count == 0) throw BusinessRuleException.NotFound("No record found!");
        if (rows.Count > MaxRecords) throw new BusinessRuleException($"More than {MaxRecords:N0} records; please use a shorter date range.");

        var now = DateTime.Now;
        await using var m = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var batch = await NextBatchAsync(m, "CASAsciiSEQ", payor, DateTime.Today, 5, ct);
        var content = new StringBuilder();
        foreach (var raw in rows) content.Append(ClaimAsciiLayout.CaseLine(ToRow(raw), now, batch));
        var fileName = ClaimAsciiLayout.FileName(payor, now, batch);
        var bytes = FixedWidth.Bytes(content.ToString());
        await m.CommitAsync(ct);
        var saved = SaveCopy("CaseASCII", fileName, bytes);
        audit.Record(new AuditEntry("Case.CaseAscii", "CASAsciiSEQ", $"{payor}-{batch}", user, new Dictionary<string, object?> { ["request"] = r, ["records"] = rows.Count, ["file"] = fileName, ["savedTo"] = saved }));
        return new AsciiExport(new AsciiFile(fileName, bytes, rows.Count, batch), saved);
    }
}
