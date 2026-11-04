using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Cases;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Bordereaux;

/// <summary>
/// Claim Bordx > Worksheet > Insert Worksheet (legacy FrmWksBordx) and Remove Worksheet (FrmBordxRevise). The legacy listing read VIEW_WS_PendingBordx2 (worksheet database, joining the main
/// database); here the claims and invoices are read from the worksheet database and the case, patient, hospital and diagnosis from the main database by
/// case number. The scanned-document check reads the case's folder in the scanned-documents store (FileStore:ScannedDocsRoot) and is skipped where that
/// store is not configured. Legacy differences: the claimability check
/// compares with "NN" (legacy compared a column it never filled with "N", so it never blocked); in Multiple mode the ticked claims must share the payor and
/// the pay-to, since the bordereau number and status come from them (legacy took the number from the first claim and the status from the last).
/// </summary>
internal sealed class BordxCreationRepository(IDbConnectionFactory connections, IAuditLog audit, ICaseDocumentStore documents) : SqlRepository(connections), IBordxCreationRepository
{
    /// <summary>The last tick check (legacy "Please ensure scanned documents are in system!"), on claims that pass the others.</summary>
    private List<PendingBordxClaim> ScanChecked(List<PendingBordxClaim> lines)
    {
        if (!documents.IsConfigured(CaseDocumentArea.Scanned)) return lines;
        return lines.Select(l => l.Problem is not null ? l
            : l with { Problem = BordxRules.ScanProblem(documents.List(CaseDocumentArea.Scanned, l.CaseNumber.Trim()).Select(f => f.Name)) }).ToList();
    }

