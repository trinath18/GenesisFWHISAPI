using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// The two legacy "send to Claims" screens share one search shape. Both list through a legacy procedure that takes a SQL fragment
/// (Case_Send2ClaimRep / CRU_Send2Claim with @StrAppendCase); the fragment is built by <see cref="LegacyFilter"/> from whitelisted values only.
/// </summary>
internal abstract class SendToClaimsBase(IDbConnectionFactory connections) : SqlRepository(connections)
{
    protected static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    protected static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;

    protected static string CaseNo(string? s, string message = "Please Enter Case No")
    {
        var c = s?.Trim() ?? "";
        if (c.Length is 0 or > 15 || !c.All(char.IsLetterOrDigit)) throw new BusinessRuleException(message, field: "caseNumber");
        return c;
    }

    /// <summary>The procedure and the column names that differ between the two screens.</summary>
    protected abstract (string Proc, string DeptColumn, string PassedByColumn, string DpoColumn, bool Versions) Shape { get; }

    protected LegacyFilter Filter(SendToClaimsSearch r, out int criteria)
    {
        var f = new LegacyFilter();
        var s = Shape;
        if (r.ExactCase) { if (!string.IsNullOrWhiteSpace(r.CaseNoFrom)) f.Text("CASNumber", "=", CaseNo(r.CaseNoFrom), "caseNoFrom"); }
        else { f.Text("CASNumber", ">=", r.CaseNoFrom, "caseNoFrom").Text("CASNumber", "<=", r.CaseNoTo, "caseNoTo"); }
        var before = f.Count;
        // Case status: the legacy combo defaulted to "2 - Open & Close".
        f.Constant((r.CaseStatus ?? "2") switch { "0" => "AND CASStatus = 'O'", "1" => "AND CASStatus = 'D'", "2" => "AND (CASStatus = 'O' OR CASStatus = 'C')", _ => throw new BusinessRuleException("Invalid case status.", field: "caseStatus") });
        var statusOnly = f.Count - before;
        if (r.Claimability is "A" or "R") f.Constant($"AND CASClaimability = '{r.Claimability}'");
        else if (!string.IsNullOrEmpty(r.Claimability)) throw new BusinessRuleException("Invalid claimability.", field: "claimability");
        if (r.Department is "0" or "1") f.Constant($"AND {s.DeptColumn} = '{r.Department}'");
        f.Text(s.PassedByColumn, "=", r.PassedBy, "passedBy");
        if (s.Versions) f.Number("ClaimVersion", ">=", r.VersionFrom).Number("ClaimVersion", "<=", r.VersionTo);
        f.Empty("CasDateAdmitted", r.NoAdmissionDate).Empty("CaseRegDate", r.NoRegDate).Empty("CasDischargeDate", r.NoDischargeDate).Empty(s.DpoColumn, r.NoDpo);
        f.Date("CaseRegDate", ">=", r.RegFrom).Date("CaseRegDate", "<=", r.RegTo).Date("CasDateAdmitted", ">=", r.AdmFrom).Date("CasDateAdmitted", "<=", r.AdmTo)
         .Date("CasDischargeDate", ">=", r.DisFrom).Date("CasDischargeDate", "<=", r.DisTo).Date(s.DpoColumn, ">=", r.DpoFrom).Date(s.DpoColumn, "<=", r.DpoTo);
        criteria = f.Count - statusOnly;
        return f;
    }

    protected Task<List<IDictionary<string, object?>>> ListAsync(string fragment, string listCase, CancellationToken ct) =>
        QueryAsync(GenesisDb.Main, $"EXEC {Shape.Proc} @StrAppend, @StrAppendCase", new { StrAppend = fragment, StrAppendCase = listCase }, ct, timeoutSeconds: 300);

    protected static int? Days(IDictionary<string, object?> r) => S(r, "NoofDay") is { } n && int.TryParse(n, out var d) ? Math.Max(1, d) : null;   // legacy showed 0 as 1
}

internal sealed class CaseSendToClaimsRepRepository(IDbConnectionFactory connections, IAuditLog audit) : SendToClaimsBase(connections), ICaseSendToClaimsRepRepository
{
    protected override (string Proc, string DeptColumn, string PassedByColumn, string DpoColumn, bool Versions) Shape => ("Case_Send2ClaimRep", "CASClaimAdmin", "CASUserToken", "CASTokenDate", false);

