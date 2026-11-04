using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Payments;

/// <summary>
/// Payment > To Hospital (legacy FrmPaymentHospital). The legacy listing read VIEW_Payment2HOSNew2018 (payment database, joining the worksheet and main
/// databases) with one row per claim invoice; here each database is read on its own and joined in memory, one row per claim voucher. Legacy differences:
/// Update keys the edited claim by case and version (legacy matched it by PVHOSIndex, which is always NULL in FWHS, so every claim of the voucher got the
/// edited claim's amounts and the save then tried to insert a duplicate row); new vouchers are not created here (see <see cref="HospitalPaymentRules"/>).
/// </summary>
internal sealed class HospitalPaymentRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IHospitalPaymentRepository
{
    private const int Chunk = 1000;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
    private static (string, int) Key(string no, int version) => (no.Trim().ToUpperInvariant(), version);
    private static int Version(IDictionary<string, object?> r, string c) => int.TryParse(S(r, c), out var v) ? v : 0;

    public async Task<HospitalPaymentOptions> OptionsAsync(CancellationToken ct = default)
    {
        var hlt = (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, HLTDescription FROM HLT ORDER BY HLTCode", null, ct))
            .Select(r => new HospitalPaymentOption(S(r, "HLTCode")!, S(r, "HLTDescription") ?? "")).ToList();
        var payors = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY WHERE ISNULL(PAYCode, '') NOT IN ('', 'MH') ORDER BY PAYCode", null, ct))
            .Select(r => new HospitalPaymentOption(S(r, "PAYCode")!, S(r, "PAYCompanyName") ?? "")).ToList();
        var hospitals = (await QueryAsync(GenesisDb.Main, "SELECT HOSCode, HOSName, HOSPayee FROM HOS WHERE ISNULL(HOSName, '') <> '' ORDER BY HOSName", null, ct))
            .Select(r => new HospitalPaymentOption(S(r, "HOSCode")!, S(r, "HOSName")!)).ToList();
        return new HospitalPaymentOptions(hlt, payors, hospitals);
    }

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(GenesisDb db, string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await QueryAsync(db, sql, new { keys = chunk }, ct));
        return all;
    }

    private static void Range(List<string> where, Dictionary<string, object?> args, string column, string name, object? from, object? to, bool dates = false)
    {
        if (from is not null) { args[name + "From"] = from; where.Add($"{column} >= @{name}From"); }
        if (to is not null) { args[name + "To"] = to; where.Add(dates ? $"{column} < DATEADD(day, 1, @{name}To)" : $"{column} <= @{name}To"); }
    }

