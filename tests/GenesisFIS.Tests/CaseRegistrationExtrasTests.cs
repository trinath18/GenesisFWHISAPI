using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class CaseLgRuleTests
{
    [Fact]
    public void Lg_needs_an_amount_and_a_valid_time()
    {
        Assert.Null(CaseLgRules.Validate(new(1000m, "09:30", "ok")));
        Assert.Equal("amount", CaseLgRules.Validate(new(null, "09:30", null))!.Value.Field);
        Assert.Equal("amount", CaseLgRules.Validate(new(0m, "09:30", null))!.Value.Field);
        Assert.Equal("time", CaseLgRules.Validate(new(10m, "", null))!.Value.Field);
        Assert.Equal("time", CaseLgRules.Validate(new(10m, "9:5", null))!.Value.Field);
        Assert.Equal("remark", CaseLgRules.Validate(new(10m, "09:30", new string('x', 101)))!.Value.Field);
    }

    [Fact]
    public void Letters_after_registration_follow_the_legacy_rules()
    {
        var today = new DateOnly(2024, 6, 15);
        Assert.Equal([CaseLetterKinds.PolicyEnding], CaseLetterKinds.AfterRegistration(today, today.AddDays(45), false));
        Assert.Empty(CaseLetterKinds.AfterRegistration(today, today.AddDays(46), false));
        Assert.Equal([CaseLetterKinds.Expired, CaseLetterKinds.Repudiation], CaseLetterKinds.AfterRegistration(today, today.AddDays(-1), true));
    }
}