    private const int Chunk = 1000;
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static (string, int) Key(string no, int version) => (no.Trim().ToUpperInvariant(), version);

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(GenesisDb db, string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await QueryAsync(db, sql, new { keys = chunk }, ct));
        return all;
    }

    public async Task<PendingBordxListing> PendingAsync(PendingBordxSearch s, CancellationToken ct = default)
    {
        var where = "c.ClaimStatus = 'C' AND c.ClaimBordxStatus = 0";
        var claimNo = string.IsNullOrWhiteSpace(s.ClaimNo) ? null : s.ClaimNo.Trim();
        if (claimNo is not null) where += " AND c.CASNumber = @claimNo";
        if (s.Version is not null) where += " AND c.ClaimVersion = @version";
        var claims = await QueryAsync(GenesisDb.Worksheet, $"""
            SELECT TOP (@take) c.CASNumber, c.ClaimVersion, c.CLMTotalApproved, c.CLMPayableByMedix2H, c.CLMPayableByMedix2M, c.CLMCaseMgmtFees, c.ClaimCloseDate
            FROM Claim c WHERE {where} ORDER BY c.CASNumber, c.ClaimVersion
            """, new { claimNo, version = s.Version, take = BordxRules.MaxClaims + 1 }, ct);
        var truncated = claims.Count > BordxRules.MaxClaims;
        var lines = (await LinesAsync(claims.Take(BordxRules.MaxClaims).ToList(), ct))
            .Where(l => (s.AdmittedFrom is null || l.Admitted >= s.AdmittedFrom) && (s.AdmittedTo is null || l.Admitted <= s.AdmittedTo)
                && (s.DischargedFrom is null || l.Discharged >= s.DischargedFrom) && (s.DischargedTo is null || l.Discharged <= s.DischargedTo)
                && (!s.NoAdmitDate || l.Admitted is null) && (!s.NoDischargeDate || l.Discharged is null)).ToList();
        return new PendingBordxListing(lines, lines.Sum(l => l.TotalApproved), truncated);
    }

    private const string PayToSql = "SELECT CASNumber, INVWSversion, INVPayTo FROM ClaimInvoice WHERE CASNumber IN @keys AND ISNULL(INVPayTo, '') <> ''";

    private const string PatientSql = """
        SELECT p.CASNumber, p.MBMNumber, p.CASStatus, p.MBMPatientCovID, p.CASClaimability, p.MBMCName, p.MBMName, p.CASDateAdmitted, p.CASDischargeDate, p.PAYCode, p.MBMBTBG, p.PLNCode,
            h.HOSName, o.MBMGroupCompany, (SELECT TOP 1 d.ICDCode1 FROM DIAFinal d WHERE d.CASNumber = p.CASNumber) AS ICDCode1
        FROM VIEW_CAS_Patient p LEFT JOIN HOS h ON h.HOSCode = p.CASHOSCode LEFT JOIN MBMOthers o ON o.MBMNumber = p.MBMNumber
        WHERE p.CASNumber IN @keys
        """;

    private async Task<List<PendingBordxClaim>> LinesAsync(List<IDictionary<string, object?>> claims, CancellationToken ct)
    {
        var cases = claims.Select(r => S(r, "CASNumber")).OfType<string>().ToList();
        return ScanChecked(Lines(claims, await InChunksAsync(GenesisDb.Worksheet, PayToSql, cases, ct), await InChunksAsync(GenesisDb.Main, PatientSql, cases, ct)));
    }

    /// <summary>
    /// The claims with their case, patient, hospital, final diagnosis and invoice pay-to (hospital when the claim has both); claims without a case are left
    /// out, as in the legacy view.
    /// </summary>
    private static List<PendingBordxClaim> Lines(IEnumerable<IDictionary<string, object?>> claims, IEnumerable<IDictionary<string, object?>> payToRows, IEnumerable<IDictionary<string, object?>> patientRows)
    {
        var payTo = payToRows.GroupBy(r => Key(S(r, "CASNumber")!, int.TryParse(S(r, "INVWSversion"), out var v) ? v : 0))
            .ToDictionary(g => g.Key, g => g.Select(r => S(r, "INVPayTo")).OrderBy(p => p == "H" ? 0 : 1).First());
        var patients = patientRows.GroupBy(r => S(r, "CASNumber")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<PendingBordxClaim>();
        foreach (var c in claims)
        {
            var no = S(c, "CASNumber")!; var version = I(c, "ClaimVersion") ?? 0;
            if (!patients.TryGetValue(no, out var p)) continue;
            var to = payTo.GetValueOrDefault(Key(no, version));
            var btbg = S(p, "MBMBTBG") ?? "N";
            var discharged = Day(p, "CASDischargeDate"); var hospital = S(p, "HOSName"); var icd = S(p, "ICDCode1"); var claimability = S(p, "CASClaimability");
            var approved = Dec(c, "CLMTotalApproved") ?? 0; var payH = Dec(c, "CLMPayableByMedix2H") ?? 0;
            lines.Add(new PendingBordxClaim(no, version, S(p, "PAYCode"), to, payH, approved,
                BordxRules.Amount(btbg, to, payH, Dec(c, "CLMPayableByMedix2M") ?? 0, approved, Dec(c, "CLMCaseMgmtFees") ?? 0), Day(p, "CASDateAdmitted"), discharged,
                S(p, "MBMPatientCovID") == "00" ? S(p, "MBMName") : S(p, "MBMCName") ?? S(p, "MBMName"), hospital, S(p, "MBMGroupCompany"), S(p, "PLNCode"), icd, btbg, claimability,
                Day(c, "ClaimCloseDate"), BordxRules.Problem(discharged, hospital, icd, claimability)));
        }
        return lines;
    }

    public async Task<CreateBordxResult> CreateAsync(CreateBordxRequest request, string user, CancellationToken ct = default)
    {
        if (request.Claims is not { Count: > 0 }) throw new BusinessRuleException("Please select at least one record to be updated! ", field: "claims");
        if (request.Claims.GroupBy(k => Key(k.CaseNumber, k.Version)).Any(g => g.Count() > 1)) throw new BusinessRuleException("A claim is ticked twice.", field: "claims");
        var today = DateTime.Today;
        var now = DateTime.Now;

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        var claims = new List<IDictionary<string, object?>>();
        foreach (var k in request.Claims)
        {
            var row = (await ws.QueryAsync("""
                SELECT CASNumber, ClaimVersion, CLMTotalApproved, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMCaseMgmtFees, ClaimCloseDate, ClaimStatus, ClaimBordxStatus
                FROM Claim WITH (UPDLOCK) WHERE CASNumber = @no AND ClaimVersion = @version
                """, new { no = k.CaseNumber.Trim(), version = k.Version }, ct)).FirstOrDefault()
                ?? throw BusinessRuleException.NotFound($"Claim {k.CaseNumber}-{k.Version} not found.");
            if (S(row, "ClaimStatus") != "C" || (I(row, "ClaimBordxStatus") ?? 0) != 0)
                throw BusinessRuleException.Conflict($"Claim {k.CaseNumber}-{k.Version} is not waiting for a bordereau (already on one, or not closed).");
            claims.Add(row);
        }
        // The pending listing's checks, on the same data (read in the sessions, so a claim changed meanwhile is seen as it is now).
        var lines = ScanChecked(await SessionLinesAsync(ws, main, claims, ct));
        foreach (var l in lines)
            if (l.Problem is { } problem) throw new BusinessRuleException($"{l.CaseNumber}-{l.Version}: {problem}", field: "claims");
        if (lines.Count < claims.Count) throw BusinessRuleException.NotFound("A ticked claim has no case.");
        var payorOf = (PendingBordxClaim l) => l.CaseNumber[..2].ToUpperInvariant();
        var groups = request.Option == BordxOption.Single
            ? lines.Select(l => new List<PendingBordxClaim> { l }).ToList()
            : [lines];
        if (request.Option == BordxOption.Multiple)
        {
            if (lines.Select(payorOf).Distinct().Count() > 1) throw new BusinessRuleException("The ticked claims belong to different payors: put them on separate bordereaux.", field: "claims");
            if (lines.Select(l => BordxRules.Status(l.PayTo)).Distinct().Count() > 1)
                throw new BusinessRuleException("The ticked claims mix hospital and member payments: put them on separate bordereaux.", field: "claims");
        }

        var created = new List<CreatedBordx>(); var done = new List<CreatedBordxClaim>();
        foreach (var group in groups)
        {
            var payor = payorOf(group[0]);
            var bordxNo = await NextBordxNoAsync(ws, main, payor, ct);
            var status = BordxRules.Status(group[0].PayTo);
            foreach (var l in group)
            {
                await ws.ExecuteAsync("UPDATE Claim SET BordxRefNo = @bordxNo, CLMBordxDate = @today, ClaimBordxStatus = 1, CLMPending = 'N' WHERE CASNumber = @no AND ClaimVersion = @version",
                    new { bordxNo, today, no = l.CaseNumber, version = l.Version }, ct);
                await ws.ExecuteAsync("UPDATE ClaimInvoice SET INVBordxRefNo = @bordxNo, INVBordxed = 1 WHERE CASNumber = @no AND INVWSversion = @version",
                    new { bordxNo, no = l.CaseNumber, version = l.Version.ToString() }, ct);
                await main.ExecuteAsync("UPDATE CAS SET CASPending = 'N' WHERE CASNumber = @no", new { no = l.CaseNumber }, ct);
                await maint.ExecuteAsync("UPDATE CASCrossReference SET CASPending = 'N' WHERE CASNumber = @no", new { no = l.CaseNumber }, ct);
                await CaseRemarkLog.AppendAsync(main, l.CaseNumber, BordxRules.CaseRemark(now, user, bordxNo), ct);
                done.Add(new CreatedBordxClaim(l.CaseNumber, l.Version, bordxNo, DateOnly.FromDateTime(today), l.BordxAmount));
            }
            var amount = group.Sum(l => l.BordxAmount);
            await ws.ExecuteAsync("""
                INSERT INTO Bordx (BordxRefNo, BordxPAYCode, BordxDate, BordxBy, BordxClaimAmt, BordxNoClaims, BordxStatus)
                VALUES (@bordxNo, @payor, @today, @user, @amount, @count, @status)
                """, new { bordxNo, payor, today, user = user.Length > 50 ? user[..50] : user, amount, count = (short)group.Count, status }, ct);
            created.Add(new CreatedBordx(bordxNo, payor, status, group.Count, amount));
        }
        await ws.CommitAsync(ct);
        await main.CommitAsync(ct);
        await maint.CommitAsync(ct);
        audit.Record(new AuditEntry("Bordx.Create", "Bordx", string.Join(",", created.Select(b => b.BordxNo)), user,
            new Dictionary<string, object?> { ["option"] = request.Option.ToString(), ["claims"] = done }));
        return new CreateBordxResult(created, done);
    }

    /// <summary>The listing lines of the ticked claims, read inside the save's sessions.</summary>
    private static async Task<List<PendingBordxClaim>> SessionLinesAsync(IDbSession ws, IDbSession main, List<IDictionary<string, object?>> claims, CancellationToken ct)
    {
        var keys = claims.Select(r => S(r, "CASNumber")!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Lines(claims, await ws.QueryAsync(PayToSql, new { keys }, ct), await main.QueryAsync(PatientSql, new { keys }, ct));
    }

    private const string ClaimSql = "SELECT CASNumber, ClaimVersion, CLMTotalApproved, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMCaseMgmtFees, ClaimCloseDate, ClaimOpenDate, CLMBordxWSASCII";

    /// <summary>
    /// The bordereau and its claims (legacy stored procedure BordxRevise). The procedure joined the HIS cross-reference tables, so on FWHS it found no claims
    /// and Remove Worksheet could not be used; here the FWHS case and patient are read.
    /// </summary>
    public async Task<BordxRevise?> ReviseAsync(string bordxNo, CancellationToken ct = default)
    {
        var bordx = (await QueryAsync(GenesisDb.Worksheet, "SELECT BordxRefNo, BordxDate, BordxPrintedDate, BordxClaimAmt, BordxNoClaims, BordxPAYToStatus, BordxMNRBToStatus FROM Bordx WHERE BordxRefNo = @bordxNo",
            new { bordxNo = bordxNo.Trim() }, ct)).FirstOrDefault();
        if (bordx is null) return null;
        var claims = await QueryAsync(GenesisDb.Worksheet, $"{ClaimSql} FROM Claim WHERE BordxRefNo = @bordxNo ORDER BY CASNumber, ClaimVersion", new { bordxNo = S(bordx, "BordxRefNo") }, ct);
        var cases = claims.Select(r => S(r, "CASNumber")).OfType<string>().ToList();
        return Revise(bordx, claims, await InChunksAsync(GenesisDb.Worksheet, PayToSql, cases, ct), await InChunksAsync(GenesisDb.Main, PatientSql, cases, ct));
    }

    private static BordxRevise Revise(IDictionary<string, object?> bordx, List<IDictionary<string, object?>> claims, IEnumerable<IDictionary<string, object?>> payToRows,
        IEnumerable<IDictionary<string, object?>> patientRows)
    {
        var patientList = patientRows.ToList();
        var lines = Lines(claims, payToRows, patientList).ToDictionary(l => Key(l.CaseNumber, l.Version));
        var patients = patientList.GroupBy(r => S(r, "CASNumber")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = claims.Select(c =>
        {
            var no = S(c, "CASNumber")!; var version = I(c, "ClaimVersion") ?? 0;
            patients.TryGetValue(no, out var p);
            var line = lines.GetValueOrDefault(Key(no, version));
            var approved = Dec(c, "CLMTotalApproved") ?? 0;
            return new BordxReviseClaim(no, version, Day(c, "ClaimOpenDate"), p is null ? null : S(p, "MBMNumber"), line?.Patient, line?.Admitted, line?.Discharged, approved,
                p is null ? null : S(p, "CASStatus"), line?.BordxAmount ?? approved + (Dec(c, "CLMCaseMgmtFees") ?? 0));
        }).ToList();
        return new BordxRevise(S(bordx, "BordxRefNo")!, Day(bordx, "BordxDate"), Day(bordx, "BordxPrintedDate"), Dec(bordx, "BordxClaimAmt") ?? 0, I(bordx, "BordxNoClaims") ?? 0,
            BordxRules.Sent(S(bordx, "BordxPAYToStatus"), S(bordx, "BordxMNRBToStatus")), rows);
    }

    /// <summary>
    /// Legacy CmdReject_Click / RemoveFromBordx: each claim is archived (BordxWSArchive), its invoices and itself are taken off the bordereau, and the
    /// bordereau's count and amount are reduced. The amount taken off is the claim's bordereau amount as Insert Worksheet added it (legacy subtracted the
    /// total approved, leaving the case management fee on the bordereau).
    /// </summary>
    public async Task<BordxRevise> RemoveAsync(string bordxNo, RemoveFromBordxRequest request, string user, CancellationToken ct = default)
    {
        if (request.Claims is not { Count: > 0 }) throw new BusinessRuleException("Please select the worksheets to remove.", field: "claims");
        bordxNo = bordxNo.Trim();
        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var bordx = (await ws.QueryAsync("SELECT BordxRefNo, BordxDate, BordxPrintedDate, BordxClaimAmt, BordxNoClaims, BordxPAYToStatus, BordxMNRBToStatus FROM Bordx WITH (UPDLOCK) WHERE BordxRefNo = @bordxNo",
            new { bordxNo }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Bordereau {bordxNo} not found.");
        if (BordxRules.Sent(S(bordx, "BordxPAYToStatus"), S(bordx, "BordxMNRBToStatus"))) throw BusinessRuleException.Conflict("This Bordx has been sent out.");
        var all = await ws.QueryAsync($"{ClaimSql} FROM Claim WITH (UPDLOCK) WHERE BordxRefNo = @bordxNo", new { bordxNo }, ct);
        var keys = all.Select(r => S(r, "CASNumber")!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var current = Revise(bordx, all.ToList(), await ws.QueryAsync(PayToSql, new { keys }, ct), await main.QueryAsync(PatientSql, new { keys }, ct));
        var chosen = new List<BordxReviseClaim>();
        foreach (var k in request.Claims.DistinctBy(k => Key(k.CaseNumber, k.Version)))
            chosen.Add(current.Claims.FirstOrDefault(c => Key(c.CaseNumber, c.Version) == Key(k.CaseNumber, k.Version))
                ?? throw new BusinessRuleException($"Claim {k.CaseNumber}-{k.Version} is not on bordereau {bordxNo}.", field: "claims"));

        var today = DateTime.Today;
        foreach (var c in chosen)
        {
            await ws.ExecuteAsync("INSERT INTO BordxWSArchive (CASNumber, Claimversion, BordxRefNo, BordxDate, LastUpdateDate, LastUpdateUser) VALUES (@no, @version, @bordxNo, @date, @today, @who)",
                new { no = c.CaseNumber, version = c.Version.ToString(), bordxNo, date = D(bordx, "BordxDate"), today, who = user.Length > 10 ? user[..10] : user }, ct);
            await ws.ExecuteAsync("UPDATE ClaimInvoice SET INVBordxRefNo = '', INVBordxed = 0 WHERE INVBordxRefNo = @bordxNo AND CASNumber = @no AND INVWSversion = @version",
                new { bordxNo, no = c.CaseNumber, version = c.Version.ToString() }, ct);
            await ws.ExecuteAsync("""
                UPDATE Claim SET ClaimBordxStatus = 0, BordxRefNo = '',
                    CLMBordxWSStatus = CASE WHEN CLMBordxWSASCII = 'Y' THEN 'D' ELSE CLMBordxWSStatus END, CLMBordxWSASCII = CASE WHEN CLMBordxWSASCII = 'Y' THEN 'N' ELSE CLMBordxWSASCII END
                WHERE CASNumber = @no AND ClaimVersion = @version
                """, new { no = c.CaseNumber, version = c.Version }, ct);
        }
        await ws.ExecuteAsync("UPDATE Bordx SET BordxNoClaims = BordxNoClaims - @count, BordxClaimAmt = BordxClaimAmt - @amount WHERE BordxRefNo = @bordxNo",
            new { bordxNo, count = chosen.Count, amount = chosen.Sum(c => c.BordxAmount) }, ct);
        await ws.CommitAsync(ct);
        await main.CommitAsync(ct);
        audit.Record(new AuditEntry("Bordx.Remove", "Bordx", bordxNo, user, new Dictionary<string, object?> { ["claims"] = chosen.Select(c => $"{c.CaseNumber}-{c.Version}").ToList(), ["amount"] = chosen.Sum(c => c.BordxAmount) }));
        return (await ReviseAsync(bordxNo, ct))!;
    }

    /// <summary>Legacy GenerateCLMBordxNo: the payor's next CLAIM_BordxSEQ number (a new sequence starts at 00001) behind its health type and code.</summary>
    private static async Task<string> NextBordxNoAsync(IDbSession ws, IDbSession main, string payor, CancellationToken ct)
    {
        var hlt = (await main.ScalarAsync<string>("SELECT HLTCode FROM PAY WHERE PAYCode = @payor", new { payor }, ct))?.Trim()
            ?? throw new BusinessRuleException($"Payor {payor} not found.", field: "claims");
        var seq = await ws.ScalarAsync<string>("SELECT CLMBordxNo FROM CLAIM_BordxSEQ WITH (UPDLOCK, HOLDLOCK) WHERE PAYCode = @payor", new { payor }, ct);
        var next = seq is null ? 1 : int.Parse(seq.Trim()) + 1;
        if (seq is null) await ws.ExecuteAsync("INSERT INTO CLAIM_BordxSEQ (HLTCode, PAYCode, CLMBordxNo) VALUES (@hlt, @payor, @no)", new { hlt, payor, no = next.ToString("00000") }, ct);
        else await ws.ExecuteAsync("UPDATE CLAIM_BordxSEQ SET CLMBordxNo = @no WHERE PAYCode = @payor", new { payor, no = next.ToString("00000") }, ct);
        return BordxRules.BordxNo(hlt, payor, next);
    }
}
