using Genesis.Core.Data;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class McoReceiptRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoReceiptRepository
{
    private const int Max = 2000;
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d ? DateOnly.FromDateTime(d) : null;
    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    public async Task<McoReceiptInvoiceListing> InvoicesAsync(McoReceiptInvoiceSearch f, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(f.Payor) || T(f.Payor).Length > 4) throw new BusinessRuleException("Please select the payor.", field: "payor");
        if (string.IsNullOrWhiteSpace(f.NumberFrom) && string.IsNullOrWhiteSpace(f.NumberTo) && f.DateFrom is null && f.DateTo is null) throw new BusinessRuleException("Please enter an invoice number range or an invoice date range.", field: "numberFrom");
        var args = new { prefix = Like(T(f.Payor)) + "%", from = Blank(f.NumberFrom), to = Blank(f.NumberTo), dateFrom = At(f.DateFrom), dateTo = At(f.DateTo) };
        var invoices = (await QueryAsync(GenesisDb.Main, $"""
            SELECT TOP ({Max + 1}) MCOInvNo AS Number, MCOInvDate AS InvDate, SUM(BillMCOAmt) AS Total
            FROM MBMMCOInvoice WHERE MBMNumber LIKE @prefix AND MCOInvNo IS NOT NULL AND (@from IS NULL OR MCOInvNo >= @from) AND (@to IS NULL OR MCOInvNo <= @to)
              AND (@dateFrom IS NULL OR MCOInvDate >= @dateFrom) AND (@dateTo IS NULL OR MCOInvDate < DATEADD(day, 1, @dateTo))
            GROUP BY MCOInvNo, MCOInvDate ORDER BY MCOInvNo
            """, args, ct)).Select(r => (Number: T(S(r, "Number")), Date: Day(r, "InvDate"), Total: Dec(r, "Total") ?? 0, Kind: "Invoice")).ToList();
        if (f.IncludeCreditNotes)
            invoices.AddRange((await QueryAsync(GenesisDb.Main, $"""
                SELECT TOP ({Max + 1}) o.MBMCRNote AS Number, o.MBMCRNoteDate AS InvDate, SUM(r.MBMMCORefund) AS Total
                FROM MBMOthers o JOIN MBMRefund r ON r.MBMNumber = o.MBMNumber
                WHERE o.MBMNumber LIKE @prefix AND o.MBMCRNote <> '' AND (@from IS NULL OR o.MBMCRNote >= @from) AND (@to IS NULL OR o.MBMCRNote <= @to)
                  AND (@dateFrom IS NULL OR o.MBMCRNoteDate >= @dateFrom) AND (@dateTo IS NULL OR o.MBMCRNoteDate < DATEADD(day, 1, @dateTo))
                GROUP BY o.MBMCRNote, o.MBMCRNoteDate ORDER BY o.MBMCRNote
                """, args, ct)).Select(r => (Number: T(S(r, "Number")), Date: Day(r, "InvDate"), Total: Dec(r, "Total") ?? 0, Kind: "CreditNote")));
        var truncated = invoices.Count > Max; if (truncated) invoices = invoices.Take(Max).ToList();

        var received = new Dictionary<string, (int Lines, decimal Amount, string? No)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in invoices.Select(i => i.Number).Distinct().Chunk(1000))
            foreach (var r in await QueryAsync(GenesisDb.Payment, "SELECT InvoiceNo, COUNT(*) AS N, SUM(INVRecAmt) AS Amt, MAX(ReceiptNo) AS No FROM RECMCO WHERE InvoiceNo IN @numbers GROUP BY InvoiceNo", new { numbers = chunk }, ct))
                received[T(S(r, "InvoiceNo"))] = (I(r, "N") ?? 0, Dec(r, "Amt") ?? 0, Blank(S(r, "No")));
        var rows = invoices.Select(i => received.TryGetValue(i.Number, out var x)
            ? new McoReceiptInvoice(i.Number, i.Date, i.Total, T(f.Payor), i.Kind, x.Lines, x.Amount, x.No) : new McoReceiptInvoice(i.Number, i.Date, i.Total, T(f.Payor), i.Kind, 0, 0, null))
            .Where(i => !f.OnlyOpen || i.ReceiptLines == 0).ToList();
        return new McoReceiptInvoiceListing(rows, truncated);
    }

    public async Task<McoReceiptListing> ListAsync(McoReceiptSearch f, CancellationToken ct = default)
    {
        var type = T(f.Type);
        if (type.Length == 0) throw new BusinessRuleException("Please Enter Rec. Type", field: "type");
        var args = new { from = Blank(f.ReceiptFrom), to = Blank(f.ReceiptTo), dateFrom = At(f.DateFrom), dateTo = At(f.DateTo), invoice = Blank(f.InvoiceNo), type };
        IEnumerable<IDictionary<string, object?>> data; bool mco = type == "M";
        const string where = "(@from IS NULL OR ReceiptNo >= @from) AND (@to IS NULL OR ReceiptNo <= @to) AND (@dateFrom IS NULL OR ReceiptDate >= @dateFrom) AND (@dateTo IS NULL OR ReceiptDate < DATEADD(day, 1, @dateTo))";
        if (mco) data = await QueryAsync(GenesisDb.Payment, $"SELECT TOP ({Max + 1}) RecMCOIndex AS Idx, ReceiptNo, ReceiptDate, ReceiptAmt, ReceiptType, InvoiceNo, InvoiceDate, INVRecAmt, RECChqNo AS ChqNo, RECChqDate AS ChqDate, AccCode, RecMCORemarks AS Remarks, RecMCOLastUpdateUser AS UpdatedBy FROM RECMCO WHERE {where} AND (@invoice IS NULL OR InvoiceNo = @invoice) ORDER BY RecMCOIndex DESC", args, ct);
        else data = await QueryAsync(GenesisDb.Payment, $"SELECT TOP ({Max + 1}) RecOthersIndex AS Idx, ReceiptNo, ReceiptDate, ReceiptAmt, ReceiptType, NULL AS InvoiceNo, NULL AS InvoiceDate, NULL AS INVRecAmt, RECChqNo AS ChqNo, CONVERT(varchar(10), RECChqDate, 103) AS ChqDate, RECAccCode AS AccCode, RecOthersRemarks AS Remarks, RecLastUpdateUser AS UpdatedBy FROM RECOthers WHERE {where} AND ReceiptType = @type ORDER BY RecOthersIndex DESC", args, ct);
        var rows = data.Select(r => new McoReceiptRow(mco ? "M" : "O", I(r, "Idx") ?? 0, T(S(r, "ReceiptNo")), Day(r, "ReceiptDate"), Dec(r, "ReceiptAmt"), T(S(r, "ReceiptType")), Blank(S(r, "InvoiceNo")), Day(r, "InvoiceDate"), Dec(r, "INVRecAmt"),
            Blank(S(r, "ChqNo")), Blank(S(r, "ChqDate")), Blank(S(r, "AccCode")), Blank(S(r, "Remarks")), Blank(S(r, "UpdatedBy")))).ToList();
        var truncated = rows.Count > Max; if (truncated) rows = rows.Take(Max).ToList();
        return new McoReceiptListing(rows, mco ? rows.GroupBy(r => r.ReceiptNo).Sum(g => g.First().Amount ?? 0) : rows.Sum(r => r.Amount ?? 0), truncated);
    }

    public async Task<McoReceiptResult> CreateAsync(McoReceiptRequest r, bool manual, string user, CancellationToken ct = default)
    {
        if (McoReceiptRules.Validate(r, manual) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var type = T(r.Type); var amount = Math.Round(r.Amount!.Value, 2); var date = At(r.ReceiptDate)!.Value; var today = DateTime.Today;
        var lines = new List<(string No, DateTime? Date, decimal Allocated)>();

        if (type == "M")
        {
            var numbers = r.Invoices!.Select(T).ToList();
            var totals = (await QueryAsync(GenesisDb.Main, "SELECT MCOInvNo AS Number, MCOInvDate AS InvDate, SUM(BillMCOAmt) AS Total FROM MBMMCOInvoice WHERE MCOInvNo IN @numbers GROUP BY MCOInvNo, MCOInvDate", new { numbers }, ct))
                .ToDictionary(x => T(S(x, "Number")), x => (Date: D(x, "InvDate"), Total: Dec(x, "Total") ?? 0), StringComparer.OrdinalIgnoreCase);
            var credits = (await QueryAsync(GenesisDb.Main, "SELECT o.MBMCRNote AS Number, o.MBMCRNoteDate AS InvDate, SUM(rf.MBMMCORefund) AS Total FROM MBMOthers o JOIN MBMRefund rf ON rf.MBMNumber = o.MBMNumber WHERE o.MBMCRNote IN @numbers GROUP BY o.MBMCRNote, o.MBMCRNoteDate", new { numbers }, ct))
                .ToDictionary(x => T(S(x, "Number")), x => (Date: D(x, "InvDate"), Total: Dec(x, "Total") ?? 0), StringComparer.OrdinalIgnoreCase);
            foreach (var n in numbers)
            {
                if (totals.TryGetValue(n, out var inv)) lines.Add((n, inv.Date, inv.Total));
                else if (credits.TryGetValue(n, out var cr)) lines.Add((n, cr.Date, -cr.Total));
                else throw BusinessRuleException.NotFound($"Invoice or credit note '{n}' does not exist.");
            }
        }

        string receiptNo; int count;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Payment, ct))
        {
            if (manual) receiptNo = T(r.ReceiptNo);
            else
            {
                var current = (await s.QueryAsync("SELECT TOP 1 RecOthersSeqNo FROM RECOthersSeq WITH (UPDLOCK, HOLDLOCK)", null, ct)).Select(x => T(S(x, "RecOthersSeqNo"))).FirstOrDefault();
                string next;
                if (current is null) { next = "00001"; await s.ExecuteAsync("INSERT INTO RECOthersSeq (RecOthersSeqNo) VALUES (@next)", new { next }, ct); }
                else { next = (int.Parse(current) + 1).ToString("00000"); await s.ExecuteAsync("UPDATE RECOthersSeq SET RecOthersSeqNo = @next", new { next }, ct); }
                receiptNo = "MB" + next;
            }
            if (await s.ScalarAsync<int>("SELECT (SELECT COUNT(*) FROM RECMCO WHERE ReceiptNo = @receiptNo) + (SELECT COUNT(*) FROM RECOthers WHERE ReceiptNo = @receiptNo)", new { receiptNo }, ct) > 0)
                throw BusinessRuleException.Conflict($"Receipt number '{receiptNo}' is already used.");

            var chequeDate = At(r.ChequeDate);
            if (type == "M")
            {
                foreach (var l in lines)
                {
                    var done = await s.QueryAsync("SELECT TOP 1 ReceiptNo FROM RECMCO WHERE InvoiceNo = @no", new { no = l.No }, ct);
                    if (done.FirstOrDefault() is { } existing) throw BusinessRuleException.Conflict($"'{l.No}' is already receipted on {T(S(existing, "ReceiptNo"))}; edit that receipt instead.");
                    await s.ExecuteAsync("""
                        INSERT INTO RECMCO (ReceiptNo, ReceiptDate, ReceiptAmt, INVRecAmt, ReceiptType, InvoiceNo, InvoiceDate, RECChqNo, RECChqDate, RecMCORemarks, RecMCOLastUpdatedate, RecMCOLastUpdateUser)
                        VALUES (@receiptNo, @date, @amount, @alloc, 'M', @no, @invDate, @chq, @chqDate, @remarks, @today, @user)
                        """, new { receiptNo, date, amount, alloc = l.Allocated, no = l.No, invDate = l.Date, chq = Blank(r.ChequeNo), chqDate = r.ChequeDate?.ToString("dd/MM/yyyy"), remarks = Blank(r.Remarks), today, user }, ct);
                }
                count = lines.Count;
            }
            else
            {
                var account = string.IsNullOrWhiteSpace(r.AccountCode) ? McoReceiptTypes.DefaultAccount(type)! : r.AccountCode.Trim();
                await s.ExecuteAsync("""
                    INSERT INTO RECOthers (ReceiptNo, ReceiptDate, ReceiptAmt, ReceiptType, RECAccCode, RECChqNo, RECChqDate, RecOthersRemarks, RecLastUpdatedate, RecLastUpdateUser)
                    VALUES (@receiptNo, @date, @amount, @type, @account, @chq, @chqDate, @remarks, @today, @user)
                    """, new { receiptNo, date, amount, type, account, chq = Blank(r.ChequeNo), chqDate = chequeDate, remarks = Blank(r.Remarks), today, user }, ct);
                count = 1;
            }
            await s.CommitAsync(ct);
        }
        var allocated = lines.Sum(l => l.Allocated);
        audit.Record(new AuditEntry("McoReceipt.Create", type == "M" ? "RECMCO" : "RECOthers", receiptNo, user, new Dictionary<string, object?> { ["request"] = r, ["lines"] = lines, ["manual"] = manual }));
        return new McoReceiptResult(receiptNo, count, allocated, type == "M" ? Math.Round(amount - allocated, 2) : 0);
    }

    public async Task UpdateAsync(string kind, int index, McoReceiptUpdate u, string user, CancellationToken ct = default)
    {
        if (kind is not ("M" or "O")) throw new BusinessRuleException("Unknown receipt kind.", field: "kind");
        if (McoReceiptRules.ValidateUpdate(u, kind) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var args = new { index, date = At(u.ReceiptDate), amount = Math.Round(u.Amount!.Value, 2), chq = Blank(u.ChequeNo), remarks = Blank(u.Remarks), today = DateTime.Today, user, alloc = u.Allocated };
        object before;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Payment, ct))
        {
            if (kind == "M")
            {
                var row = (await s.QueryAsync("SELECT ReceiptNo, ReceiptDate, ReceiptAmt, INVRecAmt, RECChqNo, RECChqDate, RecMCORemarks FROM RECMCO WHERE RecMCOIndex = @index", args, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("The receipt line does not exist.");
                before = row;
                await s.ExecuteAsync("UPDATE RECMCO SET INVRecAmt = @alloc WHERE RecMCOIndex = @index", args, ct);
                await s.ExecuteAsync("UPDATE RECMCO SET ReceiptDate = @date, ReceiptAmt = @amount, RECChqNo = @chq, RECChqDate = @chqDate, RecMCORemarks = @remarks, RecMCOLastUpdatedate = @today, RecMCOLastUpdateUser = @user WHERE ReceiptNo = @no",
                    new { args.date, args.amount, args.chq, chqDate = u.ChequeDate?.ToString("dd/MM/yyyy"), args.remarks, args.today, args.user, no = S(row, "ReceiptNo") }, ct);
            }
            else
            {
                before = (await s.QueryAsync("SELECT ReceiptNo, ReceiptDate, ReceiptAmt, RECChqNo, RECChqDate, RecOthersRemarks FROM RECOthers WHERE RecOthersIndex = @index", args, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("The receipt does not exist.");
                await s.ExecuteAsync("UPDATE RECOthers SET ReceiptDate = @date, ReceiptAmt = @amount, RECChqNo = @chq, RECChqDate = @chqDate, RecOthersRemarks = @remarks, RecLastUpdatedate = @today, RecLastUpdateUser = @user WHERE RecOthersIndex = @index",
                    new { args.index, args.date, args.amount, args.chq, chqDate = At(u.ChequeDate), args.remarks, args.today, args.user }, ct);
            }
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("McoReceipt.Update", kind == "M" ? "RECMCO" : "RECOthers", index.ToString(), user, new Dictionary<string, object?> { ["before"] = before, ["after"] = u }));
    }

    public async Task DeleteAsync(string kind, int index, string user, CancellationToken ct = default)
    {
        if (kind is not ("M" or "O")) throw new BusinessRuleException("Unknown receipt kind.", field: "kind");
        object before;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Payment, ct))
        {
            var sql = kind == "M" ? "SELECT * FROM RECMCO WHERE RecMCOIndex = @index" : "SELECT * FROM RECOthers WHERE RecOthersIndex = @index";
            before = (await s.QueryAsync(sql, new { index }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("The receipt does not exist.");
            await s.ExecuteAsync(kind == "M" ? "DELETE FROM RECMCO WHERE RecMCOIndex = @index" : "DELETE FROM RECOthers WHERE RecOthersIndex = @index", new { index }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("McoReceipt.Delete", kind == "M" ? "RECMCO" : "RECOthers", index.ToString(), user, new Dictionary<string, object?> { ["before"] = before }));
    }
}