    public async Task<IReadOnlyList<string>> PassedByAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "EXEC SearchCASToken", null, ct)).Select(r => S(r, "CASUserToken")).OfType<string>().Distinct().Order().ToList();

    private static SendToClaimsRow Row(IDictionary<string, object?> r)
    {
        var passed = S(r, "ClaimAdmin") == "1";
        var (reg, adm, dis, hos) = (Day(r, "CaseRegDate"), Day(r, "CASDateAdmitted"), Day(r, "CASDischargeDate"), S(r, "CASHOSCode"));
        return new SendToClaimsRow(passed, S(r, "CasNumber") ?? "", null, S(r, "MBMNumber"), S(r, "PatientName"), reg, adm, dis, Day(r, "TokenDate"), Days(r), S(r, "TokenBatch"), S(r, "UserToken"), S(r, "CaseOpenBy"), hos,
            S(r, "CASStayType"), S(r, "CASPAYType"), Dec(r, "ActualAmt"), S(r, "CASClaimability"), S(r, "ClaimAdmin"), SendToClaimsRules.RepCannotPass(reg, adm, dis, hos, passed));
    }

    public async Task<SendToClaimsResult> SearchAsync(SendToClaimsSearch request, CancellationToken ct = default)
    {
        var f = Filter(request with { VersionFrom = null, VersionTo = null }, out var criteria);
        if (criteria == 0) throw new BusinessRuleException("Please enter at least one search criterion.");
        var rows = (await ListAsync(f.ToString(), "3", ct)).Select(Row).ToList();
        return new SendToClaimsResult(rows.Take(SendToClaimsRules.MaxRows).ToList(), rows.Count > SendToClaimsRules.MaxRows);
    }

    public async Task<SendToClaimsPassResult> PassAsync(IReadOnlyList<string> caseNumbers, string user, CancellationToken ct = default)
    {
        var list = caseNumbers.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => CaseNo(c, "Invalid record in the selection.")).Distinct().ToList();
        if (list.Count == 0) throw new BusinessRuleException("Please tick at least one record.");
        if (list.Count > SendToClaimsRules.MaxPass) throw new BusinessRuleException($"At most {SendToClaimsRules.MaxPass} records can be passed at once.");
        // The server re-checks the legacy tick rules instead of trusting the screen.
        foreach (var no in list)
        {
            var row = (await ListAsync(new LegacyFilter().Text("CASNumber", "=", no, "caseNumber").ToString(), "3", ct)).Select(Row).FirstOrDefault()
                      ?? throw BusinessRuleException.NotFound($"Case {no} was not found.");
            if (row.CannotPass is { } why) throw new BusinessRuleException($"{no}: {why}");
        }
        var today = DateTime.Today; var tokenDate = today.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
        await using var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var batch = await CasePassBatch.NextAsync(m, today, user, ct);
        foreach (var no in list)
        {
            // Legacy UpdateRecord: case 4 of Case_Send2ClaimRep says whether the case already has a send record, then insert or update it.
            var exists = (await s.QueryAsync("EXEC Case_Send2ClaimRep @no, '4'", new { no }, ct)).Count > 0;
            await s.ExecuteAsync(exists ? "EXEC Case_SendRep2UpdateRec @no, @tokenDate, @batch, '1', @user" : "EXEC Case_SendRep2InsertRec @no, @tokenDate, @batch, '1', @user",
                new { no, tokenDate, batch = batch.ToString(), user }, ct);
        }
        await m.CommitAsync(ct); await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.SendToClaimsRep", "Case_SendRep2Rec", batch.ToString(), user, new Dictionary<string, object?> { ["batch"] = batch, ["cases"] = list }));
        return new SendToClaimsPassResult(list.Count, batch.ToString(), "Record updated...");
    }
}

internal sealed class CaseCruSendToClaimsRepository(IDbConnectionFactory connections, IAuditLog audit) : SendToClaimsBase(connections), ICaseCruSendToClaimsRepository
{
    protected override (string Proc, string DeptColumn, string PassedByColumn, string DpoColumn, bool Versions) Shape => ("CRU_Send2Claim", "CASDPOCStatus", "CASLastDPOCUser", "CASDPOC", true);

    public async Task<IReadOnlyList<string>> PassedByAsync(CancellationToken ct = default) =>
        (await ListAsync("", "1", ct)).Select(r => S(r, "CASLastDPOCUser")).OfType<string>().Distinct().Order().ToList();

