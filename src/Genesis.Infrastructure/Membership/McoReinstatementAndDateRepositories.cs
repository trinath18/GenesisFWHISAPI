using Genesis.Core.Data;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class McoReinstatementRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoReinstatementRepository
{
    private const int Max = 2000;
    private static string T(string? s) => s?.Trim() ?? "";
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;

    /// <summary>Same rows as the legacy VIEW_MBM_MCOReinstatement: reinstated persons of members that are not deleted and not clinic members.</summary>
    private const string Select = """
        SELECT TOP (2001) r.MBMIndex AS Idx, r.MBMNumber, r.MBMCoverID AS CoverId, r.MBMEndtBordxDate AS Bordx, r.MBMEndtBordxBatch AS Batch, r.MBMLapsedDate AS Lapsed, r.MBMReinstateDate AS Reinstated, r.MBMInvoiceNo AS InvoiceNo, r.MBMInvoiceDate AS InvoiceDate,
               r.MBMInvoiceBy AS InvoiceBy, r.MBMMCOFee AS Fee, m.MBMPolicyNo AS PolicyNo, m.MBMName, m.MBMPayorEffDate AS PayorEff, m.MBMPayorExpDate AS PayorExp, m.MBMAdultChild AS Type, m.PAYCode,
               c.MBMCName, c.MBMCRELCode
        FROM MBMReinstatement r LEFT JOIN MBM m ON m.MBMNumber = r.MBMNumber
             LEFT JOIN MBMCoveredPersons c ON c.MBMCNumber = r.MBMNumber AND c.MBMCCoverID = r.MBMCoverID
        WHERE m.HSCLNInd <> 'C' AND m.MBMDataStatus <> 'D'
        """;

    private static McoReinstatementListing Build(List<IDictionary<string, object?>> data)
    {
        var truncated = data.Count > Max; if (truncated) data = data.Take(Max).ToList();
        var rows = data.Select(r =>
        {
            var supp = T(S(r, "MBMCRELCode")).Length > 0;
            return new McoReinstatementRow(T(S(r, "MBMNumber")), T(S(r, "CoverId")), supp ? T(S(r, "MBMCName")) : T(S(r, "MBMName")), supp ? T(S(r, "MBMCRELCode")) : "P", Day(r, "Bordx"), T(S(r, "Batch")), T(S(r, "PolicyNo")),
                Day(r, "PayorEff"), Day(r, "PayorExp"), supp ? null : (T(S(r, "Type")) == "C" ? "Child" : "Adult"), Day(r, "Lapsed"), Day(r, "Reinstated"), T(S(r, "InvoiceNo")) is { Length: > 0 } n ? n : null, Day(r, "InvoiceDate"), T(S(r, "InvoiceBy")) is { Length: > 0 } b ? b : null, Dec(r, "Fee"));
        }).OrderBy(r => r.MemberNumber, StringComparer.Ordinal).ThenBy(r => r.CoverId, StringComparer.Ordinal).ToList();
        return new McoReinstatementListing(rows, rows.Count(r => r.Relation == "P"), rows.Count(r => r.Relation != "P"), rows.Sum(r => r.McoFee ?? 0), rows.Count > 0 && rows.All(r => r.InvoiceNo is not null), truncated);
    }

    private static (string Sql, object Args) Query(McoReinstatementFilter f) => f.Mode == "invoice"
        ? (Select + " AND r.MBMInvoiceNo = @invoice", new { invoice = T(f.InvoiceNo), payor = "", bordx = (DateTime?)null, batch = "" })
        : (Select + " AND m.PAYCode = @payor AND r.MBMEndtBordxDate = @bordx AND r.MBMEndtBordxBatch = @batch", new { invoice = "", payor = T(f.Payor), bordx = (DateTime?)f.BordxDate!.Value.ToDateTime(TimeOnly.MinValue), batch = f.BatchNo!.Value.ToString() });

    public async Task<McoReinstatementListing> ListAsync(McoReinstatementFilter f, CancellationToken ct = default)
    {
        if (McoReinstatementRules.Validate(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var (sql, args) = Query(f);
        return Build((await QueryAsync(GenesisDb.Main, sql, args, ct)).ToList());
    }

    public async Task<McoReinstatementInvoiceResult> InvoiceAsync(McoReinstatementFilter f, string user, CancellationToken ct = default)
    {
        if (f.Mode != "batch") throw new BusinessRuleException("An invoice number is generated for a payor, bordx date and batch.", field: "mode");
        if (McoReinstatementRules.Validate(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var payor = T(f.Payor); string invoiceNo; int updated;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var (sql, args) = Query(f);
            var raw = (await s.QueryAsync(sql, args, ct)).ToList();
            var found = Build(raw.ToList());
            if (found.Rows.Count == 0) throw BusinessRuleException.NotFound("Record not found !");
            if (found.Truncated) throw new BusinessRuleException("More than 2,000 rows match this batch; please contact support.");
            if (found.Rows.Any(r => r.InvoiceNo is not null)) throw BusinessRuleException.Conflict($"An invoice has already been generated for this batch ({found.Rows.First(r => r.InvoiceNo is not null).InvoiceNo}); reprint it by invoice number.");

            var prefix = (await s.QueryAsync("SELECT MCOInvoicePrefix FROM PAY WHERE PAYCode = @payor", new { payor }, ct)).Select(r => T(S(r, "MCOInvoicePrefix"))).FirstOrDefault();
            if (string.IsNullOrEmpty(prefix)) prefix = "S";
            var current = (await s.QueryAsync("SELECT TOP 1 MCOInvoice_No FROM MBMMCOINVQ WITH (UPDLOCK, HOLDLOCK)", null, ct)).Select(r => T(S(r, "MCOInvoice_No"))).FirstOrDefault();
            string number;
            if (current is null) { number = "00001"; await s.ExecuteAsync("INSERT INTO MBMMCOINVQ (MCOInvoice_No) VALUES (@number)", new { number }, ct); }
            else { number = (int.Parse(current) + 1).ToString("00000"); await s.ExecuteAsync("UPDATE MBMMCOINVQ SET MCOInvoice_No = @number", new { number }, ct); }
            invoiceNo = prefix + number;

            updated = 0;
            foreach (var idx in raw.Select(r => I(r, "Idx") ?? 0))
                updated += await s.ExecuteAsync("UPDATE MBMReinstatement SET MBMInvoiceDate = @today, MBMInvoiceNo = @invoiceNo, MBMInvoiceBy = @user WHERE MBMIndex = @idx AND (MBMInvoiceNo IS NULL OR MBMInvoiceNo = '')",
                    new { today = DateTime.Today, invoiceNo, user, idx }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Mco.ReinstatementInvoice", "MBMReinstatement", invoiceNo, user, new Dictionary<string, object?> { ["payor"] = payor, ["bordx"] = f.BordxDate, ["batch"] = f.BatchNo, ["rows"] = updated }));
        return new McoReinstatementInvoiceResult(invoiceNo, updated, await ListAsync(new McoReinstatementFilter("invoice", null, null, null, invoiceNo), ct));
    }
}

internal sealed class McoDateCorrectionRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoDateCorrectionRepository
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

    private static object Args(McoDateFilter f) => new { payor = f.Payor!.Trim(), memFrom = Blank(f.MemberFrom), memTo = Blank(f.MemberTo), bordxFrom = At(f.BordxFrom), bordxTo = At(f.BordxTo), batchFrom = f.BatchFrom, batchTo = f.BatchTo, invFrom = Blank(f.InvoiceFrom), invTo = Blank(f.InvoiceTo) };

    private static McoCorrectionRow Map(IDictionary<string, object?> r) => new(S(r, "MemberNumber")?.Trim() ?? "", S(r, "CoverId")?.Trim() ?? "", S(r, "Name")?.Trim(), Day(r, "BordxDate"), I(r, "BatchNo"),
        Blank(S(r, "InvoiceNo")), Day(r, "InvoiceDate"), Dec(r, "Mco"), Day(r, "PayorEff"), Day(r, "PayorExp"));

    public async Task<McoCorrectionListing> SearchAsync(McoDateFilter f, CancellationToken ct = default)
    {
        if (McoDateCorrectionRules.ValidateFilter(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var args = Args(f);
        var principals = await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max + 1}) m.MBMNumber AS MemberNumber, m.MBMCOVID AS CoverId, m.MBMName AS Name, m.MBMBordxDate AS BordxDate, m.MBMBatchNo AS BatchNo, o.MBMInvoiceNo AS InvoiceNo, o.MBMInvoiceDate AS InvoiceDate,
                   o.MBMMCOFee AS Mco, m.MBMPayorEffDate AS PayorEff, m.MBMPayorExpDate AS PayorExp
            FROM MBM m JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
            WHERE {Where} AND (@invFrom IS NULL OR o.MBMInvoiceNo >= @invFrom) AND (@invTo IS NULL OR o.MBMInvoiceNo <= @invTo) ORDER BY m.MBMNumber
            """, args, ct);
        var covered = await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max + 1}) c.MBMCNumber AS MemberNumber, c.MBMCCoverID AS CoverId, c.MBMCName AS Name, c.MBMCBordxDate AS BordxDate, c.MBMCBatchNo AS BatchNo, c.MBMInvoiceNo AS InvoiceNo, c.MBMInvoiceDate AS InvoiceDate,
                   c.MBMCMCO AS Mco, m.MBMPayorEffDate AS PayorEff, m.MBMPayorExpDate AS PayorExp
            FROM MBMCoveredPersons c JOIN MBM m ON m.MBMNumber = c.MBMCNumber
            WHERE {Where} AND (@invFrom IS NULL OR c.MBMInvoiceNo >= @invFrom) AND (@invTo IS NULL OR c.MBMInvoiceNo <= @invTo) ORDER BY c.MBMCNumber, c.MBMCCoverID
            """, args, ct);
        var rows = principals.Concat(covered).Select(Map).OrderBy(r => r.MemberNumber, StringComparer.Ordinal).ThenBy(r => r.CoverId, StringComparer.Ordinal).ToList();
        return new McoCorrectionListing(rows.Take(Max).ToList(), principals.Count() > Max || covered.Count() > Max || rows.Count > Max);
    }

    public async Task<McoDateCorrectionResult> UpdateAsync(McoDateCorrectionRequest r, string user, CancellationToken ct = default)
    {
        if (McoDateCorrectionRules.ValidateUpdate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var date = At(r.InvoiceDate)!.Value; var changes = new List<object>();
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            foreach (var item in r.Items!.Select(i => (Number: i.MemberNumber!.Trim(), Cover: i.CoverId!.Trim())))
            {
                var principal = item.Cover == "00";
                var row = (await s.QueryAsync(principal ? "SELECT MBMInvoiceNo AS InvoiceNo, MBMInvoiceDate AS InvoiceDate FROM MBMOthers WHERE MBMNumber = @n"
                    : "SELECT MBMInvoiceNo AS InvoiceNo, MBMInvoiceDate AS InvoiceDate, MBMCBatchNo AS Batch FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = @c", new { n = item.Number, c = item.Cover }, ct)).FirstOrDefault()
                    ?? throw BusinessRuleException.NotFound($"{item.Number} / {item.Cover} does not exist.");
                if (Blank(S(row, "InvoiceNo")) is null) throw new BusinessRuleException($"Please generate Invoice! {item.Number} / {item.Cover} has no invoice number; nothing was changed.");
                if (principal)
                {
                    await s.ExecuteAsync("UPDATE MBMOthers SET MBMInvoiceDate = @date WHERE MBMNumber = @n", new { date, n = item.Number }, ct);
                    if (r.BatchNo is { } b) await s.ExecuteAsync("UPDATE MBM SET MBMBatchNo = @b WHERE MBMNumber = @n", new { b, n = item.Number }, ct);
                }
                else await s.ExecuteAsync(r.BatchNo is null ? "UPDATE MBMCoveredPersons SET MBMInvoiceDate = @date WHERE MBMCNumber = @n AND MBMCCoverID = @c" : "UPDATE MBMCoveredPersons SET MBMInvoiceDate = @date, MBMCBatchNo = @b WHERE MBMCNumber = @n AND MBMCCoverID = @c", new { date, b = r.BatchNo, n = item.Number, c = item.Cover }, ct);
                changes.Add(new { member = item.Number, cover = item.Cover, invoice = S(row, "InvoiceNo"), beforeDate = Day(row, "InvoiceDate"), afterDate = r.InvoiceDate, batch = r.BatchNo });
            }
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Mco.DateCorrection", "MBMOthers", $"{changes.Count} row(s)", user, new Dictionary<string, object?> { ["invoiceDate"] = r.InvoiceDate, ["batchNo"] = r.BatchNo, ["changes"] = changes }));
        return new McoDateCorrectionResult(changes.Count, r.InvoiceDate!.Value, r.BatchNo);
    }
}