[Collection("CaseRegistration")]
public class CaseRegistrationExtrasTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/case-registration";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(11));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static CaseRegistrationRequest Req() => new("WMZCASE01*01", "00", "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N",
        null, null, "fever", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["r"], [new("A00", "cholera", null)], null, false, false, false);

    private async Task Seed(int expiresInDays = 165, string topUp = "N")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd, MBMMemberType, MBMAddress1)
            VALUES ('WMZCASE01*01', '00', 'A', 'CASE PRINCIPAL', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLC1', DATEADD(day, -200, CAST(GETDATE() AS date)), DATEADD(day, @exp, CAST(GETDATE() AS date)), 20000, @topUp, 'P', '1 TEST ROAD')
            """, new { exp = expiresInDays, topUp });
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, GRPCompany) VALUES ('ZZP1', 'N', 'WM', '2020-01-01', 'ZZ')");
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, PAYCode) VALUES ('ZZP1', 'F', 'N', 10000, 4000, 'A', '2020-01-01', 'WM')");
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<CaseRegistrationResult> Register(CaseRegistrationRequest r)
    {
        var res = (await (await Client().PostAsJsonAsync(Url, r)).Content.ReadFromJsonAsync<CaseRegistrationResult>())!;
        await using var m = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await m.ExecuteAsync("DELETE FROM CASLG WHERE CASENo = @n; DELETE FROM LGSeq WHERE CASNumber = @n", new { n = res.CaseNumber });
        return res;
    }

    [DatabaseFact]
    public async Task Doctors_procedures_investigations_and_dates_are_stored_with_their_totals()
    {
        await Seed();
        var d = new DateOnly(2024, 5, 2);
        var r = await Register(Req() with
        {
            AttendingDoctors = ["Dr One", "Dr Two"], Anaesthetists = ["Dr Gas"], Procedures = [new("Appendicectomy", "A1234"), new("Biopsy", "B1")], NonSurgicalProcedures = [new("Physio", "P9")],
            Investigations = [new("X-ray", "XR1")], MedicalManagement = ["IV antibiotics"], FirstDocDate = d, SubsequentDocDate = d.AddDays(3)
        });
        var n = new { n = r.CaseNumber };
        Assert.Equal(("Appendicectomy", "A1234", "Biopsy"), (await Scalar<string>(GenesisDb.Main, "SELECT CASOPROCRequired1 FROM CASOthers WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT RTRIM(CASCptCode1) FROM CASOthers WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT CASOPROCRequired2 FROM CASOthers WHERE CASNumber = @n", n)));
        Assert.Equal(("Physio", "XR1", "IV antibiotics"), (await Scalar<string>(GenesisDb.Main, "SELECT CASONonSurgPROCRequired1 FROM CASOthers WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT RTRIM(CASInvCode1) FROM CASOthers WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT CASOMedSurMgmt1 FROM CASOthers WHERE CASNumber = @n", n)));
        Assert.Equal((2, 1, 2), (await Scalar<int>(GenesisDb.Main, "SELECT CASTotalDoctors FROM CASOthersTwo WHERE CASNumber = @n", n), await Scalar<int>(GenesisDb.Main, "SELECT CASTotalAnaes FROM CASOthersTwo WHERE CASNumber = @n", n), await Scalar<int>(GenesisDb.Main, "SELECT CASTotalProc FROM CASOthersTwo WHERE CASNumber = @n", n)));
        Assert.Equal("Dr Two", await Scalar<string>(GenesisDb.Main, "SELECT CASAttendDoctor2 FROM CASOthersTwo WHERE CASNumber = @n", n));
        Assert.Equal(d.AddDays(3).ToDateTime(TimeOnly.MinValue), await Scalar<DateTime>(GenesisDb.Main, "SELECT CASSubSeqDocDate FROM CASOthersTwo WHERE CASNumber = @n", n));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { AttendingDoctors = ["1", "2", "3", "4", "5", "6"] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { Procedures = [new("x", "TOOLONGCODE")] })).StatusCode);
    }

    [DatabaseFact]
    public async Task The_tabs_return_exclusions_the_note_the_surgical_schedule_and_the_membership_tab_data()
    {
        await Seed();
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("INSERT INTO MBMOthersTwo (MBMNumber, MBMExclusion) VALUES ('WMZCASE01*01', 'NO PRE-EXISTING')");
            await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber, MBMMmgtNotes) VALUES ('WMZCASE01*01', 'VIP member')");
            await main.ExecuteAsync("INSERT INTO PLNSurgOperation (PAYCode, PLNCode, SurgMainCat, SurgSubCat, SurgPerc) VALUES ('WM', 'ZZP1', 'MAJOR', 'A', '100')");
        }
        var e = (await Client().GetFromJsonAsync<CaseMemberExtras>($"{Url}/member/WMZCASE01*01/extras"))!;
        Assert.Equal(("NO PRE-EXISTING", "VIP member"), (e.PrincipalExclusion, e.ManagementNote));
        Assert.Equal("MAJOR", Assert.Single(e.SurgicalSchedule).MainCategory);
        foreach (var tab in new[] { "benefits", "history", "non-disclosure", "policy-terms", "policy-wording" })
            Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/member/WMZCASE01*01/{tab}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/member/NOSUCH/extras")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(13)).GetAsync($"{Url}/member/WMZCASE01*01/benefits")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_guarantee_letter_takes_the_next_number_checks_the_balance_and_writes_caslg()
    {
        await Seed();
        var c = await Register(Req());
        var res = await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(2500m, "09:30", "first"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var lg = (await res.Content.ReadFromJsonAsync<CaseLgResult>())!;
        Assert.Equal(($"L{c.CaseNumber}-1", 1, "L", 10000m), (lg.LgNumber, lg.Sequence, lg.Letter, lg.Balance));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASLG WHERE CASENo = @n AND LGNo = 1 AND LGAmt = 2500 AND LGStatus = 'L' AND LGUserTime = '09:30' AND LGIssuedBy = 'TEST'", new { n = c.CaseNumber }));
        var second = (await (await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(100m, "10:00", null))).Content.ReadFromJsonAsync<CaseLgResult>())!;
        Assert.Equal(2, second.Sequence);
        var over = await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(10001m, "10:00", null));
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode); Assert.Contains("Balance Limit", await over.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/NOCASE1/lg", new CaseLgRequest(1m, "10:00", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(null, "10:00", null))).StatusCode);
        Assert.Equal(2, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASLG WHERE CASENo = @n", new { n = c.CaseNumber }));
    }

    [DatabaseFact]
    public async Task Back_to_back_plans_get_a_B_letter_and_top_up_and_lifetime_cases_are_refused()
    {
        await Seed();
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE PLN SET PLNBTBG = 'Y' WHERE PLNCode = 'ZZP1' AND PAYCode = 'WM'");
        var c = await Register(Req());
        var lg = (await (await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(500m, "09:30", null))).Content.ReadFromJsonAsync<CaseLgResult>())!;
        Assert.Equal(("B", $"B{c.CaseNumber}-1"), (lg.Letter, lg.LgNumber));
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE PLN SET PLNLifeTimeStatus = 'Y' WHERE PLNCode = 'ZZP1' AND PAYCode = 'WM'");
        var life = await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(500m, "09:30", null));
        Assert.Equal(HttpStatusCode.BadRequest, life.StatusCode); Assert.Contains("lifetime", await life.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE PLN SET PLNLifeTimeStatus = 'N' WHERE PLNCode = 'ZZP1' AND PAYCode = 'WM'; UPDATE MBM SET MBMTopupInd = 'Y' WHERE MBMNumber = 'WMZCASE01*01'");
        var top = await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(500m, "09:30", null));
        Assert.Equal(HttpStatusCode.BadRequest, top.StatusCode); Assert.Contains("top-up", await top.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Document_request_and_take_home_drugs_letters_print_from_the_form_before_the_case_is_saved()
    {
        await Seed(expiresInDays: 200);
        // The header and General Info tab: the member's address and the plan's annual limit.
        var m = (await Client().GetFromJsonAsync<CaseMemberView>($"{Url}/member?memberNumber=WMZCASE01*01"))!;
        Assert.Equal(("1 TEST ROAD", 10000m), (m.Address1, m.AnnualLimit));

        var form = new CaseDraftLetterRequest("WMZCASE01*01", "00", "00001", null, new DateOnly(2024, 5, 1), null, null);
        foreach (var kind in CaseLetterKinds.Drafts)
        {
            var res = await Client().PostAsJsonAsync($"{Url}/letters/{kind}", form);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));
            Assert.Equal($"{kind}-WMZCASE01*01.pdf", res.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        }
        // The legacy checks: patient, hospital and admission date are needed; an unknown letter or member is refused.
        var missingDate = await Client().PostAsJsonAsync($"{Url}/letters/{CaseLetterKinds.DocRequestAdmission}", form with { Admitted = null });
        Assert.Equal(HttpStatusCode.BadRequest, missingDate.StatusCode);
        Assert.Contains("Admission Date", await missingDate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/letters/{CaseLetterKinds.TakeHomeDrugs}", form with { HospitalCode = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/letters/lg", form)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/letters/{CaseLetterKinds.TakeHomeDrugs}", form with { MemberNumber = "NOSUCH" })).StatusCode);
    }

    [DatabaseFact]
    public async Task Letters_are_pdfs_registration_suggests_the_right_ones_and_an_lg_letter_needs_an_lg()
    {
        await Seed(expiresInDays: 20);
        var c = await Register(Req() with { Claimability = "RR", RepudiationCodes = ["RC1"], Document = "D0001", Personnel = "P0001", Attention = "A0002", Underlying = true });
        Assert.Equal([CaseLetterKinds.PolicyEnding, CaseLetterKinds.Repudiation], c.Letters);
        foreach (var kind in new[] { CaseLetterKinds.Repudiation, CaseLetterKinds.PolicyEnding, CaseLetterKinds.Expired })
        {
            var res = await Client().GetAsync($"{Url}/{c.CaseNumber}/letters/{kind}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode); Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}/{c.CaseNumber}/letters/lg")).StatusCode);
        await Client().PostAsJsonAsync($"{Url}/{c.CaseNumber}/lg", new CaseLgRequest(500m, "09:30", "r"));
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/{c.CaseNumber}/letters/lg")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}/{c.CaseNumber}/letters/nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/NOCASE1/letters/expired")).StatusCode);
        var plain = await Register(Req() with { AcknowledgeOpenCase = true });
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}/{plain.CaseNumber}/letters/repudiation")).StatusCode);
    }
}