    public async Task<SendToClaimsResult> SearchAsync(SendToClaimsSearch request, CancellationToken ct = default)
    {
        var f = Filter(request, out var criteria);
        if (criteria == 0) throw new BusinessRuleException("Please enter at least one search criterion.");
        var rows = (await ListAsync(f.ToString(), "2", ct)).Select(r => new SendToClaimsRow(
            S(r, "CASDPOCStatus") == "1", S(r, "CASNumber") ?? "", I(r, "ClaimVersion"), S(r, "MBMNumber"), S(r, "PatientName"), Day(r, "CaseRegDate"), Day(r, "CASDateAdmitted"), Day(r, "CASDischargeDate"),
            Day(r, "CASDPOC"), Days(r), null, S(r, "CASLastDPOCUser"), S(r, "CaseOpenBy"), S(r, "CASHOSCode"), S(r, "CASStayType"), S(r, "CASPAYType"), Dec(r, "ActualAmt"), S(r, "CASClaimability"), S(r, "CASDPOCStatus"), null)).ToList();
        return new SendToClaimsResult(rows.Take(SendToClaimsRules.MaxRows).ToList(), rows.Count > SendToClaimsRules.MaxRows);
    }

    public async Task<SendToClaimsPassResult> PassAsync(IReadOnlyList<SendToClaimsItem> items, DateOnly passOutDate, string user, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (passOutDate.Year < 1990 || passOutDate > today.AddYears(1)) throw new BusinessRuleException("Please Enter in date", field: "passOutDate");
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i.CaseNumber)).Select(i => (No: CaseNo(i.CaseNumber, "Invalid record in the selection."), Version: i.Version ?? 0)).Distinct().ToList();
        if (list.Count == 0) throw new BusinessRuleException("Please tick at least one record.");
        if (list.Count > SendToClaimsRules.MaxPass) throw new BusinessRuleException($"At most {SendToClaimsRules.MaxPass} records can be passed at once.");
        if (list.Any(i => i.Version < 1)) throw new BusinessRuleException("Invalid record in the selection.");
        foreach (var (no, v) in list)
            if (await ScalarAsync<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM Claim WHERE CASNumber = @no AND ClaimVersion = @v", new { no, v }, ct) == 0)
                throw BusinessRuleException.NotFound($"Claim {no}-{v} does not exist.");
        var fmt = System.Globalization.CultureInfo.InvariantCulture;
        var tokenDate = passOutDate.ToString("yyyy/MM/dd", fmt); var lastDate = today.ToString("yyyy/MM/dd", fmt);
        await using var w = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        foreach (var (no, v) in list)
        {
            // Legacy UpdateRecord: case 3 of CRU_Send2Claim (key "case,version") says whether a pass-out record exists, then insert or update it.
            var exists = (await ListAsync($"{no},{v}", "3", ct)).Count > 0;
            await w.ExecuteAsync(exists ? "EXEC CRU_PassOutUpdate @no, @v, @tokenDate, @user, @lastDate, 1" : "EXEC CRU_PassOutInsert @no, @v, @tokenDate, @user, @lastDate, 1",
                new { no, v = v.ToString(fmt), tokenDate, user, lastDate }, ct);
        }
        await w.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.CruSendToClaims", "CRU_PassOut", tokenDate, user, new Dictionary<string, object?> { ["passOutDate"] = passOutDate, ["claims"] = list.Select(x => $"{x.No}-{x.Version}").ToList() }));
        return new SendToClaimsPassResult(list.Count, null, "Record updated...");
    }
}

/// <summary>The legacy GenerateCasePass: one batch counter per DPO date and user in CASPASSSeq (maintenance database), shared with Send to Bordx Unit.</summary>
internal static class CasePassBatch
{
    public static async Task<int> NextAsync(IDbSession m, DateTime today, string user, CancellationToken ct)
    {
        var seq = (await m.QueryAsync("SELECT TOP 1 PASSSeq FROM CASPASSSeq WITH (UPDLOCK, HOLDLOCK) WHERE DPO = @today AND PASSBy = @user", new { today, user }, ct)).Select(x => I(x, "PASSSeq")).FirstOrDefault();
        if (seq is null) { await m.ExecuteAsync("INSERT INTO CASPASSSeq (DPO, PASSSeq, PASSBy) VALUES (@today, 1, @user)", new { today, user }, ct); return 1; }
        var batch = seq.Value + 1;
        await m.ExecuteAsync("UPDATE CASPASSSeq SET PASSSeq = @batch WHERE DPO = @today AND PASSBy = @user", new { batch, today, user }, ct);
        return batch;
    }
}
