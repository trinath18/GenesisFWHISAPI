using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Cases;
using Genesis.Core.Data;

namespace Genesis.Tests;

public class CaseTransferRuleTests
{
    private static readonly CaseTransferParty Cur = new("WM1", "00", "P1", "A", "IC", "A NAME", null, null);
    private static CaseTransferParty Tgt(string member = "WM2", string status = "A", string? policy = "P2", string? name = "B NAME") => new(member, "00", policy, status, "IC2", name, null, null);

    [Fact]
    public void Transfer_rules_follow_the_legacy_messages()
    {
        Assert.Null(CaseTransferRules.Validate("WM00001", new("WM2", "00"), Tgt(), Cur, false));
        Assert.Equal("memberNumber", CaseTransferRules.Validate("WM00001", new("", "00"), null, Cur, false)!.Value.Field);
        Assert.Equal("coverId", CaseTransferRules.Validate("WM00001", new("WM2", ""), null, Cur, false)!.Value.Field);
        Assert.Contains("Different Payor", CaseTransferRules.Validate("WM00001", new("JE2", "00"), Tgt("JE2"), Cur, false)!.Value.Message);
        Assert.Contains("not found", CaseTransferRules.Validate("WM00001", new("WM2", "00"), null, Cur, false)!.Value.Message);
        Assert.Contains("DELETE", CaseTransferRules.Validate("WM00001", new("WM2", "00"), Tgt(status: "D"), Cur, false)!.Value.Message);
        Assert.Contains("Same Membership", CaseTransferRules.Validate("WM00001", new("WM1", "00"), Tgt("WM1"), Cur, false)!.Value.Message);
        Assert.Contains("Temporary", CaseTransferRules.Validate("Z0001", new("Z2", "00"), Tgt("Z2"), Cur, true)!.Value.Message);
        Assert.Null(CaseTransferRules.Validate("Z0001", new("JE2", "00"), Tgt("JE2"), Cur, true));
    }

    [Fact]
    public void Pay_and_claim_needs_a_date_and_remarks_when_others_is_ticked()
    {
        var today = new DateOnly(2024, 6, 15);
        Assert.Null(CasePayClaimRules.Validate(new(today, true, false, false, false, false, false, false, null), today));
        Assert.Equal("date", CasePayClaimRules.Validate(new(null, true, false, false, false, false, false, false, null), today)!.Value.Field);
        Assert.Equal("others", CasePayClaimRules.Validate(new(today, false, false, false, false, false, false, true, " "), today)!.Value.Field);
        Assert.Equal("others", CasePayClaimRules.Validate(new(today, false, false, false, false, false, false, true, new string('x', 51)), today)!.Value.Field);
    }
}

