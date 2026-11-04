using System.Globalization;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

internal sealed class LifetimeLimitRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ILifetimeLimitRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static (string Member, string Cover) Keys(string m, string c)
    {
        var member = T(m); var cover = T(c);
        if (member.Length == 0) throw new BusinessRuleException("Please select a Member Number ! ", field: "memberNumber");
        if (cover.Length == 0) throw new BusinessRuleException("Please select a Cover ID ! ", field: "coverId");
        if (member.Length > 15 || cover.Length != 2 || member.Any(char.IsControl)) throw new BusinessRuleException("Invalid member number or cover ID.", field: "memberNumber");
        return (member, cover);
    }

    private async Task<(string? Topup, List<IDictionary<string, object?>> Chain)> ChainAsync(string member, CancellationToken ct)
    {
        var topup = (await QueryAsync(GenesisDb.Maintenance, "SELECT MBMTopupNumber FROM MBMCrossReference WHERE MBMNumber = @member", new { member }, ct)).Select(r => T(S(r, "MBMTopupNumber"))).FirstOrDefault(x => x.Length > 0);
        if (topup is null) return (null, []);
        var chain = await QueryAsync(GenesisDb.Main, "SELECT MBMNumber, MBMCOVID, MBMName, MBMIcBcPp, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMStatus, MBMDataStatus, PLNCode, PAYCode, INSCode FROM MBM WHERE MBMTopupNumber = @topup ORDER BY MBMPayorEffDate", new { topup }, ct);
        return (topup, chain);
    }

    public async Task<LifetimeLimitView?> GetAsync(string memberNumber, string coverId, CancellationToken ct = default)
    {
        var (member, cover) = Keys(memberNumber, coverId);
        var (topup, chain) = await ChainAsync(member, ct);
        if (topup is null || chain.Count == 0) return null;
        var numbers = chain.Select(r => T(S(r, "MBMNumber"))).ToList();
        var cases = (await QueryAsync(GenesisDb.Main, """
            SELECT c.MBMNumber, c.CASNumber, c.MBMPatientCovID, c.CASDateAdmitted, c.CASDischargeDate, c.CASClaimability, m.MBMName, cp.MBMCName
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID
            WHERE c.MBMNumber IN @numbers AND c.MBMPatientCovID = @cover ORDER BY c.CASNumber
            """, new { numbers, cover }, ct)).Select(r => new LifetimeCase(T(S(r, "MBMNumber")), T(S(r, "CASNumber")), T(S(r, "MBMPatientCovID")), cover == "00" ? Blank(S(r, "MBMName")) : Blank(S(r, "MBMCName")), Day(r, "CASDateAdmitted"), Day(r, "CASDischargeDate"), Blank(S(r, "CASClaimability")))).ToList();
        var lt = (await QueryAsync(GenesisDb.Main, "SELECT MBMLifeTimeLmt FROM MBMLifeTime WHERE MBMNumber = @topup AND MBMCovID = @cover", new { topup, cover }, ct)).FirstOrDefault();
        var current = cases.Where(c => string.Equals(c.MemberNumber, member, StringComparison.OrdinalIgnoreCase)).ToList();
        var earlier = cases.Except(current).ToList();
        var allocated = lt is not null;
        return new LifetimeLimitView(member, cover, topup,
            chain.Select(r => new LifetimeMembership(T(S(r, "MBMNumber")), T(S(r, "MBMCOVID")), Blank(S(r, "MBMName")), Blank(S(r, "MBMIcBcPp")), Blank(S(r, "MBMPolicyNo")), Day(r, "MBMPayorEffDate"), Day(r, "MBMPayorExpDate"), Blank(S(r, "MBMStatus")), Blank(S(r, "MBMDataStatus")))).ToList(),
            current, earlier, allocated, lt is null ? null : Dec(lt, "MBMLifeTimeLmt"), earlier.Count > 0, chain.Count >= 3,
            allocated ? "Lifetime Limit has been allocated for this member" : "Lifetime Limit has not been allocated for this member");
    }

    public async Task<LifetimeLimitResult> AllocateAsync(string memberNumber, string coverId, string user, CancellationToken ct = default)
    {
        var (member, cover) = Keys(memberNumber, coverId);
        var view = await GetAsync(member, cover, ct) ?? throw BusinessRuleException.NotFound("No Record Found ! ");
        if (!view.Qualified) throw new BusinessRuleException("This Member is not qualified for Lifetime Limit! ");
        if (view.HasClaimsHistory) throw new BusinessRuleException("This Member has claims history! ");
        if (view.Allocated) throw new BusinessRuleException("Lifetime Limit has been allocated for this member! ");
        if (cover != "00" && await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @member AND MBMCCoverID = @cover", new { member, cover }, ct) == 0)
            throw BusinessRuleException.NotFound("Supplementary not found ! ");
        var (_, chain) = await ChainAsync(member, ct);
        var last = chain.Last();
        var plan = T(S(last, "PLNCode")); var payor = T(S(last, "PAYCode")); var ins = T(S(last, "INSCode"));
        var annual = (await QueryAsync(GenesisDb.Main, "SELECT TOP 1 AnnLimit FROM AnnualLimit WHERE PLNCode = @plan AND PAYCode = @payor AND INSCode = @ins ORDER BY AnnualEffDate DESC", new { plan, payor, ins }, ct)).Select(r => Dec(r, "AnnLimit")).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound("No annual limit is defined for the plan of this member.");
        var limit = annual * 3;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            await s.ExecuteAsync("UPDATE MBMOthers SET MBMGuaRenewal = '3' WHERE MBMNumber = @member", new { member }, ct);
            await s.ExecuteAsync("INSERT INTO MBMLifeTime (MBMNumber, MBMCovID, MBMLifeTimeLmt) VALUES (@topup, @cover, @limit)", new { topup = view.TopupNumber, cover, limit }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Member.LifetimeLimit", "MBMLifeTime", $"{view.TopupNumber}-{cover}", user, new Dictionary<string, object?> { ["member"] = member, ["annual"] = annual, ["limit"] = limit, ["plan"] = plan }));
        return new LifetimeLimitResult(limit, "Record is Updated! ");
    }
}

internal sealed class CaseAuditReviewRepository(IDbConnectionFactory connections, ICaseAdjustmentRepository cases, IAuditLog audit) : SqlRepository(connections), ICaseAuditReviewRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? ParseText(string? s) => DateOnly.TryParseExact(T(s), ["dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Please Enter Case No!", field: "caseNumber"); return c; }

    public async Task<string?> NeighbourAsync(string caseNumber, bool next, bool reviewedOnly, CancellationToken ct = default)
    {
        // Legacy CmdNext on an empty Case No box searched "> ''", i.e. loaded the first case.
        var no = next && string.IsNullOrWhiteSpace(caseNumber) ? "" : CaseNo(caseNumber);
        var table = reviewedOnly ? "CASAuditReview" : "CAS";
        var sql = next ? $"SELECT TOP 1 CASNumber FROM {table} WHERE CASNumber > @no ORDER BY CASNumber" : $"SELECT TOP 1 CASNumber FROM {table} WHERE CASNumber < @no ORDER BY CASNumber DESC";
        return (await QueryAsync(GenesisDb.Main, sql, new { no }, ct)).Select(r => T(S(r, "CASNumber"))).FirstOrDefault();
    }

    public async Task<CaseAuditView?> GetAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var c = await cases.GetAsync(no, ct);
        if (c is null) return null;
        var a = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CASAuditReview WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var record = a is null ? null : new CaseAuditRecord(T(S(a, "CASARStatus")), T(S(a, "CASDiscType")), Blank(S(a, "CASInvNo")), Day(a, "INVDate"), Dec(a, "CASInvAmt"), Day(a, "CASLetterSent"), Day(a, "CASHospReply"), ParseText(S(a, "CASDOV")), Blank(S(a, "CASDOVTime")),
            Blank(S(a, "Officer")), Blank(S(a, "CASVRemarks")), Dec(a, "CASDisRec"), Day(a, "CASDTC"), Day(a, "CASAROpenDate"), Blank(S(a, "CASARAnswer")), Blank(S(a, "CASARRemarks")), Blank(S(a, "CASReviewAudit")), Blank(S(a, "CASLastUpdateUser")), Day(a, "CASLastUpdateDate"));
        var pay = (await QueryAsync(GenesisDb.Payment, "SELECT PVHOSNo, PVHOSDate, HOSPaidAmt FROM Payment2HOS WHERE CASNumber = @no ORDER BY PVHOSDate", new { no }, ct)).Select(r => new CaseAuditPayment(Blank(S(r, "PVHOSNo")), Day(r, "PVHOSDate"), Dec(r, "HOSPaidAmt"))).ToList();
        return new CaseAuditView(no, c, record, pay);
    }

    public async Task<bool> SaveAsync(string caseNumber, CaseAuditRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var before = await GetAsync(no, ct) ?? throw BusinessRuleException.NotFound("No Record found !");
        if (CaseAuditRules.Validate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var member = before.Case.Form.MemberNumber!;
        var update = before.Audit is not null;
        var oldRef = (await QueryAsync(GenesisDb.Maintenance, "SELECT CASARStatus FROM CASCrossReference WHERE CASNumber = @no AND MBMNumber = @member", new { no, member }, ct)).FirstOrDefault();
        await using (var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
        {
            await m.ExecuteAsync("UPDATE CASCrossReference SET CASARStatus = @status WHERE CASNumber = @no AND MBMNumber = @member", new { no, member, status = r.Status }, ct);
            await m.CommitAsync(ct);
        }
        try
        {
            await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
            var args = new
            {
                no, inv = T(r.InvoiceNo), invDate = At(r.InvoiceDate), amt = r.InvoiceAmount ?? 0, sent = At(r.LetterSent), reply = At(r.HospitalReply), dov = r.VisitDate?.ToString("dd/MM/yyyy") ?? "", dovTime = T(r.VisitTime), officer = T(r.Officer), vrem = T(r.VisitRemarks),
                dis = r.DiscountReceived ?? 0, dtc = At(r.DateToClaims), open = At(r.OpenDate), answer = T(r.Answer), rem = T(r.Remarks), review = T(r.ReviewAudit), status = r.Status, user, now = DateTime.Today, disc = r.DiscountType
            };
            if (update)
                await s.ExecuteAsync("""
                    UPDATE CASAuditReview SET CASInvNo = @inv, INVDate = @invDate, CASInvAmt = @amt, CASLetterSent = @sent, CASHospReply = @reply, CASDOV = @dov, CASDOVTime = @dovTime, Officer = @officer, CASVRemarks = @vrem, CASDisRec = @dis, CASDTC = @dtc,
                        CASAROpenDate = @open, CASARAnswer = @answer, CASARRemarks = @rem, CASReviewAudit = @review, CASARStatus = @status, CASLastUpdateUser = @user, CASLastUpdateDate = @now, CASDiscType = @disc
                    WHERE CASNumber = @no
                    """, args, ct);
            else
                await s.ExecuteAsync("""
                    INSERT INTO CASAuditReview (CASNumber, CASInvNo, INVDate, CASInvAmt, CASLetterSent, CASHospReply, CASDOV, CASDOVTime, Officer, CASVRemarks, CASDisRec, CASDTC, CASAROpenDate, CASARAnswer, CASARRemarks, CASReviewAudit, CASARStatus, CASLastUpdateUser, CASLastUpdateDate, CASDiscType)
                    VALUES (@no, @inv, @invDate, @amt, @sent, @reply, @dov, @dovTime, @officer, @vrem, @dis, @dtc, @open, @answer, @rem, @review, @status, @user, @now, @disc)
                    """, args, ct);
            await s.CommitAsync(ct);
        }
        catch
        {
            if (oldRef is not null)
                await using (var undo = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
                {
                    await undo.ExecuteAsync("UPDATE CASCrossReference SET CASARStatus = @status WHERE CASNumber = @no AND MBMNumber = @member", new { no, member, status = S(oldRef, "CASARStatus") ?? "" }, ct);
                    await undo.CommitAsync(ct);
                }
            throw;
        }
        audit.Record(new AuditEntry(update ? "Case.AuditReviewUpdate" : "Case.AuditReviewAdd", "CASAuditReview", no, user, new Dictionary<string, object?> { ["before"] = before.Audit, ["after"] = r }));
        return update;
    }
}

internal sealed class CaseSendToBordxRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseSendToBordxRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private const int Cap = 3000;
    private static string Safe(string? s, int max, string field) { var v = T(s); if (v.Length > max || v.Any(char.IsControl)) throw new BusinessRuleException("Invalid search value.", field: field); return v; }

    public async Task<BordxOptions> OptionsAsync(CancellationToken ct = default) => new(
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY ORDER BY PAYCode", null, ct)).Select(r => new CaseOption(T(S(r, "PAYCode")), T(S(r, "PAYCompanyName")))).ToList(),
        (await QueryAsync(GenesisDb.Worksheet, "SELECT DISTINCT CASUserToken FROM ClaimOthersTwo WHERE CASUserToken IS NOT NULL AND CASUserToken <> '' ORDER BY CASUserToken", null, ct)).Select(r => T(S(r, "CASUserToken"))).ToList());

    public async Task<BordxSearchResult> SearchAsync(BordxSearchRequest r, CancellationToken ct = default)
    {
        var from = Safe(r.CaseNoFrom, 15, "caseNoFrom"); var to = Safe(r.CaseNoTo, 15, "caseNoTo");
        var caseSide = new List<string>(); var cp = new Dictionary<string, object?>();
        void CaseCond(string sql, string name, object? v) { caseSide.Add(sql); cp[name] = v; }
        if (r.CaseStatus == "0") caseSide.Add("c.CASStatus = 'O'"); else if (r.CaseStatus == "1") caseSide.Add("c.CASStatus = 'D'"); else if (r.CaseStatus == "2") caseSide.Add("c.CASStatus IN ('O','C')");
        if (r.RegFrom is { } a1) CaseCond("c.CaseRegDate >= @regFrom", "regFrom", At(a1)); if (r.RegTo is { } a2) CaseCond("c.CaseRegDate <= @regTo", "regTo", At(a2));
        if (r.AdmFrom is { } b1) CaseCond("c.CASDateAdmitted >= @admFrom", "admFrom", At(b1)); if (r.AdmTo is { } b2) CaseCond("c.CASDateAdmitted <= @admTo", "admTo", At(b2));
        if (r.DisFrom is { } d1) CaseCond("c.CASDischargeDate >= @disFrom", "disFrom", At(d1)); if (r.DisTo is { } d2) CaseCond("c.CASDischargeDate <= @disTo", "disTo", At(d2));
        if (r.NoAdmissionDate) caseSide.Add("c.CASDateAdmitted IS NULL"); if (r.NoRegDate) caseSide.Add("c.CaseRegDate IS NULL"); if (r.NoDischargeDate) caseSide.Add("c.CASDischargeDate IS NULL");
        if (!string.IsNullOrWhiteSpace(r.Payor)) CaseCond("m.PAYCode = @payor", "payor", Safe(r.Payor, 10, "payor"));

        var ws = new List<string>(); var wp = new Dictionary<string, object?>();
        void WsCond(string sql, string name, object? v) { ws.Add(sql); wp[name] = v; }
        if (r.ExactCase) { if (from.Length > 0) WsCond("c.CASNumber = @from", "from", from); }
        else { if (from.Length > 0) WsCond("c.CASNumber >= @from", "from", from); if (to.Length > 0) WsCond("c.CASNumber <= @to", "to", to); }
        if (!string.IsNullOrWhiteSpace(r.BordxFrom)) WsCond("c.BordxRefNo >= @bf", "bf", Safe(r.BordxFrom, 30, "bordxFrom")); if (!string.IsNullOrWhiteSpace(r.BordxTo)) WsCond("c.BordxRefNo <= @bt", "bt", Safe(r.BordxTo, 30, "bordxTo"));
        if (r.VersionFrom is { } v1) WsCond("c.ClaimVersion >= @v1", "v1", v1); if (r.VersionTo is { } v2) WsCond("c.ClaimVersion <= @v2", "v2", v2);
        if (!string.IsNullOrWhiteSpace(r.Claimability))
        {
            var cl = Safe(r.Claimability, 2, "claimability");
            if (cl == "1A") ws.Add("c.CLMWSStatus IN ('AA','SA','NA','PA','RA')"); else if (cl == "1R") ws.Add("c.CLMWSStatus IN ('RR','NR','PR','AR','BR','XR')"); else WsCond("c.CLMWSStatus = @cl", "cl", cl);
        }
        if (r.Department is "0" or "1") WsCond("o.CASClaimAdmin = @dept", "dept", r.Department);
        if (!string.IsNullOrWhiteSpace(r.PassedBy)) WsCond("o.CASUserToken = @by", "by", Safe(r.PassedBy, 50, "passedBy"));
        if (r.NoDpo) ws.Add("o.CASTokenDate IS NULL");
        if (r.DpoFrom is { } t1) WsCond("o.CASTokenDate >= @dpoFrom", "dpoFrom", At(t1)); if (r.DpoTo is { } t2) WsCond("o.CASTokenDate <= @dpoTo", "dpoTo", At(t2));
        if (r.BordxDateFrom is { } g1) WsCond("b.BordxDate >= @bdFrom", "bdFrom", At(g1)); if (r.BordxDateTo is { } g2) WsCond("b.BordxDate <= @bdTo", "bdTo", At(g2));
        var stay = r.StayType switch { "0" => "c.CLMStayType = 'I'", "1" => "c.CLMStayType = 'O'", "2" => "c.CLMStayType = 'F'", "3" => "c.CLMStayType = 'P'", "4" => "c.CLMStayType IN ('I','O')", "5" => "c.CLMStayType IN ('F','P')", _ => null };
        if (stay is not null) ws.Add(stay);
        if (r.CashType is "C" or "R") WsCond("c.CLMCashType = @cash", "cash", r.CashType);
        if (ws.Count == 0 && caseSide.Count == 0) throw new BusinessRuleException("Please enter at least one search criterion.");

        const string wsSelect = """
            SELECT c.CASNumber, c.ClaimVersion, c.CLMWSStatus, c.CLMBordxAmt, c.BordxRefNo, c.CLMCashType, c.CLMStayType, o.CASTokenDate, o.CASTokenBatch, o.CASUserToken, o.CASClaimAdmin, b.BordxDate
            FROM Claim c LEFT JOIN ClaimOthersTwo o ON o.CASNumber = c.CASNumber AND o.ClaimVersion = c.ClaimVersion LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo
            """;
        var truncated = false; List<IDictionary<string, object?>> claims = [];
        Dictionary<string, IDictionary<string, object?>> caseMap = [];
        async Task<List<IDictionary<string, object?>>> Cases(IReadOnlyList<string> nos) => nos.Count == 0 ? [] : (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASNumber, c.MBMNumber, c.MBMPatientCovID, c.CaseRegDate, c.CASDateAdmitted, c.CASDischargeDate, c.CaseOpenBy, c.CASHOSCode, m.MBMName, cp.MBMCName
            FROM CAS c LEFT JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID WHERE c.CASNumber IN @nos
            """, new { nos }, ct));
        if (caseSide.Count > 0)
        {
            var wsForCase = new List<string>(); // case number bounds also narrow the case-side search
            if (from.Length > 0) { wsForCase.Add(r.ExactCase ? "c.CASNumber = @cf" : "c.CASNumber >= @cf"); cp["cf"] = from; }
            if (!r.ExactCase && to.Length > 0) { wsForCase.Add("c.CASNumber <= @ct"); cp["ct"] = to; }
            var all = caseSide.Concat(wsForCase);
            var found = (await QueryAsync(GenesisDb.Main, $"SELECT TOP ({Cap + 1}) c.CASNumber FROM CAS c LEFT JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE {string.Join(" AND ", all)} ORDER BY c.CASNumber", cp, ct)).Select(x => T(S(x, "CASNumber"))).ToList();
            if (found.Count > Cap) { truncated = true; found = found[..Cap]; }
            foreach (var chunk in found.Chunk(1000))
            {
                var p = new Dictionary<string, object?>(wp) { ["nos"] = chunk };
                claims.AddRange(await QueryAsync(GenesisDb.Worksheet, wsSelect + " WHERE c.CASNumber IN @nos" + (ws.Count > 0 ? " AND " + string.Join(" AND ", ws) : ""), p, ct));
            }
        }
        else
        {
            claims = await QueryAsync(GenesisDb.Worksheet, $"SELECT TOP ({Cap + 1}) * FROM ({wsSelect} WHERE {string.Join(" AND ", ws)}) q ORDER BY CASNumber, ClaimVersion", wp, ct);
            if (claims.Count > Cap) { truncated = true; claims = claims.Take(Cap).ToList(); }
        }
        foreach (var chunk in claims.Select(x => T(S(x, "CASNumber"))).Distinct().Chunk(1000)) foreach (var row in await Cases(chunk)) caseMap[T(S(row, "CASNumber"))] = row;
        var rows = claims.Where(x => caseMap.ContainsKey(T(S(x, "CASNumber")))).OrderBy(x => T(S(x, "CASNumber")), StringComparer.Ordinal).ThenBy(x => I(x, "ClaimVersion")).Select(x =>
        {
            var no = T(S(x, "CASNumber")); var c = caseMap[no]; var cover = T(S(c, "MBMPatientCovID"));
            var reg = Day(c, "CaseRegDate"); var dpo = Day(x, "CASTokenDate");
            return new BordxRow(T(S(x, "CASClaimAdmin")) == "1", no, I(x, "ClaimVersion") ?? 1, T(S(c, "MBMNumber")), cover == "00" ? Blank(S(c, "MBMName")) : Blank(S(c, "MBMCName")), reg, Day(c, "CASDateAdmitted"), Day(c, "CASDischargeDate"), dpo,
                reg is { } rg && dpo is { } dp ? Math.Max(1, dp.DayNumber - rg.DayNumber) : null, Blank(S(x, "CASTokenBatch")), Blank(S(x, "CASUserToken")), Blank(S(c, "CaseOpenBy")), Blank(S(c, "CASHOSCode")), Blank(S(x, "CLMWSStatus")), Blank(S(x, "BordxRefNo")), Day(x, "BordxDate"), Dec(x, "CLMBordxAmt"),
                Blank(S(x, "CLMStayType")), Blank(S(x, "CLMCashType")));
        }).ToList();
        return new BordxSearchResult(rows, truncated);
    }

    public async Task<BordxPassResult> PassAsync(IReadOnlyList<BordxPassItem> items, string user, CancellationToken ct = default)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i.CaseNumber)).Select(i => (No: T(i.CaseNumber), i.Version)).Distinct().ToList();
        if (list.Count == 0) throw new BusinessRuleException("Please tick at least one record.");
        if (list.Count > 500) throw new BusinessRuleException("At most 500 records can be passed at once.");
        foreach (var (no, v) in list)
            if (no.Length > 15 || no.Any(c => !char.IsLetterOrDigit(c)) || v < 1) throw new BusinessRuleException("Invalid record in the selection.");
        foreach (var (no, v) in list)
            if (await ScalarAsync<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM Claim WHERE CASNumber = @no AND ClaimVersion = @v", new { no, v }, ct) == 0) throw BusinessRuleException.NotFound($"Claim {no}-{v} does not exist.");
        var today = DateTime.Today;
        await using var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        await using var w = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var seq = (await m.QueryAsync("SELECT TOP 1 PASSSeq FROM CASPASSSeq WITH (UPDLOCK, HOLDLOCK) WHERE DPO = @today AND PASSBy = @user", new { today, user }, ct)).Select(x => I(x, "PASSSeq")).Cast<int?>().FirstOrDefault();
        int batch;
        if (seq is null) { batch = 1; await m.ExecuteAsync("INSERT INTO CASPASSSeq (DPO, PASSSeq, PASSBy) VALUES (@today, 1, @user)", new { today, user }, ct); }
        else { batch = seq.Value + 1; await m.ExecuteAsync("UPDATE CASPASSSeq SET PASSSeq = @batch WHERE DPO = @today AND PASSBy = @user", new { batch, today, user }, ct); }
        foreach (var (no, v) in list)
        {
            var a = new { no, v, today, batch = batch.ToString(), user };
            if (await w.ExecuteAsync("UPDATE ClaimOthersTwo SET CASTokenDate = @today, CASTokenBatch = @batch, CASClaimAdmin = '1', CASUserToken = @user WHERE CASNumber = @no AND ClaimVersion = @v", a, ct) == 0)
                await w.ExecuteAsync("INSERT INTO ClaimOthersTwo (CASNumber, ClaimVersion, CASTokenDate, CASTokenBatch, CASClaimAdmin, CASUserToken) VALUES (@no, @v, @today, @batch, '1', @user)", a, ct);
        }
        await m.CommitAsync(ct); await w.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.SendToBordxUnit", "ClaimOthersTwo", batch.ToString(), user, new Dictionary<string, object?> { ["batch"] = batch, ["claims"] = list.Select(x => $"{x.No}-{x.Version}").ToList() }));
        return new BordxPassResult(batch.ToString(), list.Count, "Record updated...");
    }

    public async Task<bool> AmendAsync(string caseNumber, int version, DateOnly? dpo, string user, CancellationToken ct = default)
    {
        var no = T(caseNumber);
        if (no.Length is 0 or > 15 || no.Any(c => !char.IsLetterOrDigit(c))) throw new BusinessRuleException("Invalid case number.");
        if (dpo is { } d && (d < new DateOnly(1990, 1, 1) || d > DateOnly.FromDateTime(DateTime.Today).AddYears(1))) throw new BusinessRuleException("Invalid date.", field: "dpo");
        var row = (await QueryAsync(GenesisDb.Worksheet, "SELECT CASTokenDate, CASClaimAdmin FROM ClaimOthersTwo WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version }, ct)).FirstOrDefault();
        if (row is null || T(S(row, "CASClaimAdmin")) != "1") return false;
        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        if (dpo is null) await s.ExecuteAsync("UPDATE ClaimOthersTwo SET CASTokenDate = NULL, CASTokenBatch = '', CASClaimAdmin = '0', CASUserToken = '' WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version }, ct);
        else await s.ExecuteAsync("UPDATE ClaimOthersTwo SET CASTokenDate = @dpo WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version, dpo = At(dpo) }, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.AmendDpo", "ClaimOthersTwo", $"{no}-{version}", user, new Dictionary<string, object?> { ["before"] = D(row, "CASTokenDate"), ["after"] = dpo }));
        return true;
    }
}
