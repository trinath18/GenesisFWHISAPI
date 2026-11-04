using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Cases;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Payments;

/// <summary>
/// Receipt > Payor (legacy FrmReceiptPayor). The legacy listing read VIEW_WKSReceiptPAY (worksheet database, joining the payment and main databases);
/// here each database is read on its own and joined in memory, so a save's uncommitted rows never block the read. Legacy differences: the contra entered
/// on a claim is saved to ContraAllocation (the legacy save never called UpdateContra); Update writes the edited claim's details and quotes its dates
/// (the legacy update left the claim row unchanged and wrote dates as arithmetic, e.g. 2217-08-31); a partial receipt no longer marks an existing hospital
/// payment as fully paid.
/// </summary>
internal sealed class PayorReceiptRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IPayorReceiptRepository
{
    private const int Chunk = 1000;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
    private static (string, int) Key(string no, int version) => (no.Trim().ToUpperInvariant(), version);

    public async Task<PayorReceiptOptions> OptionsAsync(string user, CancellationToken ct = default)
    {
        var payors = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY WHERE ISNULL(PAYCode, '') <> '' ORDER BY PAYCode", null, ct))
            .Select(r => new PayorReceiptOption(S(r, "PAYCode")!, S(r, "PAYCompanyName") ?? "")).ToList();
        var banks = (await QueryAsync(GenesisDb.Maintenance, "SELECT Bank FROM BankAccounts WHERE ISNULL(Bank, '') <> '' ORDER BY Bank", null, ct))
            .Select(r => S(r, "Bank")!).Distinct().ToList();
        var authorised = await ScalarAsync<string>(GenesisDb.Maintenance, "SELECT AuthorisedStatus FROM USR WHERE USRCode = @user", new { user }, ct);
        return new PayorReceiptOptions(payors, banks, authorised?.Trim() == "Y");
    }

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(GenesisDb db, string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await QueryAsync(db, sql, new { keys = chunk }, ct));
        return all;
    }

    public async Task<PayorReceiptListing> SearchAsync(PayorReceiptSearch s, CancellationToken ct = default)
    {
        var receiptFilter = Blank(s.ReceiptNoFrom) is not null || Blank(s.ReceiptNoTo) is not null || s.ReceiptDateFrom is not null || s.ReceiptDateTo is not null;
        var args = new Dictionary<string, object?>
        {
            ["payor"] = Blank(s.Payor), ["claimFrom"] = Blank(s.ClaimNoFrom), ["claimTo"] = Blank(s.ClaimNoTo), ["claimId"] = Blank(s.ClaimId), ["recFrom"] = Blank(s.ReceiptNoFrom),
            ["recTo"] = Blank(s.ReceiptNoTo), ["recDateFrom"] = At(s.ReceiptDateFrom), ["recDateTo"] = At(s.ReceiptDateTo), ["claimDateFrom"] = At(s.ClaimDateFrom),
            ["claimDateTo"] = At(s.ClaimDateTo), ["bordxDateFrom"] = At(s.BordxDateFrom), ["bordxDateTo"] = At(s.BordxDateTo), ["bordxFrom"] = Blank(s.BordxNoFrom),
            ["bordxTo"] = Blank(s.BordxNoTo), ["max"] = PayorReceiptRules.MaxLines + 1,
        };
        var where = new List<string>();
        if (args["payor"] is not null) where.Add("b.BordxPAYCode = @payor");
        // One claim number is that claim (legacy "Use Scan"); two are a range.
        if (args["claimFrom"] is not null) where.Add(args["claimTo"] is null ? "c.CASNumber = @claimFrom" : "c.CASNumber >= @claimFrom");
        if (args["claimTo"] is not null) where.Add(args["claimFrom"] is null ? "c.CASNumber = @claimTo" : "c.CASNumber <= @claimTo");
        if (args["claimId"] is not null) where.Add("c.ClaimID = @claimId");
        if (args["claimDateFrom"] is not null) where.Add("c.ClaimCloseDate >= @claimDateFrom");
        if (args["claimDateTo"] is not null) where.Add("c.ClaimCloseDate < DATEADD(day, 1, @claimDateTo)");
        if (args["bordxDateFrom"] is not null) where.Add("b.BordxDate >= @bordxDateFrom");
        if (args["bordxDateTo"] is not null) where.Add("b.BordxDate < DATEADD(day, 1, @bordxDateTo)");
        if (args["bordxFrom"] is not null) where.Add("c.BordxRefNo >= @bordxFrom");
        if (args["bordxTo"] is not null) where.Add("c.BordxRefNo <= @bordxTo");
        var status = s.Status?.Trim().ToLowerInvariant();
        if (status == PayorReceiptStatus.Partial) where.Add("c.PVRecPayorStatus = 'P'");
        if (status == PayorReceiptStatus.Unpaid) where.Add("ISNULL(c.PVRecPayorStatus, '') <> 'F'");
        if (where.Count == 0 && !receiptFilter)
            throw new BusinessRuleException("Please narrow the search (payor, claim, receipt, dates or bordereau).", field: "search");

        // The receipt filters pick the claims first (ReceiptfrPAY is indexed by receipt number), the claim filters narrow them.
        List<string>? receiptCases = null;
        if (receiptFilter)
        {
            var rw = new List<string>();
            if (args["recFrom"] is not null) rw.Add("ReceiptNo >= @recFrom");
            if (args["recTo"] is not null) rw.Add("ReceiptNo <= @recTo");
            if (args["recDateFrom"] is not null) rw.Add("ReceiptDate >= @recDateFrom");
            if (args["recDateTo"] is not null) rw.Add("ReceiptDate < DATEADD(day, 1, @recDateTo)");
            receiptCases = (await QueryAsync(GenesisDb.Payment, $"SELECT DISTINCT TOP (@max) CASNumber FROM ReceiptfrPAY WHERE {string.Join(" AND ", rw)}", args, ct))
                .Select(r => S(r, "CASNumber")).OfType<string>().ToList();
            if (receiptCases.Count == 0) return new PayorReceiptListing([], false);
        }
        var sql = $"""
            SELECT {(receiptCases is null ? "TOP (@max)" : "")} c.CASNumber, c.ClaimVersion, c.BordxRefNo, c.CLMBordxAmt, c.PVRecPayorStatus, c.ClaimCloseDate, c.ClaimID, b.BordxPAYCode, b.BordxDate
            FROM Claim c LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo
            WHERE {(where.Count == 0 ? "1 = 1" : string.Join(" AND ", where))} {(receiptCases is null ? "" : "AND c.CASNumber IN @keys")}
            ORDER BY c.CASNumber, c.ClaimVersion
            """;
        var claims = new List<IDictionary<string, object?>>();
        if (receiptCases is null) claims = await QueryAsync(GenesisDb.Worksheet, sql, args, ct);
        else
            foreach (var chunk in receiptCases.Chunk(Chunk))
                claims.AddRange(await QueryAsync(GenesisDb.Worksheet, sql, new Dictionary<string, object?>(args) { ["keys"] = chunk }, ct));
        var lines = await LinesAsync(claims, ct);
        if (receiptFilter)
            lines = lines.Where(l => l.ReceiptNo is not null && (Blank(s.ReceiptNoFrom) is not { } from || string.CompareOrdinal(l.ReceiptNo, from) >= 0)
                && (Blank(s.ReceiptNoTo) is not { } to || string.CompareOrdinal(l.ReceiptNo, to) <= 0)
                && (s.ReceiptDateFrom is null || l.ReceiptDate >= s.ReceiptDateFrom) && (s.ReceiptDateTo is null || l.ReceiptDate <= s.ReceiptDateTo)).ToList();
        if (status == PayorReceiptStatus.Paid) lines = lines.Where(l => l.ReceiptNo is not null).ToList();
        if (status == PayorReceiptStatus.Unpaid) lines = lines.Where(l => l.ReceiptNo is null).ToList();
        var truncated = lines.Count > PayorReceiptRules.MaxLines || (receiptCases is null && claims.Count > PayorReceiptRules.MaxLines);
        return new PayorReceiptListing(lines.Take(PayorReceiptRules.MaxLines).ToList(), truncated);
    }

    /// <summary>The listing rows of the claims: one per receipt of the claim, or one without a receipt when it has none.</summary>
    private async Task<List<PayorReceiptLine>> LinesAsync(List<IDictionary<string, object?>> claims, CancellationToken ct)
    {
        var cases = claims.Select(r => S(r, "CASNumber")).OfType<string>().ToList();
        var receipts = (await InChunksAsync(GenesisDb.Payment, "SELECT * FROM ReceiptfrPAY WHERE CASNumber IN @keys", cases, ct))
            .ToLookup(r => Key(S(r, "CASNumber")!, I(r, "ClaimVersion") ?? 0));
        var payments = (await InChunksAsync(GenesisDb.Payment, "SELECT CASNumber, ClaimVersion, PVHOSChequeDate, PVHOSChequeNoMR, PVBank FROM Payment2HOS WHERE CASNumber IN @keys", cases, ct))
            .GroupBy(r => Key(S(r, "CASNumber")!, int.TryParse(S(r, "ClaimVersion"), out var v) ? v : 0)).ToDictionary(g => g.Key, g => g.First());
        var contras = (await InChunksAsync(GenesisDb.Payment, "SELECT RefNo, CaseNo, VersionNo, ContraCaseNo, ContraVersionNo, ContraCaseAmt FROM ContraAllocation WHERE CaseNo IN @keys", cases, ct))
            .GroupBy(r => (S(r, "RefNo") ?? "", Key(S(r, "CaseNo") ?? "", int.TryParse(S(r, "VersionNo"), out var v) ? v : 0))).ToDictionary(g => g.Key, g => g.First());
        var lines = new List<PayorReceiptLine>();
        foreach (var c in claims)
        {
            var no = S(c, "CASNumber")!; var version = I(c, "ClaimVersion") ?? 0; var key = Key(no, version);
            var amount = Dec(c, "CLMBordxAmt") ?? 0;
            var own = receipts[key].OrderBy(r => D(r, "ReceiptDate")).ThenBy(r => S(r, "ReceiptNo")).ToList();
            var total = own.Sum(r => Dec(r, "CheckAmt") ?? 0);
            payments.TryGetValue(key, out var pay);
            PayorReceiptLine Line(IDictionary<string, object?>? r)
            {
                var receiptNo = r is null ? null : S(r, "ReceiptNo");
                var contra = receiptNo is null ? null : contras.GetValueOrDefault((receiptNo, key));
                return new PayorReceiptLine(no, version, S(c, "BordxRefNo"), S(c, "BordxPAYCode"), S(c, "ClaimID"), amount, r is null ? 0 : Dec(r, "CheckAmt") ?? 0, total, amount - total,
                    S(c, "PVRecPayorStatus"), receiptNo, r is null ? null : Day(r, "ReceiptDate"), r is null ? null : Dec(r, "ReceiptAmt"),
                    r is null ? null : I(r, "RECAccPeriodMth"), r is null ? null : I(r, "RECAccPeriodYR"), r is null ? null : S(r, "Remark"), r is null ? null : S(r, "PAYPVNo"),
                    r is null ? null : Day(r, "PAYPVDate"), r is null ? null : S(r, "ChequeNo"), pay is null ? null : S(pay, "PVHOSChequeNoMR"),
                    pay is null ? null : Day(pay, "PVHOSChequeDate"), pay is null ? null : S(pay, "PVBank"), r is null ? null : Dec(r, "PAYBankCharge"),
                    contra is null ? null : S(contra, "ContraCaseNo"), contra is null ? null : S(contra, "ContraVersionNo"), contra is null ? null : Dec(contra, "ContraCaseAmt"),
                    r is null ? null : Dec(r, "PAYGainLoss"), r is null ? null : Dec(r, "PAYMisc"), r is null ? null : Dec(r, "PAYDiscount"), r is null ? null : Dec(r, "PAYWriteOff"),
                    r is null ? null : Dec(r, "PAYMedixReceipt"), r is null ? null : S(r, "PAYRemarks"), Day(c, "ClaimCloseDate"), Day(c, "BordxDate"),
                    r is null ? null : Day(r, "PAYChqSentDate"), r is null ? null : S(r, "PAYCourierNo"));
            }
            if (own.Count == 0) lines.Add(Line(null));
            else lines.AddRange(own.Select(Line));
        }
        return lines;
    }

    /// <summary>A claim as the save needs it: bordereau amount, status, payor and what its receipts already paid.</summary>
    private sealed record SaveClaim(string CaseNumber, int Version, string? BordxNo, string? Payor, decimal Amount, string? Status);

    private static async Task<SaveClaim> ClaimAsync(IDbSession ws, string no, int version, CancellationToken ct)
    {
        var r = (await ws.QueryAsync("""
            SELECT c.CASNumber, c.ClaimVersion, c.BordxRefNo, c.CLMBordxAmt, c.PVRecPayorStatus, b.BordxPAYCode
            FROM Claim c WITH (UPDLOCK) LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo WHERE c.CASNumber = @no AND c.ClaimVersion = @version
            """, new { no = no.Trim(), version }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Claim {no}-{version} not found.");
        return new SaveClaim(S(r, "CASNumber")!, version, S(r, "BordxRefNo"), S(r, "BordxPAYCode"), Dec(r, "CLMBordxAmt") ?? 0, S(r, "PVRecPayorStatus"));
    }

    private static async Task<decimal> ReceivedAsync(IDbSession pay, string no, int version, string? exceptReceipt, CancellationToken ct) =>
        await pay.ScalarAsync<decimal?>("SELECT SUM(CheckAmt) FROM ReceiptfrPAY WHERE CASNumber = @no AND ClaimVersion = @version AND (@except IS NULL OR ReceiptNo <> @except)",
            new { no, version = version.ToString(), except = exceptReceipt }, ct) ?? 0;

    /// <summary>The next receipt number for the payor's receipt indicator (legacy GenerateSRecNo: "RF" + RecSeq.RecSeqNo + 1).</summary>
    private static async Task<string> NextReceiptNoAsync(IDbSession pay, string rcInd, CancellationToken ct)
    {
        var seq = await pay.ScalarAsync<string>("SELECT RecSeqNo FROM RecSeq WITH (UPDLOCK, HOLDLOCK) WHERE RCInd = @rcInd", new { rcInd }, ct);
        var next = seq is null ? "10001" : (int.Parse(seq.Trim()) + 1).ToString("00000");
        if (seq is null) await pay.ExecuteAsync("INSERT INTO RecSeq (RecSeqNo, RCInd) VALUES (@next, @rcInd)", new { next, rcInd }, ct);
        else await pay.ExecuteAsync("UPDATE RecSeq SET RecSeqNo = @next WHERE RCInd = @rcInd", new { next, rcInd }, ct);
        return "RF" + next;
    }

    /// <summary>The next payment voucher number (legacy GeneratePaymentPVNo: "CF" + PVSeq.PVSeqNo + 1).</summary>
    private static async Task<string> NextPvNoAsync(IDbSession pay, CancellationToken ct)
    {
        var seq = await pay.ScalarAsync<string>("SELECT TOP 1 PVSeqNo FROM PVSeq WITH (UPDLOCK, HOLDLOCK)", null, ct);
        var next = seq is null ? "10001" : (int.Parse(seq.Trim()) + 1).ToString("00000");
        if (seq is null) await pay.ExecuteAsync("INSERT INTO PVSeq (PVSeqNo) VALUES (@next)", new { next }, ct);
        else await pay.ExecuteAsync("UPDATE PVSeq SET PVSeqNo = @next", new { next }, ct);
        return "CF" + next;
    }

    private static (int Month, int Year) Period(PayorReceiptHeader h) => (h.AccountMonth ?? h.ReceiptDate!.Value.Month, h.AccountYear ?? h.ReceiptDate!.Value.Year);

    public async Task<PayorReceiptResult> SaveAsync(PayorReceiptSaveRequest request, string user, CancellationToken ct = default)
    {
        if (PayorReceiptRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var h = request.Header;
        var authorised = (await ScalarAsync<string>(GenesisDb.Maintenance, "SELECT AuthorisedStatus FROM USR WHERE USRCode = @user", new { user }, ct))?.Trim() == "Y";
        var who = Cut(user, 10);
        var (month, year) = Period(h);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var pay = await Db.BeginSessionAsync(GenesisDb.Payment, ct);
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var claims = new List<(PayorReceiptClaim Input, SaveClaim Claim, decimal Received)>();
        foreach (var input in request.Claims)
        {
            var claim = await ClaimAsync(ws, input.CaseNumber, input.Version, ct);
            if (claim.Status == "F" && !authorised) throw BusinessRuleException.Conflict($"{claim.CaseNumber}-{claim.Version}: Record has been paid !");
            claims.Add((input, claim, await ReceivedAsync(pay, claim.CaseNumber, claim.Version, null, ct)));
        }
        var payors = claims.Select(c => c.Claim.Payor).OfType<string>().Distinct().ToList();
        var rcInds = (await main.QueryAsync("SELECT PAYCode, RCInd FROM PAY WHERE PAYCode IN @payors", new { payors }, ct)).Select(r => S(r, "RCInd") ?? "").Distinct().ToList();
        if (rcInds.Count > 1) throw new BusinessRuleException("The ticked claims belong to payors with different receipt series: receipt them separately.", field: "claims");

        var pvNo = await NextPvNoAsync(pay, ct);
        var receiptNo = await NextReceiptNoAsync(pay, rcInds.FirstOrDefault() ?? "", ct);
        var remark = PayorReceiptRules.CaseRemark(DateOnly.FromDateTime(DateTime.Today), receiptNo, h.ReceiptDate!.Value, user);
        foreach (var (input, claim, received) in claims)
        {
            var status = PayorReceiptRules.Status(claim.Amount, received, input.Paying);
            await pay.ExecuteAsync("""
                INSERT INTO ReceiptfrPAY (CASNumber, ClaimVersion, ReceiptNo, ReceiptDate, ReceiptAmt, ChequeNo, Remark, BordxNo, PAYLastUpdatedate, PAYLastUpdateUser, RecPAYAmt,
                    CheckAmt, PAYPVNo, PAYPVDate, RECAccPeriodMth, RECAccPeriodYR, PAYBankCharge, PAYGainLoss, PAYMisc, PAYDiscount, PAYWriteOff, PAYMedixReceipt, PAYRemarks)
                VALUES (@no, @version, @receiptNo, @receiptDate, @amount, @chequeNo, @remark, @bordx, @today, @who, @amount,
                    @paying, @pvNo, @pvDate, @month, @year, @bankCharge, @gainLoss, @misc, @discount, @writeOff, @medix, @caseRemark)
                """, ReceiptArgs(claim, input, h, receiptNo, pvNo, month, year, who), ct);
            await ws.ExecuteAsync("UPDATE Claim SET PVRecPayorStatus = @status WHERE CASNumber = @no AND ClaimVersion = @version", new { status, no = claim.CaseNumber, version = claim.Version }, ct);
            await CaseRemarkLog.AppendAsync(main, claim.CaseNumber, remark, ct);
            await SaveContraAsync(pay, receiptNo, claim, input, ct);
            // Legacy UpdatePayment2HOs: a fully paid claim records the hospital payment under the voucher (a partial receipt leaves it).
            if (status == "F") await SaveHospitalPaymentAsync(pay, claim, input, h, pvNo, status, who, ct);
        }
        await ws.CommitAsync(ct);
        await pay.CommitAsync(ct);
        await main.CommitAsync(ct);
        audit.Record(new AuditEntry("Receipt.Payor.Save", "ReceiptfrPAY", receiptNo, user, new Dictionary<string, object?> { ["pvNo"] = pvNo, ["header"] = h, ["claims"] = request.Claims }));
        return await ResultAsync(receiptNo, pvNo, ct);
    }

    /// <summary>The manual form's bordereau mode: the listing's claims grouped by bordereau, filtered by the bordereau's receipt status.</summary>
    public async Task<PayorReceiptBordxListing> SearchBordereauxAsync(PayorReceiptSearch search, CancellationToken ct = default)
    {
        var listing = await SearchAsync(search with { Status = null }, ct);
        var status = search.Status?.Trim().ToLowerInvariant();
        var rows = listing.Lines.Where(l => l.BordxNo is not null).GroupBy(l => l.BordxNo!, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var claims = g.GroupBy(l => (l.CaseNumber, l.Version)).Select(c => c.First()).ToList();
            var receipts = g.Where(l => l.ReceiptNo is not null).OrderBy(l => l.ReceiptDate).ToList();
            var amount = claims.Sum(c => c.Amount); var received = claims.Sum(c => c.TotalReceived);
            var last = receipts.LastOrDefault();
            return new PayorReceiptBordx(g.Key, claims[0].Payor, claims[0].BordxDate, claims.Count, amount, received, amount - received,
                PayorReceiptRules.BordxStatus(claims.Select(c => c.Status).ToList()), receipts.Count == 0 ? null : string.Join(", ", receipts.Select(r => r.ReceiptNo).Distinct()),
                last?.ReceiptDate, last?.ChequeNo, last?.PvNo);
        }).Where(b => status switch
        {
            PayorReceiptStatus.Paid => b.Status == "F",
            PayorReceiptStatus.Unpaid => b.Status is null,
            PayorReceiptStatus.Partial => b.Status == "P",
            _ => true,
        }).OrderBy(b => b.BordxNo).ToList();
        return new PayorReceiptBordxListing(rows, listing.Truncated);
    }

    /// <summary>
    /// Receipt > Payor (Manual) save (legacy FrmReceiptPayorManual cmdSave_Click, ProcessWS / SaveRecByBordx): the typed receipt number on every claim,
    /// the claims' PVRecPayorStatus, the contra, and each bordereau's receipt status and date. A receipt number already in use is refused (legacy would add
    /// the claims to it); a bordereau with a received claim is refused in bordereau mode (legacy looked only at the bordereau's own status, which the
    /// normal form never sets, so every FWHS claim would have been receipted again).
    /// </summary>
    public async Task<PayorReceiptResult> SaveManualAsync(ManualPayorReceiptRequest request, string user, CancellationToken ct = default)
    {
        if (PayorReceiptRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var h = request.Header;
        var receiptNo = request.ReceiptNo!.Trim(); var pvNo = Blank(request.PvNo);
        var authorised = (await ScalarAsync<string>(GenesisDb.Maintenance, "SELECT AuthorisedStatus FROM USR WHERE USRCode = @user", new { user }, ct))?.Trim() == "Y";
        var who = Cut(user, 10);
        var (month, year) = Period(h);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var pay = await Db.BeginSessionAsync(GenesisDb.Payment, ct);
        if (await pay.ScalarAsync<int>("SELECT COUNT(*) FROM ReceiptfrPAY WITH (UPDLOCK, HOLDLOCK) WHERE ReceiptNo = @receiptNo", new { receiptNo }, ct) > 0)
            throw BusinessRuleException.Conflict($"Receipt No {receiptNo} is already used.");
        var claims = new List<(PayorReceiptClaim Input, SaveClaim Claim, decimal Received)>();
        if (request.Bordereaux is { Count: > 0 } bordereaux)
            foreach (var bordx in bordereaux.Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var keys = await ws.QueryAsync("SELECT CASNumber, ClaimVersion FROM Claim WHERE BordxRefNo = @bordx ORDER BY CASNumber, ClaimVersion", new { bordx }, ct);
                if (keys.Count == 0) throw BusinessRuleException.NotFound($"Bordereau {bordx} has no claims.");
                var own = new List<(PayorReceiptClaim Input, SaveClaim Claim, decimal Received)>();
                foreach (var k in keys)
                {
                    var claim = await ClaimAsync(ws, S(k, "CASNumber")!, I(k, "ClaimVersion") ?? 0, ct);
                    own.Add((new PayorReceiptClaim(claim.CaseNumber, claim.Version, claim.Amount), claim, await ReceivedAsync(pay, claim.CaseNumber, claim.Version, null, ct)));
                }
                var status = PayorReceiptRules.BordxStatus(own.Select(c => c.Claim.Status).ToList());
                if (status == "F" && !authorised) throw BusinessRuleException.Conflict($"{bordx}: Record has been paid !");
                if (status == "P" || own.Any(c => c.Received > 0 && c.Claim.Status != "F"))
                    throw BusinessRuleException.Conflict($"{bordx}: Record has been Partial Paid ! Please Pay by Receipt Type - Worksheet !");
                claims.AddRange(own);
            }
        else
            foreach (var input in request.Claims!)
            {
                var claim = await ClaimAsync(ws, input.CaseNumber, input.Version, ct);
                if (claim.Status == "F" && !authorised) throw BusinessRuleException.Conflict($"{claim.CaseNumber}-{claim.Version}: Record has been paid !");
                claims.Add((input, claim, await ReceivedAsync(pay, claim.CaseNumber, claim.Version, null, ct)));
            }
        if (PayorReceiptRules.Remark(h, claims.Sum(c => c.Input.Paying)) is { } remark) throw new BusinessRuleException(remark.Message, field: remark.Field);

        foreach (var (input, claim, received) in claims)
        {
            var status = PayorReceiptRules.Status(claim.Amount, received, input.Paying);
            await pay.ExecuteAsync("""
                INSERT INTO ReceiptfrPAY (CASNumber, ClaimVersion, ReceiptNo, ReceiptDate, ReceiptAmt, ChequeNo, Remark, BordxNo, PAYLastUpdatedate, PAYLastUpdateUser, RecPAYAmt,
                    CheckAmt, PAYPVNo, PAYPVDate, RECAccPeriodMth, RECAccPeriodYR, PAYBankCharge, PAYGainLoss, PAYMisc, PAYDiscount, PAYWriteOff, PAYMedixReceipt, PAYRemarks)
                VALUES (@no, @version, @receiptNo, @receiptDate, @amount, @chequeNo, @remark, @bordx, @today, @who, @amount,
                    @paying, @pvNo, @pvDate, @month, @year, @bankCharge, @gainLoss, @misc, @discount, @writeOff, @medix, @caseRemark)
                """, ReceiptArgs(claim, input, h, receiptNo, pvNo, month, year, who), ct);
            await ws.ExecuteAsync("UPDATE Claim SET PVRecPayorStatus = @status WHERE CASNumber = @no AND ClaimVersion = @version", new { status, no = claim.CaseNumber, version = claim.Version }, ct);
            await SaveContraAsync(pay, receiptNo, claim, input, ct);
        }
        // Legacy UpdateBordxStatus: F once every claim of the bordereau is fully received, else P, with the receipt date.
        foreach (var bordx in claims.Select(c => c.Claim.BordxNo).OfType<string>().Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var statuses = (await ws.QueryAsync("SELECT PVRecPayorStatus FROM Claim WHERE BordxRefNo = @bordx", new { bordx }, ct)).Select(r => S(r, "PVRecPayorStatus")).ToList();
            await ws.ExecuteAsync("UPDATE Bordx SET BordxPAYRecDate = @date, BordxPAYFrStatus = @status WHERE BordxRefNo = @bordx",
                new { bordx, date = At(h.ReceiptDate), status = PayorReceiptRules.BordxStatus(statuses) ?? "P" }, ct);
        }
        await ws.CommitAsync(ct);
        await pay.CommitAsync(ct);
        audit.Record(new AuditEntry("Receipt.Payor.Manual", "ReceiptfrPAY", receiptNo, user, new Dictionary<string, object?> { ["pvNo"] = pvNo, ["header"] = h, ["claims"] = request.Claims, ["bordereaux"] = request.Bordereaux }));
        return await ResultAsync(receiptNo, pvNo, ct);
    }

    public async Task<PayorReceiptResult> UpdateAsync(string receiptNo, PayorReceiptUpdateRequest request, string user, CancellationToken ct = default)
    {
        if (PayorReceiptRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var h = request.Header; var input = request.Claim;
        receiptNo = receiptNo.Trim();
        var who = Cut(user, 10);
        var (month, year) = Period(h);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var pay = await Db.BeginSessionAsync(GenesisDb.Payment, ct);
        var rows = await pay.QueryAsync("SELECT CASNumber, ClaimVersion, PAYPVNo FROM ReceiptfrPAY WITH (UPDLOCK) WHERE ReceiptNo = @receiptNo", new { receiptNo }, ct);
        if (rows.Count == 0) throw BusinessRuleException.NotFound($"Receipt {receiptNo} not found.");
        if (!rows.Any(r => Key(S(r, "CASNumber")!, I(r, "ClaimVersion") ?? 0) == Key(input.CaseNumber, input.Version)))
            throw new BusinessRuleException($"Claim {input.CaseNumber}-{input.Version} is not on receipt {receiptNo}.", field: "claim");
        var claim = await ClaimAsync(ws, input.CaseNumber, input.Version, ct);
        var status = PayorReceiptRules.Status(claim.Amount, await ReceivedAsync(pay, claim.CaseNumber, claim.Version, receiptNo, ct), input.Paying);

        // Legacy UPdateWholeRecInfo: the header is the same on every claim of the receipt.
        await pay.ExecuteAsync("""
            UPDATE ReceiptfrPAY SET ReceiptDate = @receiptDate, ReceiptAmt = @amount, RecPAYAmt = @amount, ChequeNo = @chequeNo, Remark = @remark, PAYPVDate = @pvDate,
                RECAccPeriodMth = @month, RECAccPeriodYR = @year, PAYLastUpdatedate = @today, PAYLastUpdateUser = @who
            WHERE ReceiptNo = @receiptNo
            """, new { receiptNo, receiptDate = At(h.ReceiptDate), amount = h.Amount, chequeNo = h.ChequeNo!.Trim(), remark = Blank(h.Remark) ?? "", pvDate = At(h.PvDate), month, year,
            today = DateTime.Today, who }, ct);
        await pay.ExecuteAsync("""
            UPDATE ReceiptfrPAY SET CheckAmt = @paying, PAYBankCharge = @bankCharge, PAYGainLoss = @gainLoss, PAYMisc = @misc, PAYDiscount = @discount, PAYWriteOff = @writeOff,
                PAYMedixReceipt = @medix, PAYRemarks = @caseRemark
            WHERE ReceiptNo = @receiptNo AND CASNumber = @no AND ClaimVersion = @version
            """, ReceiptArgs(claim, input, h, receiptNo, null, month, year, who), ct);
        await ws.ExecuteAsync("UPDATE Claim SET PVRecPayorStatus = @status WHERE CASNumber = @no AND ClaimVersion = @version", new { status, no = claim.CaseNumber, version = claim.Version }, ct);
        await pay.ExecuteAsync("UPDATE Payment2HOS SET PVHOSChequeNoMR = @mr, PVHOSChequeNo = @chequeNo WHERE CASNumber = @no AND ClaimVersion = @version",
            new { mr = Blank(h.ChequeNoMr) ?? "", chequeNo = h.ChequeNo!.Trim(), no = claim.CaseNumber, version = claim.Version.ToString() }, ct);
        await SaveContraAsync(pay, receiptNo, claim, input, ct);
        await ws.CommitAsync(ct);
        await pay.CommitAsync(ct);
        audit.Record(new AuditEntry("Receipt.Payor.Update", "ReceiptfrPAY", receiptNo, user, new Dictionary<string, object?> { ["header"] = h, ["claim"] = input, ["status"] = status }));
        return await ResultAsync(receiptNo, S(rows[0], "PAYPVNo"), ct);
    }

    private static Dictionary<string, object?> ReceiptArgs(SaveClaim claim, PayorReceiptClaim c, PayorReceiptHeader h, string receiptNo, string? pvNo, int month, int year, string who) => new()
    {
        ["no"] = claim.CaseNumber, ["version"] = claim.Version.ToString(), ["receiptNo"] = receiptNo, ["receiptDate"] = At(h.ReceiptDate), ["amount"] = h.Amount, ["chequeNo"] = h.ChequeNo!.Trim(),
        ["remark"] = Blank(h.Remark) ?? "", ["bordx"] = claim.BordxNo ?? "", ["today"] = DateTime.Today, ["who"] = who, ["paying"] = c.Paying, ["pvNo"] = pvNo ?? "", ["pvDate"] = At(h.PvDate),
        ["month"] = month, ["year"] = year, ["bankCharge"] = c.BankCharge ?? 0, ["gainLoss"] = c.GainLoss ?? 0, ["misc"] = c.Misc ?? 0, ["discount"] = c.Discount ?? 0,
        ["writeOff"] = c.WriteOff ?? 0, ["medix"] = c.MedixReceipt ?? 0, ["caseRemark"] = Cut(Blank(c.CaseRemark) ?? "", 200),
    };

    /// <summary>Legacy UpdatePayment2HOs: the claim's hospital payment row is (re)written under the voucher; Payment2HOS has no identity, its key includes the voucher.</summary>
    private static async Task SaveHospitalPaymentAsync(IDbSession pay, SaveClaim claim, PayorReceiptClaim c, PayorReceiptHeader h, string pvNo, string status, string who, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>
        {
            ["no"] = claim.CaseNumber, ["version"] = claim.Version.ToString(), ["pvNo"] = pvNo, ["pvDate"] = At(h.PvDate), ["status"] = status, ["chequeDate"] = At(h.PayChequeDate),
            ["chequeNo"] = h.ChequeNo!.Trim(), ["chequeAmt"] = h.Amount, ["paid"] = c.Paying, ["today"] = DateTime.Today, ["who"] = who, ["bankCharge"] = c.BankCharge ?? 0,
            ["gainLoss"] = c.GainLoss ?? 0, ["misc"] = c.Misc ?? 0, ["discount"] = c.Discount ?? 0, ["excess"] = c.WriteOff ?? 0, ["medix"] = c.MedixReceipt ?? 0,
            ["caseRemark"] = Cut(Blank(c.CaseRemark) ?? "", 200), ["mr"] = Blank(h.ChequeNoMr) ?? "", ["bank"] = Blank(h.Bank) ?? "",
        };
        const string set = """
            PVHOSNo = @pvNo, PVHOSDate = @pvDate, PVPayHOSStatus = @status, PVStatus = 'F', PVHOSChequeDate = @chequeDate, PVHOSChequeNo = @chequeNo, PVHOSChequeAmt = @chequeAmt,
            HOSPaidAmt = @paid, Pay2HOSLastUpdateDate = @today, Pay2HOSLastUpdateUser = @who, PVBankCharges = @bankCharge, PVHOSClmGainLoss = @gainLoss, PVHOSMiscDiff = @misc,
            PVHOSDiscount = @discount, PVExcess = @excess, PVMedixPayment = @medix, PVHOSCaseRemark = @caseRemark, PVHOSChequeNoMR = @mr, PVBank = @bank
            """;
        if (await pay.ExecuteAsync($"UPDATE Payment2HOS SET {set} WHERE CASNumber = @no AND ClaimVersion = @version", args, ct) == 0)
            await pay.ExecuteAsync("""
                INSERT INTO Payment2HOS (CASNumber, ClaimVersion, PVHOSNo, PVHOSDate, PVPayHOSStatus, PVStatus, PVHOSChequeDate, PVHOSChequeNo, PVHOSChequeAmt, HOSPaidAmt,
                    Pay2HOSLastUpdateDate, Pay2HOSLastUpdateUser, PVBankCharges, PVHOSClmGainLoss, PVHOSMiscDiff, PVHOSDiscount, PVExcess, PVMedixPayment, PVHOSCaseRemark, PVHOSChequeNoMR, PVBank)
                VALUES (@no, @version, @pvNo, @pvDate, @status, 'F', @chequeDate, @chequeNo, @chequeAmt, @paid,
                    @today, @who, @bankCharge, @gainLoss, @misc, @discount, @excess, @medix, @caseRemark, @mr, @bank)
                """, args, ct);
    }

    /// <summary>Legacy UpdateContra (never called by the legacy save): the contra case set against the claim on this receipt.</summary>
    private static async Task SaveContraAsync(IDbSession pay, string receiptNo, SaveClaim claim, PayorReceiptClaim c, CancellationToken ct)
    {
        if (Blank(c.ContraCase) is not { } contraCase) return;
        var args = new { receiptNo, no = claim.CaseNumber, version = claim.Version.ToString(), amount = c.Paying, contraCase, contraVersion = Blank(c.ContraVersion) ?? "", contraAmount = c.ContraAmount ?? 0 };
        if (await pay.ExecuteAsync("""
                UPDATE ContraAllocation SET CaseAmt = @amount, ContraCaseNo = @contraCase, ContraVersionNo = @contraVersion, ContraCaseAmt = @contraAmount
                WHERE RefNo = @receiptNo AND CaseNo = @no AND VersionNo = @version
                """, args, ct) == 0)
            await pay.ExecuteAsync("""
                INSERT INTO ContraAllocation (RefNo, CaseNo, VersionNo, CaseAmt, ContraCaseNo, ContraVersionNo, ContraCaseAmt)
                VALUES (@receiptNo, @no, @version, @amount, @contraCase, @contraVersion, @contraAmount)
                """, args, ct);
    }

    private async Task<PayorReceiptResult> ResultAsync(string receiptNo, string? pvNo, CancellationToken ct)
    {
        var listing = await SearchAsync(new PayorReceiptSearch(null, null, null, null, null, receiptNo, receiptNo, null, null, null, null, null, null, null, null), ct);
        return new PayorReceiptResult(receiptNo, pvNo, listing.Lines);
    }
}