    public async Task<HospitalPaymentListing> SearchAsync(HospitalPaymentSearch s, CancellationToken ct = default)
    {
        var max = HospitalPaymentRules.MaxLines + 1;
        // Voucher filters pick the claims first in the payment database, the MRN in the main database; the claim filters narrow them.
        HashSet<string>? keys = null;
        void Narrow(IEnumerable<string> found) { var set = found.ToHashSet(StringComparer.OrdinalIgnoreCase); if (keys is null) keys = set; else keys.IntersectWith(set); }
        var pw = new List<string>(); var pa = new Dictionary<string, object?> { ["max"] = max };
        Range(pw, pa, "PVHOSNo", "pv", Blank(s.PvNoFrom), Blank(s.PvNoTo));
        Range(pw, pa, "PVHOSDate", "pvDate", At(s.PvDateFrom), At(s.PvDateTo), true);
        Range(pw, pa, "PVSunHOSNo", "sun", Blank(s.SunNoFrom), Blank(s.SunNoTo));
        Range(pw, pa, "PVSunHOSDate", "sunDate", At(s.SunDateFrom), At(s.SunDateTo), true);
        if (pw.Count > 0)
            Narrow((await QueryAsync(GenesisDb.Payment, $"SELECT DISTINCT TOP (@max) CASNumber FROM Payment2HOS WHERE {string.Join(" AND ", pw)}", pa, ct)).Select(r => S(r, "CASNumber")).OfType<string>());
        var mw = new List<string>(); var ma = new Dictionary<string, object?> { ["max"] = max };
        Range(mw, ma, "CASMRNNo", "mrn", Blank(s.MrnFrom), Blank(s.MrnTo));
        if (mw.Count > 0)
            Narrow((await QueryAsync(GenesisDb.Main, $"SELECT TOP (@max) CASNumber FROM CAS WHERE {string.Join(" AND ", mw)}", ma, ct)).Select(r => S(r, "CASNumber")).OfType<string>());

        var where = new List<string> { "c.CLMPayableByMedix2H <> 0", "LEFT(c.CASNumber, 2) <> 'MH'" };
        var args = new Dictionary<string, object?> { ["max"] = max };
        var narrowed = keys is not null;
        if (Blank(s.Payor) is { } payor) { args["payor"] = payor; where.Add("LEFT(c.CASNumber, 2) = @payor"); narrowed = true; }
        if (Blank(s.HealthType) is { } hlt)
        {
            args["hltPayors"] = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode FROM PAY WHERE HLTCode = @hlt", new { hlt }, ct)).Select(r => S(r, "PAYCode")).OfType<string>().ToArray();
            where.Add("LEFT(c.CASNumber, 2) IN @hltPayors");
        }
        if (Blank(s.WorksheetStatus) is { } wss) { args["wss"] = wss; where.Add("c.CLMWSStatus = @wss"); }
        var claimFrom = Blank(s.ClaimNoFrom); var claimTo = Blank(s.ClaimNoTo);
        if (claimFrom is not null && claimTo is null) { args["claim"] = claimFrom; where.Add("c.CASNumber = @claim"); narrowed = true; }
        else if (claimFrom is not null || claimTo is not null) { Range(where, args, "c.CASNumber", "claim", claimFrom, claimTo); narrowed = true; }
        var before = where.Count;
        Range(where, args, "c.ClaimCloseDate", "claimDate", At(s.ClaimDateFrom), At(s.ClaimDateTo), true);
        Range(where, args, "b.BordxDate", "bordxDate", At(s.BordxDateFrom), At(s.BordxDateTo), true);
        var invoice = new List<string>();
        Range(invoice, args, "i.InvNo", "inv", Blank(s.InvoiceNoFrom), Blank(s.InvoiceNoTo));
        Range(invoice, args, "i.InvDate", "invDate", At(s.InvoiceDateFrom), At(s.InvoiceDateTo), true);
        if (Blank(s.Hospital) is { } hos) { args["hos"] = hos; invoice.Add("i.INVPayee = @hos"); }
        // The legacy view listed claims through their invoices, so a claim without one is not listed.
        where.Add($"EXISTS (SELECT 1 FROM ClaimInvoice i WHERE i.CASNumber = c.CASNumber AND i.INVWSversion = CONVERT(nvarchar(2), c.ClaimVersion){string.Concat(invoice.Select(x => " AND " + x))})");
        if (!narrowed && where.Count - before <= 1 && invoice.Count == 0)
            throw new BusinessRuleException("Please narrow the search (payor, hospital, claim, voucher, invoice or dates).", field: "search");

        var sql = $"""
            SELECT {(keys is null ? "TOP (@max)" : "")} c.CASNumber, c.ClaimVersion, c.BordxRefNo, c.CLMPayableByMedix2H, c.CLMPayableByMedix2M, c.CLMAppStatus, c.CLMWSStatus, c.ClaimCloseDate, b.BordxDate
            FROM Claim c LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo
            WHERE {string.Join(" AND ", where)} {(keys is null ? "" : "AND c.CASNumber IN @keys")}
            ORDER BY c.CASNumber, c.ClaimVersion
            """;
        var claims = new List<IDictionary<string, object?>>();
        if (keys is null) claims = await QueryAsync(GenesisDb.Worksheet, sql, args, ct);
        else
            foreach (var chunk in keys.Chunk(Chunk))
                claims.AddRange(await QueryAsync(GenesisDb.Worksheet, sql, new Dictionary<string, object?>(args) { ["keys"] = chunk }, ct));
        var lines = await LinesAsync(claims, ct);
        lines = lines.Where(l => HospitalPaymentStatus.Matches(s.Status, l.Status) && (!s.WithoutPv || l.PvNo is null)
            && (pw.Count == 0 || l.PvNo is not null && Within(l.PvNo, s.PvNoFrom, s.PvNoTo) && Within(l.PvDate, s.PvDateFrom, s.PvDateTo)
                && Within(l.SunNo, s.SunNoFrom, s.SunNoTo) && Within(l.SunDate, s.SunDateFrom, s.SunDateTo))).ToList();
        var truncated = lines.Count > HospitalPaymentRules.MaxLines || claims.Count >= max;
        return new HospitalPaymentListing(lines.Take(HospitalPaymentRules.MaxLines).ToList(), truncated);
    }

