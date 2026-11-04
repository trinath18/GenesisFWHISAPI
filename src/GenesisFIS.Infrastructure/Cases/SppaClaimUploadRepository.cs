using System.Globalization;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using GenesisFIS.Infrastructure.Membership;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Case > Claims ASCII (From SPPA) (frmCLMASCIIUpload2012): registers the cases, worksheets and invoices of an SPPA claims file through the procedures the
/// legacy form called (Case_RegFWHSCAS, Insert_WS_Claim_2010, ...). The legacy form looked everything up line by line; here the lookups are read once per file.
/// </summary>
internal sealed class SppaClaimUploadRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ISppaClaimUploadRepository
{
    private const int Chunk = 1000;

    private sealed record Member(string Number, DateTime? EffDate);

    /// <summary>What the database already holds for the file's members, admissions and hospitals.</summary>
    private sealed class Lookups
    {
        public Dictionary<(string, string, string), List<Member>> Members { get; } = [];
        public Dictionary<(DateTime, string, string, string), string> Registered { get; } = [];
        public HashSet<(string, string)> Bills { get; } = [];
        public Dictionary<string, string> KodPtj { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<SppaMemberClaim>> ClaimsByMember { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Analysis(string FileName, List<SppaUploadLine> Lines, List<SppaUploadRow> Rows, List<SppaUploadIssue> Issues, Lookups Lookups);

    private static string U(string? s) => (s ?? "").Trim().ToUpperInvariant();

    private static async Task<List<IDictionary<string, object?>>> InChunksAsync(IDbSession s, string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await s.QueryAsync(sql, new { keys = chunk }, ct));
        return all;
    }

    private static async Task<Analysis> AnalyseAsync(IDbSession main, IDbSession ws, SppaUploadRequest r, CancellationToken ct)
    {
        var name = r.FileName?.Trim() ?? "";
        var issues = SppaClaimUpload.CheckFile(r);
        var look = new Lookups();
        if (issues.Count > 0) return new Analysis(name, [], [], issues, look);
        var lines = SppaClaimUpload.Parse(r.Content!, issues);
        if (lines.Count == 0 && issues.Count == 0) issues.Add(new(0, "The file has no claim lines"));
        if (issues.Count > 0) return new Analysis(name, lines, [], issues, look);

        // MBM has no index on the worker keys: one hash join for the file (as Upload Bordx), not one scan per line.
        foreach (var m in await main.QueryAsync("""
            DECLARE @x xml = @keys;
            DECLARE @k TABLE (p nvarchar(5), i nvarchar(50), f nvarchar(50));
            INSERT @k SELECT DISTINCT n.value('@a0', 'nvarchar(5)'), n.value('@a1', 'nvarchar(50)'), n.value('@a2', 'nvarchar(50)') FROM @x.nodes('/r/k') t(n);
            SELECT m.MBMNumber, m.PAYCode, m.MBMInsurerUniqueNo, m.MBMFWUniqueNo, m.MBMPayorEffDate
            FROM MBM m INNER HASH JOIN @k k ON m.PAYCode = k.p AND m.MBMInsurerUniqueNo = k.i AND m.MBMFWUniqueNo = k.f
            ORDER BY m.MBMNumber
            """, new { keys = BordxUploadRepository.Keys(lines.Select(l => new[] { l.PayorCode, l.PolicyUniqueNo, l.WorkerUniqueNo })) }, ct))
        {
            var key = (U(S(m, "PAYCode")), U(S(m, "MBMInsurerUniqueNo")), U(S(m, "MBMFWUniqueNo")));
            if (!look.Members.TryGetValue(key, out var list)) look.Members[key] = list = [];
            list.Add(new Member(S(m, "MBMNumber")!.Trim(), D(m, "MBMPayorEffDate")));
        }

        // Admissions registered by earlier uploads (legacy duplicate check: admission date, insurer policy ID, worker ID, ESP reference).
        foreach (var c in await InChunksAsync(main, "SELECT CasNumber, CASDateAdmitted, CASPayorPolUnqNo, CASPayorFWUnqNo, CASESPRefNo FROM CAS WHERE CASESPRefNo IN @keys ORDER BY CasNumber",
                     lines.Select(l => l.EspRefNo).Where(e => e.Length > 0), ct))
            if (D(c, "CASDateAdmitted") is { } admitted)
                look.Registered.TryAdd((admitted.Date, U(S(c, "CASPayorPolUnqNo")), U(S(c, "CASPayorFWUnqNo")), U(S(c, "CASESPRefNo"))), S(c, "CasNumber")!.Trim());
        foreach (var b in await InChunksAsync(ws, "SELECT CasNumber, INVNo FROM ClaimInvoice WHERE CasNumber IN @keys", look.Registered.Values, ct))
            look.Bills.Add((U(S(b, "CasNumber")), U(S(b, "INVNo"))));

        foreach (var h in await InChunksAsync(main, "SELECT HOSCode, KodPTJ FROM HOS WHERE HOSCode IN @keys", lines.Select(l => l.HospitalCode), ct))
            look.KodPtj[S(h, "HOSCode")!.Trim()] = (S(h, "KodPTJ") ?? "").Trim();

        // The claim rows the legacy form read for the balance limit: every case of every member with the line's keys and policy effective date.
        var numbers = look.Members.Values.SelectMany(l => l).Select(m => m.Number).ToList();
        var cases = await InChunksAsync(main, "SELECT CasNumber, MBMNumber FROM CAS WHERE MBMNumber IN @keys", numbers, ct);
        var claims = (await InChunksAsync(ws, "SELECT CASNumber, ClaimVersion, CLMBordxAmt FROM Claim WHERE CASNumber IN @keys", cases.Select(c => S(c, "CasNumber")!.Trim()), ct))
            .GroupBy(c => U(S(c, "CASNumber"))).ToDictionary(g => g.Key, g => g.OrderBy(c => I(c, "ClaimVersion") ?? 0).ToList());
        foreach (var byMember in cases.GroupBy(c => U(S(c, "MBMNumber"))))
            look.ClaimsByMember[byMember.Key] = byMember.OrderBy(c => U(S(c, "CasNumber")))
                .SelectMany(c => claims.GetValueOrDefault(U(S(c, "CasNumber")), []))
                .Select(c => new SppaMemberClaim(S(c, "CASNumber")!.Trim(), I(c, "ClaimVersion") ?? 0, Dec(c, "CLMBordxAmt") ?? 0)).ToList();

        var rows = SppaClaimUpload.Plan(lines,
            l => look.Members.TryGetValue(SppaClaimUpload.MemberKey(l), out var m) ? m[^1].Number : null,
            l => look.Registered.GetValueOrDefault(SppaClaimUpload.AdmissionKey(l)),
            l => look.Registered.TryGetValue(SppaClaimUpload.AdmissionKey(l), out var c) && look.Bills.Contains((U(c), U(l.BillNo))));
        return new Analysis(name, lines, rows, issues, look);
    }

    /// <summary>Claim rows for the balance limit, in the legacy reading order (member, case, version), followed by 0 for each case this upload already registered
    /// for the same members (the legacy form saw those rows too, last).</summary>
    private static List<SppaMemberClaim> MemberClaims(Lookups look, SppaUploadLine l, IReadOnlyCollection<string> createdFor)
    {
        var members = look.Members.GetValueOrDefault(SppaClaimUpload.MemberKey(l), []).Where(m => m.EffDate?.Date == l.PolicyEffDate.Date).Select(m => m.Number).ToList();
        var list = members.SelectMany(n => look.ClaimsByMember.GetValueOrDefault(n, [])).ToList();
        list.AddRange(createdFor.Where(n => members.Contains(n, StringComparer.OrdinalIgnoreCase)).Select(n => new SppaMemberClaim("", 1, 0)));
        return list;
    }

    public async Task<SppaUploadPreview> PreviewAsync(SppaUploadRequest request, CancellationToken ct = default)
    {
        // Sessions only so the reads share a connection per database; disposed without commit, nothing is written.
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var a = await AnalyseAsync(main, ws, request, ct);
        return new SppaUploadPreview(a.FileName, a.Rows, a.Issues);
    }

    private static Task<int> ExecAsync(IDbSession s, string proc, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        var args = new DynamicParameters();
        foreach (var (name, value) in ps) args.Add(name, value);
        return s.ExecuteAsync($"EXEC dbo.{proc} " + string.Join(", ", ps.Select(p => $"@{p.Name} = @{p.Name}")), args, ct);
    }

    /// <summary>The legacy calls that only wrote zeros: the key argument(s), then <paramref name="zeros"/> times 0.</summary>
    private static Task<int> ZerosAsync(IDbSession s, string proc, string keys, int zeros, object args, CancellationToken ct) =>
        s.ExecuteAsync($"EXEC dbo.{proc} {keys}, " + string.Join(", ", Enumerable.Repeat("0", zeros)), args, ct);

    /// <summary>The legacy blank date: '' passed to a datetime parameter is stored as 1900-01-01.</summary>
    private static readonly DateTime Blank = new(1900, 1, 1);

    public async Task<SppaUploadResult> UploadAsync(SppaUploadRequest request, string user, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var a = await AnalyseAsync(main, ws, request, ct);
        if (a.Issues.FirstOrDefault() is { } issue) throw new BusinessRuleException(issue.Line > 0 ? $"Line {issue.Line}: {issue.Message}" : issue.Message);
        if (a.Rows.All(r => r.Action == SppaUploadAction.Skipped)) throw new BusinessRuleException("No line of the file can be uploaded.");

        var rows = new List<SppaUploadRow>();
        var caseOfAdmission = new Dictionary<(DateTime, string, string, string), string>();
        var created = new List<string>();
        foreach (var (row, l) in a.Rows.Zip(a.Lines))
        {
            if (row.Action == SppaUploadAction.Skipped) { rows.Add(row); continue; }
            var key = SppaClaimUpload.AdmissionKey(l);
            var claimId = SppaClaimUpload.ClaimId(a.Lookups.KodPtj.GetValueOrDefault(l.HospitalCode), l.PayorCode, l.BillNo);
            string caseNumber;
            if (row.Action == SppaUploadAction.NewCase)
            {
                // GenerateCaseNo: payor code + the payor's CAQ counter (5 digits).
                var current = await maint.ScalarAsync<string>("SELECT TOP 1 CAQNumber FROM CAQ WITH (UPDLOCK, HOLDLOCK) WHERE PAYCode = @p", new { p = l.PayorCode }, ct);
                var next = current is null ? "00001" : (int.Parse(current.Trim(), CultureInfo.InvariantCulture) + 1).ToString("00000", CultureInfo.InvariantCulture);
                if (current is null) await maint.ExecuteAsync("INSERT INTO CAQ (PAYCode, CAQNumber) VALUES (@p, @next)", new { p = l.PayorCode, next }, ct);
                else await maint.ExecuteAsync("UPDATE CAQ SET CAQNumber = @next WHERE PAYCode = @p", new { p = l.PayorCode, next }, ct);
                caseNumber = l.PayorCode + next;
                caseOfAdmission[key] = caseNumber;

                await ExecAsync(main, "Case_RegFWHSCAS", ct, ("CasNumber", caseNumber), ("MBMNumber", row.MemberNumber), ("MBMPatientCovID", "00"), ("CASStatus", "O"),
                    ("CASDateAdmitted", l.Admitted), ("CASDischargeDate", l.Discharged), ("CASClaimability", "AA"), ("CASStayType", "I"), ("CASPAYType", "C"),
                    ("ClaimNatureCode", l.NatureCode), ("CaseOpenBy", user), ("CaseRegDate", today), ("CASHOSCode", l.HospitalCode),
                    ("CASPayorPolUnqNo", l.PolicyUniqueNo), ("CASPayorFWUnqNo", l.WorkerUniqueNo), ("CASPayorPolNo", l.PolicyNo), ("CASMRNNo", l.MrnNo),
                    ("CASBillNo", l.BillNo), ("CASBillDate", l.BillDate), ("CASBillAmount", l.BilledAmount), ("CASBillRptDate", l.BillLodgedDate ?? Blank),
                    ("CASBillRptBy", l.BillLodgedBy), ("CASESPRefNo", l.EspRefNo), ("ClaimID", claimId));
                await main.ExecuteAsync("EXEC dbo.Case_RegFWHSDiagFirst @c", new { c = caseNumber }, ct);
                await main.ExecuteAsync("EXEC dbo.Case_RegFWHSDiagFinal @c", new { c = caseNumber }, ct);
            }
            else caseNumber = caseOfAdmission[key];

            // Insert_ClaimInvoice_WO; the new INVIndex is read back with OUTPUT instead of the legacy "top INVIndex" query.
            var index = await ws.ScalarAsync<int>("""
                INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, INVNo, CasNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVStayType, INVPAYType,
                                          InvPayTo, CLMINVRecMode, INVAmount, InvPaidByMem, CLMUHCNo, InvExcessAmt, InvPayAmt, INVInitialActAmt, CASESPRefNo)
                OUTPUT INSERTED.INVIndex
                VALUES ('1', 0, 1, @bill, @c, @billDate, @today, @user, @hos, 'I', 'H', 'H', 'HS', @amount, 0, '0', 0, 0, 0, @esp)
                """, new { bill = l.BillNo, c = caseNumber, billDate = l.BillDate, today, user, hos = l.HospitalCode, amount = l.BilledAmount, esp = l.EspRefNo }, ct);
            var inv = new { i = index };
            await ZerosAsync(ws, "Insert_ClaimInvActual2010", "@i", 46, inv, ct);
            await ZerosAsync(ws, "Insert_ClaimInvoiceActOthers_New", "@i", 40, inv, ct);
            await ZerosAsync(ws, "Insert_ClaimInvoiceNotCov08", "@i", 10, inv, ct);

            if (row.Action == SppaUploadAction.NewCase)
            {
                var balance = SppaClaimUpload.BalanceLimit(l.PolicyEffDate, MemberClaims(a.Lookups, l, created));
                await ExecAsync(ws, "Insert_WS_Claim_2010", ct, ("CasNumber", caseNumber), ("claimversion", 1), ("ClaimOpendate", today), ("ClaimOpenBy", user),
                    ("ClaimLastUpdateUser", user), ("ClaimLastUpdateDate", today), ("ClaimBordxStatus", "0"), ("CLMBordxWSStatus", "N"), ("CLMBordxWSASCII", "N"),
                    ("LimitCollectedBy", "N"), ("CLMTopMBMNumber", ""), ("CLMCashType", "C"), ("CLMLastDocDate", l.LastDocDate), ("CLMExGratia", "N"), ("CLMStayType", "I"),
                    ("CLMTotalActual", 0m), ("CLMTotalApproved", 0m), ("CLMPayableByMedix2H", 0m), ("CLMCompExcess", 0m), ("ExcessCollectedBy", "H"), ("CLMTotalCompApp", 0m),
                    ("CLMExcess", 0m), ("CLMReimburse", 0m), ("CLMBalanceLimit", 0m), ("ClaimCloseDate", today), ("CLMPreviousBalance", balance), ("CLMPreviousNewLimit", balance),
                    ("CLMLifetimeBal", 0m), ("CLMPrevTOLmt", 0m), ("BordxRefNo", ""), ("CLMPaidBYMedix2M", 0m), ("CLMPaidByMedix2H", 0m), ("CLMLimitExceeded", 0m),
                    ("CLMPayableByMedix2M", 0m), ("CLMWSStatus", "O"), ("CLMCoPayRB", 0m), ("CLMExGratiaAmt", 0m), ("CLMPrevCancerLmt", 0m), ("CLMPrevHomeNCLmt", 0m),
                    ("ClaimStatus", "O"), ("CLMPrevPerDisLmt", 0m), ("CLMBordxAmt", 0m), ("CLMBTBGAmt", 0m), ("CLMFinalApprove", 0m), ("CLMPayto", ""), ("CLMAccountNo", "0"),
                    ("CLMBank", "0"), ("MBMTopupInd", "0"), ("CLMMainTUAmt", 0m), ("CLMGPName", "0"), ("CaseMgmtFees", 0m), ("CLMPrevMajorMed", 0m), ("CLMMajorMedFee", 0m),
                    ("ClaimID", claimId));
                // Insert_WS_ClaimLimit: room and board RM 76 a day for the stay; services, surgical, ambulance and medical report "FULL"; the rest 0.
                await ws.ExecuteAsync("""
                    EXEC dbo.Insert_WS_ClaimLimit @c, 1, @rb, @days, '0', '0', 'FULL', '0', '0', '0', '0', 'FULL', '0', '0', '0', '0', '0', '0', '0', '0',
                        '0', 'FULL', '0', '0', '0', '0', '0', '0', '0', '0', 'FULL', '0', '0', '0', '0', '0', '0', '0', '0'
                    """, new
                {
                    c = caseNumber, rb = SppaClaimUpload.RoomBoardLimit(l).ToString(CultureInfo.InvariantCulture),
                    days = SppaClaimUpload.RoomBoardDays(l).ToString(CultureInfo.InvariantCulture)
                }, ct);
                var claim = new { c = caseNumber };
                foreach (var proc in new[] { "Insert_WS_ClaimActual", "Insert_WS_ClaimApprove", "Insert_WS_ClaimHOS", "Insert_WS_ClaimMBM" })
                    await ZerosAsync(ws, proc, "@c, 1", 31, claim, ct);
                created.Add(row.MemberNumber!);
            }
            rows.Add(row with { CaseNumber = caseNumber });
        }

        // Case numbers come from CAQ: commit those first so a failure afterwards only leaves a gap, never a reused number.
        await maint.CommitAsync(ct);
        await main.CommitAsync(ct);
        await ws.CommitAsync(ct);

        var result = new SppaUploadResult(a.FileName, rows);
        audit.Record(new AuditEntry("Claim.SppaUpload", "CAS", a.FileName, user, new Dictionary<string, object?>
        {
            ["newCases"] = result.NewCases, ["invoices"] = result.Invoices, ["skipped"] = result.Skipped,
            ["cases"] = rows.Where(r => r.Action == SppaUploadAction.NewCase).Select(r => r.CaseNumber).ToList(),
        }));
        return result;
    }
}
