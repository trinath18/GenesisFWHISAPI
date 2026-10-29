using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

internal sealed class CaseAdjustmentTabsRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseAdjustmentTabsRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Please Enter Case No!", field: "caseNumber"); return c; }

    public async Task<IReadOnlyList<CaseLgItem>> LgListAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.MBMPatientCovID, c.CASDateAdmitted, h.HOSName, m.MBMName, cp.MBMCName
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID LEFT JOIN HOS h ON h.HOSCode = c.CASHOSCode
            WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (c is null) return [];
        var patient = T(S(c, "MBMPatientCovID"));
        var name = patient == "00" ? Blank(S(c, "MBMName")) : Blank(S(c, "MBMCName"));
        return (await QueryAsync(GenesisDb.Maintenance, "SELECT LGNo, LGDate, LGAmt, LGIssuedBy, LGStatus, LGRemark, LGUserTime, LGSystemTime FROM CASLG WHERE CASENo = @no ORDER BY LGNo", new { no }, ct))
            .Select(r => new CaseLgItem((T(S(r, "LGStatus")) == "B" ? "B" : "L") + no + "-" + T(S(r, "LGNo")), I(r, "LGNo") ?? 0, T(S(r, "LGStatus")), Day(r, "LGDate"), Dec(r, "LGAmt"), patient, name, Day(c, "CASDateAdmitted"), Blank(S(c, "HOSName")),
                Blank(S(r, "LGIssuedBy")), Blank(S(r, "LGRemark")), Blank(S(r, "LGUserTime")), Blank(S(r, "LGSystemTime")))).ToList();
    }

    public async Task<bool> UpdateLgRemarkAsync(string caseNumber, int lgNo, string? remark, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        if ((remark?.Length ?? 0) > 100 || (remark?.Any(char.IsControl) ?? false)) throw new BusinessRuleException("The remark can be at most 100 characters.", field: "remark");
        await using var s = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        var old = (await s.QueryAsync("SELECT LGRemark FROM CASLG WHERE CASENo = @no AND LGNo = @lgNo", new { no, lgNo }, ct)).FirstOrDefault();
        if (old is null) return false;
        await s.ExecuteAsync("UPDATE CASLG SET LGRemark = @remark WHERE CASENo = @no AND LGNo = @lgNo", new { no, lgNo, remark = Blank(remark) ?? "" }, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.UpdateLgRemark", "CASLG", $"{no}-{lgNo}", user, new Dictionary<string, object?> { ["before"] = S(old, "LGRemark"), ["after"] = Blank(remark) }));
        return true;
    }

    public async Task<IReadOnlyList<CaseWorksheetItem>> WorksheetsAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var rev = (await QueryAsync(GenesisDb.Worksheet, "SELECT ClaimVersion, CLMRevBordxStatus FROM Claim WHERE CASNumber = @no", new { no }, ct)).ToDictionary(r => I(r, "ClaimVersion") ?? 0, r => Blank(S(r, "CLMRevBordxStatus")));
        return (await QueryAsync(GenesisDb.Worksheet, """
            SELECT ClaimVersion, CLMTotalApproved, CLMTotalActual, CLMPayableByMedix2H, CLMPayableByMedix2M, BordxRefNo, BordxDate, CLMBordxAmt, CLMPayorAppStatus, CLMPayorAppDate, CLMPayorAppCode, CLMBordxRevInd, CRTCartonNo, DOSend, DebitCreditRefNo, DebitCreditDate
            FROM VIEW_WS_Listing WHERE CASNumber = @no ORDER BY ClaimVersion
            """, new { no }, ct)).Select(r =>
        {
            var v = I(r, "ClaimVersion") ?? 0;
            return new CaseWorksheetItem(v, Dec(r, "CLMTotalApproved"), Dec(r, "CLMTotalActual"), Dec(r, "CLMPayableByMedix2H"), Dec(r, "CLMPayableByMedix2M"), Blank(S(r, "BordxRefNo")), Day(r, "BordxDate"), Dec(r, "CLMBordxAmt"), Blank(S(r, "CLMPayorAppStatus")), Day(r, "CLMPayorAppDate"),
                Blank(S(r, "CLMPayorAppCode")), Blank(S(r, "CLMBordxRevInd")), Blank(S(r, "CRTCartonNo")), Day(r, "DOSend"), Blank(S(r, "DebitCreditRefNo")), Day(r, "DebitCreditDate"), rev.GetValueOrDefault(v));
        }).ToList();
    }

    public async Task<bool> UpdateWorksheetApprovalAsync(string caseNumber, int version, CaseWorksheetApprovalRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var row = (await QueryAsync(GenesisDb.Worksheet, "SELECT CLMBordxAmt, CLMPayorAppStatus, CLMPayorAppDate, CLMPayorAppCode, CLMBordxRevInd, CLMRevBordxStatus FROM Claim WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version }, ct)).FirstOrDefault();
        if (row is null) return false;
        if (CaseWorksheetRules.Validate(r, Dec(row, "CLMBordxAmt") ?? 0) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        await s.ExecuteAsync("UPDATE Claim SET CLMPayorAppStatus = 'Y', CLMPayorAppDate = @date, CLMPayorAppCode = @code, CLMBordxRevInd = @ind, CLMRevBordxStatus = @status WHERE CASNumber = @no AND ClaimVersion = @version",
            new { no, version, date = r.ApprovalDate!.Value.ToDateTime(TimeOnly.MinValue), code = T(r.ApprovalCode), ind = T(r.ReversalIndicator), status = T(r.ReversalStatus) }, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.UpdateWorksheetApproval", "Claim", $"{no}-{version}", user, new Dictionary<string, object?>
        {
            ["beforeStatus"] = S(row, "CLMPayorAppStatus"), ["beforeDate"] = D(row, "CLMPayorAppDate"), ["beforeCode"] = S(row, "CLMPayorAppCode"), ["after"] = r
        }));
        return true;
    }
}