    private static bool Within(string? value, string? from, string? to) =>
        (Blank(from) is not { } f || value is not null && string.Compare(value, f, StringComparison.OrdinalIgnoreCase) >= 0)
        && (Blank(to) is not { } t || value is not null && string.Compare(value, t, StringComparison.OrdinalIgnoreCase) <= 0);

    private static bool Within(DateOnly? value, DateOnly? from, DateOnly? to) => (from is null || value >= from) && (to is null || value <= to);

    /// <summary>The listing rows of the claims: one per voucher of the claim, or one without a voucher when it has none.</summary>
    private async Task<List<HospitalPaymentLine>> LinesAsync(List<IDictionary<string, object?>> claims, CancellationToken ct)
    {
        var cases = claims.Select(r => S(r, "CASNumber")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var invoices = (await InChunksAsync(GenesisDb.Worksheet, "SELECT CASNumber, INVWSversion, InvNo, InvDate, INVPayee, INVOrigRecStatus, INVOrigRecDate FROM ClaimInvoice WHERE CASNumber IN @keys", cases, ct))
            .ToLookup(r => Key(S(r, "CASNumber")!, Version(r, "INVWSversion")));
        var payments = (await InChunksAsync(GenesisDb.Payment, "SELECT * FROM Payment2HOS WHERE CASNumber IN @keys", cases, ct)).ToLookup(r => Key(S(r, "CASNumber")!, Version(r, "ClaimVersion")));
        var contras = (await InChunksAsync(GenesisDb.Payment, "SELECT RefNo, CaseNo, VersionNo, ContraCaseNo, ContraVersionNo, ContraCaseAmt FROM ContraAllocation WHERE CaseNo IN @keys", cases, ct))
            .GroupBy(r => (S(r, "RefNo") ?? "", Key(S(r, "CaseNo") ?? "", Version(r, "VersionNo")))).ToDictionary(g => g.Key, g => g.First());
        var receipts = (await InChunksAsync(GenesisDb.Payment, "SELECT CASNumber, ClaimVersion, ReceiptNo, CheckAmt FROM ReceiptfrPAY WHERE CASNumber IN @keys", cases, ct))
            .ToLookup(r => Key(S(r, "CASNumber")!, Version(r, "ClaimVersion")));
        var cas = (await InChunksAsync(GenesisDb.Main, "SELECT c.CASNumber, c.MBMNumber, c.CASHOSCode, c.CASDateAdmitted, c.CASDischargeDate, c.CASMRNNo, c.ClaimID, m.MBMName FROM CAS c LEFT JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE c.CASNumber IN @keys", cases, ct))
            .GroupBy(r => S(r, "CASNumber")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hosCodes = invoices.SelectMany(g => g).Select(r => S(r, "INVPayee")).Concat(cas.Values.Select(r => S(r, "CASHOSCode"))).OfType<string>();
        var hospitals = (await InChunksAsync(GenesisDb.Main, "SELECT HOSCode, HOSName, HOSPayee FROM HOS WHERE HOSCode IN @keys", hosCodes, ct))
            .GroupBy(r => S(r, "HOSCode")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var lines = new List<HospitalPaymentLine>();
        foreach (var c in claims)
        {
            var no = S(c, "CASNumber")!; var version = I(c, "ClaimVersion") ?? 0; var key = Key(no, version);
            var inv = invoices[key].OrderBy(r => D(r, "InvDate")).ToList();
            cas.TryGetValue(no, out var cs);
            var hosCode = inv.Select(r => S(r, "INVPayee")).OfType<string>().FirstOrDefault() ?? (cs is null ? null : S(cs, "CASHOSCode"));
            var hos = hosCode is null ? null : hospitals.GetValueOrDefault(hosCode);
            var payable = Dec(c, "CLMPayableByMedix2H") ?? 0;
            var own = payments[key].OrderBy(r => D(r, "PVHOSDate")).ThenBy(r => S(r, "PVHOSNo")).ToList();
            var totalPaid = own.Sum(r => Dec(r, "HOSPaidAmt") ?? 0);
            var rec = receipts[key].ToList();
            var receiptNos = rec.Select(r => S(r, "ReceiptNo")).OfType<string>().Distinct().ToList();
            var first = inv.FirstOrDefault();
            HospitalPaymentLine Line(IDictionary<string, object?>? p)
            {
                var pv = p is null ? null : S(p, "PVHOSNo");
                var contra = pv is null ? null : contras.GetValueOrDefault((pv, key));
                string? P(string col) => p is null ? null : S(p, col);
                decimal? M(string col) => p is null ? null : Dec(p, col);
                return new HospitalPaymentLine(no, version, P("PVPayHOSStatus"), hos is null ? null : S(hos, "HOSName"),
                    inv.Count == 0 ? null : string.Join(", ", inv.Select(r => S(r, "InvNo")).OfType<string>().Distinct()), first is null ? null : Day(first, "InvDate"),
                    payable, M("HOSPaidAmt") ?? 0, totalPaid, payable - totalPaid, Dec(c, "CLMPayableByMedix2M") ?? 0, S(c, "BordxRefNo"), cs is null ? null : S(cs, "MBMNumber"),
                    cs is null ? null : S(cs, "MBMName"), cs is null ? null : Day(cs, "CASDateAdmitted"), cs is null ? null : Day(cs, "CASDischargeDate"), pv,
                    p is null ? null : Day(p, "PVHOSDate"), P("PVStatus"), P("PVHOSChequeNo"), p is null ? null : Day(p, "PVHOSChequeDate"), M("PVHOSChequeAmt"),
                    int.TryParse(P("HOSAccPeriodMth"), out var mth) ? mth : null, int.TryParse(P("HOSAccPeriodYr"), out var yr) ? yr : null, P("HOSRemark"),
                    receiptNos.Count == 0 ? null : string.Join(", ", receiptNos), rec.Count == 0 ? null : rec.Sum(r => Dec(r, "CheckAmt") ?? 0),
                    M("PVBankCharges"), M("PVHOSClmGainLoss"), M("PVHOSDiscount"), M("PVHOSMiscDiff"), contra is null ? null : S(contra, "ContraCaseNo"),
                    contra is null ? null : S(contra, "ContraVersionNo"), contra is null ? null : Dec(contra, "ContraCaseAmt"), P("PVHOSCaseRemark"), M("PVExcess"), M("PVMedixPayment"),
                    S(c, "CLMAppStatus"), P("PVSunHOSNo"), p is null ? null : Day(p, "PVSunHOSDate"), S(c, "CLMWSStatus"), Day(c, "ClaimCloseDate"), Day(c, "BordxDate"),
                    first is null ? null : S(first, "INVOrigRecStatus"), first is null ? null : Day(first, "INVOrigRecDate"), P("PVPayee"),
                    p is null ? null : Blank(string.Concat(S(p, "PVAmtWording"), " ", S(p, "PVAmtWordingTwo"))), hos is null ? null : S(hos, "HOSPayee"),
                    cs is null ? null : S(cs, "CASMRNNo"), cs is null ? null : S(cs, "ClaimID"), P("PVBank"));
            }
            if (own.Count == 0) lines.Add(Line(null));
            else lines.AddRange(own.Select(Line));
        }
        return lines;
    }

    public async Task<HospitalPaymentResult> UpdateAsync(string pvNo, HospitalPaymentUpdateRequest request, string user, CancellationToken ct = default)
    {
        if (HospitalPaymentRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var h = request.Header; var c = request.Claim;
        pvNo = pvNo.Trim();
        var (line1, line2) = HospitalPaymentRules.Wording(h.ChequeAmount!.Value);

        await using var pay = await Db.BeginSessionAsync(GenesisDb.Payment, ct);
        var rows = await pay.QueryAsync("SELECT CASNumber, ClaimVersion FROM Payment2HOS WITH (UPDLOCK) WHERE PVHOSNo = @pvNo", new { pvNo }, ct);
        if (rows.Count == 0) throw BusinessRuleException.NotFound($"Payment voucher {pvNo} not found.");
        var row = rows.FirstOrDefault(r => Key(S(r, "CASNumber")!, Version(r, "ClaimVersion")) == Key(c.CaseNumber, c.Version))
            ?? throw new BusinessRuleException($"Claim {c.CaseNumber}-{c.Version} is not on voucher {pvNo}.", field: "claim");
        var no = S(row, "CASNumber")!; var version = S(row, "ClaimVersion")!;

        // Legacy cmdUpdate_Click: the voucher header is written on every claim of the voucher.
        await pay.ExecuteAsync("""
            UPDATE Payment2HOS SET PVHOSDate = @pvDate, PVStatus = COALESCE(@pvStatus, PVStatus), PVHOSChequeNo = @chequeNo, PVHOSChequeDate = @chequeDate, PVHOSChequeAmt = @chequeAmt,
                HOSAccPeriodMth = COALESCE(@month, HOSAccPeriodMth), HOSAccPeriodYr = COALESCE(@year, HOSAccPeriodYr), HOSRemark = @remark, PVPayee = @payee,
                PVAmtWording = @line1, PVAmtWordingTwo = @line2, Pay2HOSLastUpdateDate = @today, Pay2HOSLastUpdateUser = @who
            WHERE PVHOSNo = @pvNo
            """, new
        {
            pvNo, pvDate = At(h.PvDate), pvStatus = Blank(h.PvStatus), chequeNo = Cut(h.ChequeNo!.Trim(), 30), chequeDate = At(h.ChequeDate), chequeAmt = h.ChequeAmount,
            month = h.AccountMonth?.ToString("00"), year = h.AccountYear?.ToString(), remark = Cut(Blank(h.Remark) ?? "", 200), payee = Cut(h.Payee!.Trim(), 200),
            line1 = Cut(line1, 300), line2 = Cut(line2, 50), today = DateTime.Today, who = Cut(user, 10),
        }, ct);
        await pay.ExecuteAsync("""
            UPDATE Payment2HOS SET HOSPaidAmt = @paid, PVBankCharges = @bankCharge, PVHOSClmGainLoss = @gainLoss, PVHOSDiscount = @discount, PVHOSMiscDiff = @misc,
                PVExcess = @excess, PVMedixPayment = @medix, PVHOSCaseRemark = @caseRemark
            WHERE PVHOSNo = @pvNo AND CASNumber = @no AND ClaimVersion = @version
            """, new
        {
            pvNo, no, version, paid = c.Paid, bankCharge = c.BankCharge ?? 0, gainLoss = c.GainLoss ?? 0, discount = c.Discount ?? 0, misc = c.Misc ?? 0, excess = c.Excess ?? 0,
            medix = c.MedixPayment ?? 0, caseRemark = Cut(Blank(c.CaseRemark) ?? "", 200),
        }, ct);
        // Legacy UpdateContraAlloc / DeleteContraAllocation: the contra case set against the claim on this voucher.
        if (Blank(c.ContraCase) is not { } contraCase)
            await pay.ExecuteAsync("DELETE FROM ContraAllocation WHERE RefNo = @pvNo AND CaseNo = @no AND VersionNo = @version", new { pvNo, no, version }, ct);
        else
        {
            var args = new { pvNo, no, version, paid = c.Paid, contraCase, contraVersion = Blank(c.ContraVersion) ?? "", contraAmount = c.ContraAmount ?? 0 };
            if (await pay.ExecuteAsync("""
                    UPDATE ContraAllocation SET CaseAmt = @paid, ContraCaseNo = @contraCase, ContraVersionNo = @contraVersion, ContraCaseAmt = @contraAmount
                    WHERE RefNo = @pvNo AND CaseNo = @no AND VersionNo = @version
                    """, args, ct) == 0)
                await pay.ExecuteAsync("""
                    INSERT INTO ContraAllocation (RefNo, CaseNo, VersionNo, CaseAmt, ContraCaseNo, ContraVersionNo, ContraCaseAmt)
                    VALUES (@pvNo, @no, @version, @paid, @contraCase, @contraVersion, @contraAmount)
                    """, args, ct);
        }
        await pay.CommitAsync(ct);
        audit.Record(new AuditEntry("Payment.Hospital.Update", "Payment2HOS", pvNo, user, new Dictionary<string, object?> { ["header"] = h, ["claim"] = c }));
        var listing = await SearchAsync(new HospitalPaymentSearch(PvNoFrom: pvNo, PvNoTo: pvNo), ct);
        return new HospitalPaymentResult(pvNo, listing.Lines);
    }
}