[Collection("CaseRegistration")]
public class CaseTransfersTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Reg = "/api/case-registration";
    private HttpClient Cli() => factory.ClientFor(WriteApiFactory.Access(11));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Member(string number, string payor, decimal limit, string status = "A")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd)
            VALUES (@number, '00', @status, @name, 'IC1', @payor, 'N', 'F', 'ZZP1', 'POLT1', DATEADD(day, -200, CAST(GETDATE() AS date)), DATEADD(day, 165, CAST(GETDATE() AS date)), @limit, 'N')
            """, new { number, payor, limit, status, name = "NAME OF " + number });
    }

    private async Task<string> Case(string member, bool open = false)
    {
        var req = new CaseRegistrationRequest(member, "00", "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N", null, null, "x", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["r"], [new("A00", "c", null)], null, false, false, open);
        var res = await Cli().PostAsJsonAsync(Reg, req);
        var no = (await res.Content.ReadFromJsonAsync<CaseRegistrationResult>())!.CaseNumber;
        await using var m = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await m.ExecuteAsync("DELETE FROM CASLG WHERE CASENo = @no; DELETE FROM LGSeq WHERE CASNumber = @no", new { no });
        await using var w = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await w.ExecuteAsync("DELETE FROM Claim WHERE CASNumber = @no", new { no });
        return no;
    }

    private async Task Claim(string no, decimal approved, string? bordx = null)
    {
        await using var w = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await w.ExecuteAsync("INSERT INTO Claim (CASNumber, ClaimVersion, CLMTotalApproved, BordxRefNo) VALUES (@no, 1, @approved, @bordx)", new { no, approved, bordx });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task The_three_screens_need_position_11()
    {
        var other = factory.ClientFor(WriteApiFactory.Access(13));
        foreach (var u in new[] { "/api/case-transfer/case/WM00001", "/api/case-temp-transfer/case/Z0001", "/api/case-payclaim/WM00001" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(u)).StatusCode);
    }

    [DatabaseFact]
    public async Task Case_transfer_moves_the_case_and_moves_the_approved_amount_between_the_limits()
    {
        await Member("WMZTRA01*01", "WM", 5000); await Member("WMZTRB01*01", "WM", 8000);
        var no = await Case("WMZTRA01*01"); await Claim(no, 800m);
        var cur = (await Cli().GetFromJsonAsync<CaseTransferParty>($"/api/case-transfer/case/{no}"))!;
        Assert.Equal(("WMZTRA01*01", "00"), (cur.MemberNumber, cur.CoverId));
        Assert.Equal("NAME OF WMZTRB01*01", (await Cli().GetFromJsonAsync<CaseTransferParty>("/api/case-transfer/member?memberNumber=WMZTRB01*01&coverId=00"))!.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli().GetAsync("/api/case-transfer/member?memberNumber=NOSUCH&coverId=00")).StatusCode);
        var res = await Cli().PostAsJsonAsync($"/api/case-transfer/{no}", new CaseTransferRequest("WMZTRB01*01", "00"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @no AND MBMNumber = 'WMZTRB01*01' AND MBMPatientCovID = '00' AND CASLastUpdateUser = 'TEST'", new { no }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASCrossReference WHERE CASNumber = @no AND MBMNumber = 'WMZTRB01*01' AND TransRemarks LIKE '%TransMBM by : TEST%WMZTRA01*01-00->WMZTRB01*01-00%'", new { no }));
        Assert.Equal(5800m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = 'WMZTRA01*01'"));
        Assert.Equal(7200m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = 'WMZTRB01*01'"));
        Assert.Equal("Case.Transfer", factory.Audit.Entries.Last().Action);
    }

    [DatabaseFact]
    public async Task Case_transfer_refuses_other_payors_deleted_members_the_same_member_and_unknown_cases()
    {
        await Member("WMZTRA01*01", "WM", 5000); await Member("WMZTRB01*01", "WM", 8000, "D"); await Member("JEZTRC01*01", "JE", 100);
        var no = await Case("WMZTRA01*01");
        foreach (var (m, c) in new[] { ("JEZTRC01*01", "00"), ("WMZTRB01*01", "00"), ("WMZTRA01*01", "00"), ("NOSUCH", "00"), ("WMZTRA01*01", "05") })
            Assert.True((await Cli().PostAsJsonAsync($"/api/case-transfer/{no}", new CaseTransferRequest(m, c))).StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli().PostAsJsonAsync("/api/case-transfer/NOCASE1", new CaseTransferRequest("WMZTRA01*01", "00"))).StatusCode);
        Assert.Equal(5000m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = 'WMZTRA01*01'"));
        Assert.Equal("WMZTRA01*01", await Scalar<string>(GenesisDb.Main, "SELECT MBMNumber FROM CAS WHERE CASNumber = @no", new { no }));
    }

    [DatabaseFact]
    public async Task Temp_transfer_gives_the_case_a_new_number_everywhere_and_removes_the_temporary_member()
    {
        await Member("ZQZTMP01*01", "ZQ", 100); await Member("QZTRB01*01", "QZ", 8000);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber, MBMMmgtNotes) VALUES ('ZQZTMP01*01', 'temp note')");
        var no = await Case("ZQZTMP01*01"); await Claim(no, 800m);
        Assert.StartsWith("Z", no);
        Assert.Equal("ZQZTMP01*01", (await Cli().GetFromJsonAsync<CaseTransferParty>($"/api/case-temp-transfer/case/{no}"))!.MemberNumber);
        var res = await Cli().PostAsJsonAsync($"/api/case-temp-transfer/{no}", new CaseTransferRequest("QZTRB01*01", "00"));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var r = (await res.Content.ReadFromJsonAsync<CaseTransferResult>())!;
        Assert.StartsWith("QZ", r.NewCaseNumber); Assert.True(r.TemporaryMemberRemoved);
        var n = new { no, nn = r.NewCaseNumber };
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @no", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @nn AND MBMNumber = 'QZTRB01*01'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASOthers WHERE CASNumber = @nn", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM DIAFirst WHERE CASNumber = @nn", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASCrossReference WHERE CASNumber = @nn AND TransRemarks LIKE '%TransCAS by : TEST%'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM Claim WHERE CASNumber = @nn", n));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM Claim WHERE CASNumber = @no", n));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = 'ZQZTMP01*01'"));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = 'ZQZTMP01*01'"));
        Assert.Equal(7200m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = 'QZTRB01*01'"));
        Assert.Equal("Case.TempTransfer", factory.Audit.Entries.Last().Action);
    }

    [DatabaseFact]
    public async Task Temp_transfer_refuses_real_cases_bordxd_cases_shared_members_and_temporary_targets()
    {
        await Member("ZQZTMP01*01", "ZQ", 100); await Member("QZTRB01*01", "QZ", 8000); await Member("ZQZTMP02*01", "ZQ", 100);
        var real = await Case("QZTRB01*01");
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().GetAsync($"/api/case-temp-transfer/case/{real}")).StatusCode);
        var bordx = await Case("ZQZTMP02*01"); await Claim(bordx, 10m, "BORDX1");
        var res = await Cli().GetAsync($"/api/case-temp-transfer/case/{bordx}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); Assert.Contains("bordx", await res.Content.ReadAsStringAsync());
        var a = await Case("ZQZTMP01*01"); await Case("ZQZTMP01*01", open: true);
        var shared = await Cli().GetAsync($"/api/case-temp-transfer/case/{a}");
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode); Assert.Contains("Other case", await shared.Content.ReadAsStringAsync());
        var solo = await Case("ZQZTMP01*01".Replace("01*01", "02*01"), open: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PostAsJsonAsync($"/api/case-temp-transfer/{solo}", new CaseTransferRequest("ZQZTMP01*01", "00"))).StatusCode);
    }

    [DatabaseFact]
    public async Task Pay_and_claim_adds_then_updates_and_prints_a_letter()
    {
        await Member("WMZTRA01*01", "WM", 5000);
        var no = await Case("WMZTRA01*01");
        var v = (await Cli().GetFromJsonAsync<CasePayClaimView>($"/api/case-payclaim/{no}"))!;
        Assert.Null(v.Record); Assert.Equal("NAME OF WMZTRA01*01", v.PatientName);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().GetAsync($"/api/case-payclaim/{no}/letter")).StatusCode);
        var date = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PutAsJsonAsync($"/api/case-payclaim/{no}", new CasePayClaimRequest(null, true, false, false, false, false, false, false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PutAsJsonAsync($"/api/case-payclaim/{no}", new CasePayClaimRequest(date, false, false, false, false, false, false, true, ""))).StatusCode);
        var add = await Cli().PutAsJsonAsync($"/api/case-payclaim/{no}", new CasePayClaimRequest(date, true, false, true, false, true, false, true, "other reason"));
        Assert.False((await add.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("updated").GetBoolean());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASPayClaim WHERE CASNumber = @no AND CASLab = 'Y' AND CASConfInfo = 'N' AND CASInfoInv = 'Y' AND CASNonPan = 'Y' AND CASCOthers = 'Y' AND CASOthers = 'other reason' AND CASRegUser = 'TEST' AND LastUpdateUser = 'TEST'", new { no }));
        var upd = await Cli().PutAsJsonAsync($"/api/case-payclaim/{no}", new CasePayClaimRequest(date, false, true, false, false, false, true, false, "ignored"));
        Assert.True((await upd.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("updated").GetBoolean());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASPayClaim WHERE CASNumber = @no AND CASLab = 'N' AND CASConfInfo = 'Y' AND CASNonCash = 'Y' AND CASCOthers = 'N' AND CASOthers = ''", new { no }));
        var pdf = await Cli().GetAsync($"/api/case-payclaim/{no}/letter");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli().GetAsync("/api/case-payclaim/NOCASE1")).StatusCode);
    }
}




