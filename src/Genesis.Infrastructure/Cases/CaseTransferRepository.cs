using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

internal sealed class CaseTransferRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseTransferRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Please keyin Case Number !", field: "caseNumber"); return c; }
    private static string Member(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(char.IsControl)) throw new BusinessRuleException("Invalid member number.", field: "memberNumber"); return c; }

    public async Task<CaseTransferParty?> PartyAsync(string memberNumber, string coverId, CancellationToken ct = default)
    {
        var no = Member(memberNumber); var cover = T(coverId);
        if (cover.Length != 2) throw new BusinessRuleException("Invalid cover ID.", field: "coverId");
        var m = (await QueryAsync(GenesisDb.Main, """
            SELECT m.MBMNumber, m.MBMPolicyNo, m.MBMStatus, m.MBMIcBcPp, m.MBMName, m.MBMPayorEffDate, ISNULL(g.CoPolicyExpDate, m.MBMPayorExpDate) AS MBMPayorExpDate
            FROM MBM m LEFT JOIN GroupCompanyFW g ON g.CoPolicyNo = m.MBMPolicyNo AND g.CoGRPPaycode = m.PAYCode WHERE m.MBMNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (m is null) return null;
        string? name = Blank(S(m, "MBMName")), ic = Blank(S(m, "MBMIcBcPp"));
        if (cover != "00")
        {
            var c = (await QueryAsync(GenesisDb.Main, "SELECT MBMCName, MBMCICBCPPNo FROM MBMCoveredPersons WHERE MBMCNumber = @no AND MBMCCoverID = @cover", new { no, cover }, ct)).FirstOrDefault();
            if (c is null) return new CaseTransferParty(T(S(m, "MBMNumber")), cover, Blank(S(m, "MBMPolicyNo")), Blank(S(m, "MBMStatus")), null, null, Day(m, "MBMPayorEffDate"), Day(m, "MBMPayorExpDate"));
            (name, ic) = (Blank(S(c, "MBMCName")), Blank(S(c, "MBMCICBCPPNo")));
        }
        return new CaseTransferParty(T(S(m, "MBMNumber")), cover, Blank(S(m, "MBMPolicyNo")), Blank(S(m, "MBMStatus")), ic, name, Day(m, "MBMPayorEffDate"), Day(m, "MBMPayorExpDate"));
    }

    public async Task<CaseTransferParty?> CurrentAsync(string caseNumber, bool temporary, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var x = (await QueryAsync(GenesisDb.Maintenance, "SELECT MBMNumber, MBMPatientCovID FROM CASCrossReference WHERE CASStatus <> 'D' AND CASNumber = @no", new { no }, ct)).FirstOrDefault();
        if (x is null) return null;
        var member = T(S(x, "MBMNumber")); var cover = T(S(x, "MBMPatientCovID"));
        if (temporary)
        {
            if (!no.StartsWith('Z')) throw new BusinessRuleException("This is not a Temporary Case Number !");
            if (!member.StartsWith('Z')) throw new BusinessRuleException("This Case is not registered under a Temporary Member !");
            var claims = await QueryAsync(GenesisDb.Worksheet, "SELECT BordxRefNo FROM Claim WHERE CASNumber = @no", new { no }, ct);
            if (claims.Any(c => T(S(c, "BordxRefNo")).Length > 0)) throw new BusinessRuleException("This Case has been bordx !");
            if ((await QueryAsync(GenesisDb.Maintenance, "SELECT MBMNumber FROM CASCrossReference WHERE CASStatus <> 'D' AND MBMNumber = @member AND CASNumber <> @no", new { member, no }, ct)).Any())
                throw new BusinessRuleException("Other case is reigetered under this membership !");
        }
        return await PartyAsync(member, cover, ct);
    }

    /// <summary>Which limit bucket a patient's claims count against, as the legacy transfer decides it: the annual limit type of the plan (A: member, B: first covered person, C: the covered person) and the benefit limit type.</summary>
    private async Task AdjustLimitsAsync(IDbSession s, string member, string cover, decimal approved, decimal cancer, decimal kidney, decimal home, bool giveBack, bool onlyIfPositive, CancellationToken ct)
    {
        var m = (await s.QueryAsync("SELECT HLTCode, INSCode, PLNCode, MBMPayorEffDate, MBMAvailableLimit FROM MBM WITH (UPDLOCK) WHERE MBMNumber = @member", new { member }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{member}' does not exist.");
        var (hlt, ins, plan) = (T(S(m, "HLTCode")), T(S(m, "INSCode")), T(S(m, "PLNCode")));
        var eff = D(m, "MBMPayorEffDate") ?? DateTime.Today;
        var sign = giveBack ? 1m : -1m;
        if (approved != 0)
        {
            var ind = (await s.QueryAsync("SELECT TOP 1 SuppLimitStatus FROM AnnualLimit WHERE PLNCode = @plan AND INSCode = @ins AND AnnualEffDate <= @eff ORDER BY AnnualEffDate DESC", new { plan, ins, eff }, ct)).Select(r => T(S(r, "SuppLimitStatus"))).FirstOrDefault() is { Length: > 0 } v ? v : "A";
            if (ind == "A" || cover == "00")
            {
                var avail = Dec(m, "MBMAvailableLimit") ?? 0;
                if (!onlyIfPositive || avail > 0) await s.ExecuteAsync("UPDATE MBM SET MBMAvailableLimit = @limit WHERE MBMNumber = @member", new { member, limit = avail + sign * approved }, ct);
            }
            else
            {
                var id = ind == "B" ? "01" : cover;
                var c = (await s.QueryAsync("SELECT MBMCAvailableLimit, MBMCPLNCode FROM MBMCoveredPersons WITH (UPDLOCK) WHERE MBMCNumber = @member AND MBMCCoverID = @id", new { member, id }, ct)).FirstOrDefault();
                if (c is not null)
                {
                    var avail = Dec(c, "MBMCAvailableLimit") ?? 0;
                    if (!onlyIfPositive || avail > 0) await s.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMCAvailableLimit = @limit WHERE MBMCNumber = @member AND MBMCCoverID = @id", new { member, id, limit = avail + sign * approved }, ct);
                    if (giveBack && T(S(c, "MBMCPLNCode")).Length > 0) plan = T(S(c, "MBMCPLNCode"));
                }
            }
        }
        var bnf = (await s.QueryAsync("SELECT TOP 1 SuppALBStatus FROM BnfAnnLmt WHERE INSCode = @ins AND PLNCode = @plan AND HLTCode = @hlt AND ALBEffDate <= @eff ORDER BY ALBEffDate DESC", new { ins, plan, hlt, eff }, ct)).Select(r => T(S(r, "SuppALBStatus"))).FirstOrDefault();
        var bid = bnf == "B" ? "01" : cover;
        var b = (await s.QueryAsync("SELECT MBMCancerBalLmt, MBMKidneyBalLmt, MBMHomeNCBalLmt FROM MBMBnfLmt WITH (UPDLOCK) WHERE MBMNumber = @member AND MBMCoveredID = @bid", new { member, bid }, ct)).FirstOrDefault();
        if (b is not null)
            await s.ExecuteAsync("UPDATE MBMBnfLmt SET MBMCancerBalLmt = @c, MBMKidneyBalLmt = @k, MBMHomeNCBalLmt = @h WHERE MBMNumber = @member AND MBMCoveredID = @bid",
                new { member, bid, c = (Dec(b, "MBMCancerBalLmt") ?? 0) + sign * cancer, k = (Dec(b, "MBMKidneyBalLmt") ?? 0) + sign * kidney, h = (Dec(b, "MBMHomeNCBalLmt") ?? 0) + sign * home }, ct);
    }

    private async Task<(decimal Approved, decimal Cancer, decimal Kidney, decimal Home)> ClaimTotalsAsync(string no, CancellationToken ct)
    {
        var approved = (await QueryAsync(GenesisDb.Worksheet, "SELECT CLMTotalApproved FROM Claim WHERE CASNumber = @no", new { no }, ct)).Sum(r => Dec(r, "CLMTotalApproved") ?? 0);
        var a = await QueryAsync(GenesisDb.Worksheet, "SELECT CLMIndCancerTreatment, CLMIndKidneyDial, CLMHomeNCAmt FROM ClaimApprove WHERE CASNumber = @no", new { no }, ct);
        return (approved, a.Sum(r => Dec(r, "CLMIndCancerTreatment") ?? 0), a.Sum(r => Dec(r, "CLMIndKidneyDial") ?? 0), a.Sum(r => Dec(r, "CLMHomeNCAmt") ?? 0));
    }

    private static string Remark(string? old, string user, string text) => T(old) + "~" + text.Replace("{user}", user).Replace("{date}", DateTime.Today.ToString("yyyyMMdd"));

    public async Task<CaseTransferResult> TransferAsync(string caseNumber, CaseTransferRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var current = await CurrentAsync(no, false, ct) ?? throw BusinessRuleException.NotFound("No Record found !");
        var target = await PartyAsync(r.MemberNumber ?? "", r.CoverId ?? "", ct);
        if (CaseTransferRules.Validate(no, r, target, current, false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var t = target!; var (approved, cancer, kidney, home) = await ClaimTotalsAsync(no, ct);

        var oldRef = (await QueryAsync(GenesisDb.Maintenance, "SELECT MBMNumber, MBMPatientCovID, TransRemarks FROM CASCrossReference WHERE CASNumber = @no", new { no }, ct)).First();
        var remark = Remark(S(oldRef, "TransRemarks"), user, $"TransMBM by : {{user}} Date : {{date}},{current.MemberNumber}-{current.CoverId}->{t.MemberNumber}-{t.CoverId}");
        await using (var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
        {
            await m.ExecuteAsync("UPDATE CASCrossReference SET TransRemarks = @remark, MBMNumber = @member, MBMPatientCovID = @cover WHERE CASNumber = @no", new { no, remark, member = t.MemberNumber, cover = t.CoverId }, ct);
            await m.CommitAsync(ct);
        }
        try
        {
            await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
            await s.ExecuteAsync("UPDATE CAS SET MBMNumber = @member, MBMPatientCovID = @cover, CASLastUpdateUser = @user, CASLastUpdateDate = @now WHERE CASNumber = @no", new { no, member = t.MemberNumber, cover = t.CoverId, user, now = DateTime.Today }, ct);
            await AdjustLimitsAsync(s, current.MemberNumber, current.CoverId, approved, cancer, kidney, home, giveBack: true, onlyIfPositive: false, ct);
            await AdjustLimitsAsync(s, t.MemberNumber, t.CoverId, approved, cancer, kidney, home, giveBack: false, onlyIfPositive: false, ct);
            await s.CommitAsync(ct);
        }
        catch
        {
            await using var undo = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await undo.ExecuteAsync("UPDATE CASCrossReference SET TransRemarks = @remark, MBMNumber = @member, MBMPatientCovID = @cover WHERE CASNumber = @no", new { no, remark = Nz(S(oldRef, "TransRemarks")), member = T(S(oldRef, "MBMNumber")), cover = T(S(oldRef, "MBMPatientCovID")) }, ct);
            await undo.CommitAsync(ct);
            throw;
        }
        audit.Record(new AuditEntry("Case.Transfer", "CAS", no, user, new Dictionary<string, object?> { ["from"] = current, ["to"] = t, ["approved"] = approved, ["cancer"] = cancer, ["kidney"] = kidney, ["homeNursing"] = home }));
        return new CaseTransferResult(no, no, "Transfered Sucessfully....", false);
    }

    private static string Nz(string? s) => s ?? "";

    private static readonly string[] MainCaseTables = ["CASOthers", "CASOthersTwo", "CAS_UD", "CASAuditReview", "CASRemarks", "CASRemarksTwo", "CASRemarkThree", "CASRepudiation", "CASProcInv", "CASPayClaim", "DIAFinal", "DIAFirst"];
    private static readonly string[] WorksheetCaseTables = ["Claim", "ClaimActual", "ClaimApprove", "ClaimHos", "ClaimInvoice", "ClaimLimit", "ClaimMember", "ClaimPayMBMStatus", "ClaimPosting", "ClaimSameLimit", "CLMReminder"];

    public async Task<CaseTransferResult> TempTransferAsync(string caseNumber, CaseTransferRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var current = await CurrentAsync(no, true, ct) ?? throw BusinessRuleException.NotFound("No Record found !");
        var target = await PartyAsync(r.MemberNumber ?? "", r.CoverId ?? "", ct);
        if (CaseTransferRules.Validate(no, r, target, current, true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var t = target!; var (approved, cancer, kidney, home) = await ClaimTotalsAsync(no, ct);
        var payor = t.MemberNumber[..2];
        var removeTemp = (await QueryAsync(GenesisDb.Maintenance, "SELECT CASNumber FROM CASCrossReference WHERE CASStatus <> 'D' AND MBMNumber = @member", new { member = current.MemberNumber }, ct)).Count == 1;

        await using var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        await using var w = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);

        var seq = (await m.QueryAsync("SELECT CAQNumber FROM CAQ WITH (UPDLOCK, HOLDLOCK) WHERE PAYCode = @payor", new { payor }, ct)).Select(x => T(S(x, "CAQNumber"))).FirstOrDefault();
        string next;
        if (seq is null) { next = "00001"; await m.ExecuteAsync("INSERT INTO CAQ (PAYCode, CAQNumber) VALUES (@payor, @next)", new { payor, next }, ct); }
        else { next = (int.Parse(seq) + 1).ToString("00000"); await m.ExecuteAsync("UPDATE CAQ SET CAQNumber = @next WHERE PAYCode = @payor", new { payor, next }, ct); }
        var newNo = payor + next;

        var xr = (await m.QueryAsync("SELECT TransRemarks FROM CASCrossReference WHERE CASNumber = @no", new { no }, ct)).First();
        var remark = Remark(S(xr, "TransRemarks"), user, $"TransCAS by : {{user}} Date : {{date}},{no}->{newNo} , {current.MemberNumber}-{current.CoverId}->{t.MemberNumber}-{t.CoverId}");
        await m.ExecuteAsync("UPDATE CASCrossReference SET TransRemarks = @remark, CASNumber = @newNo, MBMNumber = @member, MBMPatientCovID = @cover WHERE CASNumber = @no", new { no, newNo, remark, member = t.MemberNumber, cover = t.CoverId }, ct);
        await m.ExecuteAsync("UPDATE CASLG SET CASENo = @newNo WHERE CASENo = @no", new { no, newNo }, ct);
        await m.ExecuteAsync("UPDATE LGSeq SET CASNumber = @newNo WHERE CASNumber = @no", new { no, newNo }, ct);

        await s.ExecuteAsync("UPDATE CAS SET CASNumber = @newNo, MBMNumber = @member, MBMPatientCovID = @cover, CASLastUpdateUser = @user, CASLastUpdateDate = @now WHERE CASNumber = @no", new { no, newNo, member = t.MemberNumber, cover = t.CoverId, user, now = DateTime.Today }, ct);
        await s.ExecuteAsync("UPDATE CASAmendHis SET CASNumber = @newNo, MBMNumber = @member, MBMCoveredID = @cover WHERE CASNumber = @no", new { no, newNo, member = t.MemberNumber, cover = t.CoverId }, ct);
        foreach (var table in MainCaseTables) await s.ExecuteAsync($"UPDATE {table} SET CASNumber = @newNo WHERE CASNumber = @no", new { no, newNo }, ct);
        foreach (var table in WorksheetCaseTables) await w.ExecuteAsync($"UPDATE {table} SET CASNumber = @newNo WHERE CASNumber = @no", new { no, newNo }, ct);
        await w.ExecuteAsync("UPDATE CAS_Excess SET CASNumber = @newNo, EXCNumber = LEFT(EXCNumber, 1) + @newNo + RIGHT(EXCNumber, 2) WHERE CASNumber = @no", new { no, newNo }, ct);

        if (removeTemp)
        {
            var tm = current.MemberNumber;
            await m.ExecuteAsync("DELETE FROM MBMCrossReference WHERE MBMNumber = @tm", new { tm }, ct);
            foreach (var table in new[] { "MBMLifeTime", "MBM", "MBMTwo", "MBMOthers", "MBMOthersTwo", "MBMCLNOthers" }) await s.ExecuteAsync($"DELETE FROM {table} WHERE MBMNumber = @tm", new { tm }, ct);
            foreach (var table in new[] { "MBMCoveredPersons", "MBMCoveredPersonsTwo", "MBMCoveredPersonsCLN" }) await s.ExecuteAsync($"DELETE FROM {table} WHERE MBMCNumber = @tm", new { tm }, ct);
        }
        if (approved != 0 || cancer != 0 || kidney != 0 || home != 0) await AdjustLimitsAsync(s, t.MemberNumber, t.CoverId, approved, cancer, kidney, home, giveBack: false, onlyIfPositive: true, ct);

        await s.CommitAsync(ct); await m.CommitAsync(ct); await w.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.TempTransfer", "CAS", newNo, user, new Dictionary<string, object?> { ["oldCase"] = no, ["from"] = current, ["to"] = t, ["approved"] = approved, ["temporaryMemberRemoved"] = removeTemp }));
        return new CaseTransferResult(no, newNo, $"Transfered Sucessfully.... New case number {newNo}", removeTemp);
    }
}
