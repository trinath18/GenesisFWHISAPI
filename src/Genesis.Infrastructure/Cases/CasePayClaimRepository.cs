using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

internal sealed class CasePayClaimRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICasePayClaimRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Please key in the Case No.!", field: "caseNumber"); return c; }

    public async Task<CasePayClaimView?> GetAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASNumber, c.MBMNumber, c.MBMPatientCovID, c.CASDateAdmitted, c.CASDischargeDate, m.MBMName, m.MBMPolicyNo, cp.MBMCName
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (c is null) return null;
        var rec = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CASPayClaim WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        bool Y(string col) => rec is not null && T(S(rec, col)) == "Y";
        var record = rec is null ? null : new CasePayClaimRecord(Day(rec, "CASPayCLMDate"), Y("CASLab"), Y("CASConfInfo"), Y("CASInfoInv"), Y("CASCovgSub"), Y("CASNonPan"), Y("CASNonCash"), Y("CASCOthers"), Blank(S(rec, "CASOthers")), Blank(S(rec, "CASRegUser")), Day(rec, "CASRegDate"), Blank(S(rec, "LastUpdateUser")), Day(rec, "LastUpdateDate"));
        var principal = T(S(c, "MBMName"));
        return new CasePayClaimView(no, T(S(c, "MBMNumber")), principal, Blank(S(c, "MBMPolicyNo")), T(S(c, "MBMPatientCovID")) == "00" ? principal : T(S(c, "MBMCName")), Day(c, "CASDateAdmitted"), Day(c, "CASDischargeDate"), record);
    }

    public async Task<bool> SaveAsync(string caseNumber, CasePayClaimRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var before = await GetAsync(no, ct) ?? throw BusinessRuleException.NotFound("No Record found !");
        if (CasePayClaimRules.Validate(r, DateOnly.FromDateTime(DateTime.Today)) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        string F(bool b) => b ? "Y" : "N";
        var args = new { no, date = r.Date!.Value.ToDateTime(TimeOnly.MinValue), lab = F(r.Lab), conf = F(r.ConfInfo), inv = F(r.InfoInv), cov = F(r.CovgSub), nonPan = F(r.NonPan), nonCash = F(r.NonCash), others = F(r.OthersChecked), text = r.OthersChecked ? T(r.Others) : "", user, now = DateTime.Today };
        var update = before.Record is not null;
        await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        if (update)
            await s.ExecuteAsync("UPDATE CASPayClaim SET CASPayCLMDate = @date, CASLab = @lab, CASConfInfo = @conf, CASInfoInv = @inv, CASCovgSub = @cov, CASNonPan = @nonPan, CASNonCash = @nonCash, CASCOthers = @others, CASOthers = @text, LastUpdateUser = @user, LastUpdateDate = @now WHERE CASNumber = @no", args, ct);
        else
            await s.ExecuteAsync("INSERT INTO CASPayClaim (CASNumber, CASPayCLMDate, CASLab, CASConfInfo, CASInfoInv, CASCovgSub, CASNonPan, CASNonCash, CASCOthers, CASOthers, CASRegUser, LastUpdateUser, CASRegDate, LastUpdateDate) VALUES (@no, @date, @lab, @conf, @inv, @cov, @nonPan, @nonCash, @others, @text, @user, @user, @now, @now)", args, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry(update ? "Case.PayClaimUpdate" : "Case.PayClaimAdd", "CASPayClaim", no, user, new Dictionary<string, object?> { ["before"] = before.Record, ["after"] = r }));
        return update;
    }
}
