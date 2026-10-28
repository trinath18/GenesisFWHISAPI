using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Cases;
using Genesis.Core.Data;

namespace Genesis.Tests;

public class CaseRegistrationRuleTests
{
    private static readonly DateOnly Today = new(2024, 6, 15);
    private static CaseRegistrationRequest Ok() => new("WMZCASE01*01", "00", "AA", "00001", null, new(2024, 6, 10), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N", null, null, "fever", null, 2024, null,
        "N", 0m, null, null, "35", "Y", null, null, "N", null, null, null, ["note"], [new("A00", "cholera", new(2024, 6, 10))], null, false, false, false);

    [Fact]
    public void A_complete_request_is_valid() => Assert.Null(CaseRegistrationRules.Validate(Ok(), Today));

    [Fact]
    public void Each_missing_field_uses_the_legacy_message()
    {
        Assert.Equal("memberNumber", CaseRegistrationRules.Validate(Ok() with { MemberNumber = "" }, Today)!.Value.Field);
        Assert.Equal("patientId", CaseRegistrationRules.Validate(Ok() with { PatientId = "" }, Today)!.Value.Field);
        Assert.Equal("claimability", CaseRegistrationRules.Validate(Ok() with { Claimability = "" }, Today)!.Value.Field);
        Assert.Equal("hospitalCode", CaseRegistrationRules.Validate(Ok() with { HospitalCode = null }, Today)!.Value.Field);
        Assert.Equal("dateAdmitted", CaseRegistrationRules.Validate(Ok() with { DateAdmitted = null }, Today)!.Value.Field);
        Assert.Equal("reserveAmount", CaseRegistrationRules.Validate(Ok() with { ReserveAmount = null }, Today)!.Value.Field);
        Assert.Equal("lgAmount", CaseRegistrationRules.Validate(Ok() with { LgAmount = null }, Today)!.Value.Field);
        Assert.Equal("firstDiagnosis", CaseRegistrationRules.Validate(Ok() with { FirstDiagnosis = [] }, Today)!.Value.Field);
        Assert.Equal("topUpLimit", CaseRegistrationRules.Validate(Ok() with { TopUpLimit = null }, Today)!.Value.Field);
        Assert.Equal("takeOverPlan", CaseRegistrationRules.Validate(Ok() with { IntExtTakeOver = "I" }, Today)!.Value.Field);
        Assert.Equal("policyYear", CaseRegistrationRules.Validate(Ok() with { PolicyYear = null }, Today)!.Value.Field);
        Assert.Equal("patientAge", CaseRegistrationRules.Validate(Ok() with { PatientAge = "" }, Today)!.Value.Field);
        Assert.Equal("ageUnit", CaseRegistrationRules.Validate(Ok() with { AgeUnit = null }, Today)!.Value.Field);
        Assert.Equal("espRefNo", CaseRegistrationRules.Validate(Ok() with { PayType = "R", EspRefNo = null }, Today)!.Value.Field);
        Assert.Equal("dateDischarge", CaseRegistrationRules.Validate(Ok() with { DateDischarge = new(2024, 6, 1) }, Today)!.Value.Field);
        Assert.Equal("lgTime", CaseRegistrationRules.Validate(Ok() with { LgTime = "25:00" }, Today)!.Value.Field);
    }

    [Fact]
    public void Repudiating_claimability_is_refused_in_this_slice() =>
        Assert.Contains("repudiation", CaseRegistrationRules.Validate(Ok() with { Claimability = "RR" }, Today)!.Value.Message);
}

public class CaseRegistrationTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/case-registration";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(11));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static CaseRegistrationRequest Req(string member = "WMZCASE01*01", string patient = "00") => new(member, patient, "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N",
        null, "Dr Referrer", "fever and pain", null, 2024, null, "N", 0m, null, null, "35", "Y", "01/06/2024", "02/06/2024", "N", "N", "N", "N", ["remark one\nline two", "remark two"],
        [new("A00", "cholera", DateOnly.FromDateTime(DateTime.Today).AddDays(-3))], [new("A00", "final cholera", null)], false, false, false);

    private async Task Seed(string status = "A", int effDaysAgo = 200, decimal limit = 20000, string topUp = "N")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd)
            VALUES ('WMZCASE01*01', '00', @status, 'CASE PRINCIPAL', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLC1', DATEADD(day, -@eff, CAST(GETDATE() AS date)), DATEADD(day, 165, CAST(GETDATE() AS date)), @limit, @topUp)
            """, new { status, eff = effDaysAgo, limit, topUp });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCRELCode) VALUES ('WMZCASE01*01', '01', 'CASE SPOUSE', 'IC2', 'S')");
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Needs_position_11()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(13));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Options_hold_the_legacy_lists_without_the_repudiating_statuses()
    {
        var o = (await Client().GetFromJsonAsync<CaseRegistrationOptions>($"{Url}/options"))!;
        Assert.Contains(o.Claimability, c => c.Code == "AA"); Assert.DoesNotContain(o.Claimability, c => c.Code == "RR");
        Assert.Equal(["C", "R"], o.PayTypes.Select(p => p.Code)); Assert.DoesNotContain(o.StayTypes, p => p.Code is "C" or "R");
        Assert.NotEmpty(o.Icd); Assert.NotEmpty(o.Modalities); Assert.NotEmpty(o.Natures); Assert.NotEmpty(o.SpecialApprovals);
        Assert.NotEmpty(await Client().GetFromJsonAsync<List<CaseOption>>($"{Url}/hospitals?search=DIALYSIS") ?? []);
    }

    [DatabaseFact]
    public async Task Member_lookup_lists_the_patients_and_applies_the_legacy_blocks_and_warnings()
    {
        await Seed();
        var m = (await Client().GetFromJsonAsync<CaseMemberView>($"{Url}/member?memberNumber=WMZCASE01*01"))!;
        Assert.Equal(["00", "01"], m.Patients.Select(p => p.CoverId)); Assert.Null(m.BlockedReason); Assert.Empty(m.Warnings);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/member?memberNumber=NOSUCH")).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = 'WMZCASE01*01'");
            Assert.Equal("Policy has been cancelled!", (await Client().GetFromJsonAsync<CaseMemberView>($"{Url}/member?memberNumber=WMZCASE01*01"))!.BlockedReason);
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'A', MBMPayorEffDate = DATEADD(day, 5, GETDATE()) WHERE MBMNumber = 'WMZCASE01*01'");
            Assert.Equal("Policy not yet activated!", (await Client().GetFromJsonAsync<CaseMemberView>($"{Url}/member?memberNumber=WMZCASE01*01"))!.BlockedReason);
            await main.ExecuteAsync("UPDATE MBM SET MBMPayorEffDate = DATEADD(day, -10, GETDATE()), MBMAvailableLimit = 0, MBMTopupInd = 'Y' WHERE MBMNumber = 'WMZCASE01*01'");
            var m2 = (await Client().GetFromJsonAsync<CaseMemberView>($"{Url}/member?memberNumber=WMZCASE01*01"))!;
            Assert.Contains("Limit Exceeded", m2.BlockedReason); Assert.Equal(2, m2.Warnings.Count);
        }
    }

    [DatabaseFact]
    public async Task Registering_writes_the_case_the_remarks_the_diagnoses_the_others_rows_and_the_cross_reference()
    {
        await Seed();
        var before = int.Parse((await Scalar<string>(GenesisDb.Maintenance, "SELECT ISNULL((SELECT CAQNumber FROM CAQ WHERE PAYCode = 'WM'), '00000')"))!);
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<CaseRegistrationResult>())!;
        Assert.Equal("WM" + (before + 1).ToString("00000"), r.CaseNumber);
        var n = new { n = r.CaseNumber };
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @n AND MBMNumber = 'WMZCASE01*01' AND MBMPatientCovID = '00' AND CASStatus = 'O' AND CASClaimability = 'AA' AND CASHOSCode = '00001' AND CASReserveAmount = 5000 AND CASClaimAdmin = '0' AND CaseOpenBy = 'TEST' AND CASPAYType = 'C' AND CaseRegDate = CAST(GETDATE() AS date)", n));
        Assert.Equal("remark one~line two", await Scalar<string>(GenesisDb.Main, "SELECT CASORemarks FROM CASRemarks WHERE CASNumber = @n", n));
        Assert.Equal(("A00", "cholera"), (await Scalar<string>(GenesisDb.Main, "SELECT RTRIM(ICDCode1) FROM DIAFirst WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT DIAPrelimDiag1 FROM DIAFirst WHERE CASNumber = @n", n)));
        Assert.Equal("final cholera", await Scalar<string>(GenesisDb.Main, "SELECT DIAFinalDiag1 FROM DIAFinal WHERE CASNumber = @n", n));
        Assert.Equal("35", await Scalar<string>(GenesisDb.Main, "SELECT CASPatientAge FROM CASOthers WHERE CASNumber = @n", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT CASTotalFinalDiag FROM CASOthersTwo WHERE CASNumber = @n", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASCrossReference WHERE CASNumber = @n AND MBMNumber = 'WMZCASE01*01' AND CASClaimability = 'A'", n));
        Assert.Equal((before + 1).ToString("00000"), await Scalar<string>(GenesisDb.Maintenance, "SELECT CAQNumber FROM CAQ WHERE PAYCode = 'WM'"));
        Assert.Equal("Case.Register", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Reimbursement_cases_are_flagged_for_claims_with_the_token_date_and_user()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req() with { PayType = "R", EspRefNo = "ESP-1", PolicyYear = null });
        var r = (await res.Content.ReadFromJsonAsync<CaseRegistrationResult>())!;
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @n AND CASClaimAdmin = '1' AND CASUserToken = 'TEST' AND CASESPRefNo = 'ESP-1' AND CASTokenDate = CAST(GETDATE() AS date)", new { n = r.CaseNumber }));
    }

    [DatabaseFact]
    public async Task The_legacy_confirmations_must_be_acknowledged()
    {
        await Seed(effDaysAgo: 10);
        var recent = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Conflict, recent.StatusCode); Assert.Contains("less than 30 days", await recent.Content.ReadAsStringAsync());
        var longStay = await Client().PostAsJsonAsync(Url, Req() with { AcknowledgeRecentPolicy = true, DateAdmitted = DateOnly.FromDateTime(DateTime.Today).AddDays(-20), DateDischarge = DateOnly.FromDateTime(DateTime.Today).AddDays(-10) });
        Assert.Equal(HttpStatusCode.Conflict, longStay.StatusCode); Assert.Contains("More Than 5 Days", await longStay.Content.ReadAsStringAsync());
        var first = await Client().PostAsJsonAsync(Url, Req() with { AcknowledgeRecentPolicy = true });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var dup = await Client().PostAsJsonAsync(Url, Req() with { AcknowledgeRecentPolicy = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode); Assert.Contains("already has case", await dup.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req() with { AcknowledgeRecentPolicy = true, AcknowledgeOpenCase = true })).StatusCode);
    }

    [DatabaseFact]
    public async Task Refusals_write_nothing_and_do_not_use_a_case_number()
    {
        await Seed();
        var counter = await Scalar<string>(GenesisDb.Maintenance, "SELECT CAQNumber FROM CAQ WHERE PAYCode = 'WM'");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { Claimability = "RR" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req(patient: "05"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req() with { HospitalCode = "NOHOSP" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req() with { Nature = "ZZZ" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req() with { FirstDiagnosis = [new("QQQQQ", null, null)] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req(member: "NOSUCH"))).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = 'WMZCASE01*01'");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(counter, await Scalar<string>(GenesisDb.Maintenance, "SELECT CAQNumber FROM CAQ WHERE PAYCode = 'WM'"));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE MBMNumber = 'WMZCASE01*01'"));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task A_generic_hospital_row_needs_the_real_name()
    {
        await Seed();
        var code = await Scalar<string>(GenesisDb.Main, "SELECT TOP 1 HOSCode FROM HOS WHERE HOSName = 'DIALYSIS CENTRE'");
        if (code is null) { await using var m = await factory.Db.OpenAsync(GenesisDb.Main); await m.ExecuteAsync("INSERT INTO HOS (HOSCode, HOSName) VALUES ('ZZGEN', 'DIALYSIS CENTRE')"); code = "ZZGEN"; }
        var res = await Client().PostAsJsonAsync(Url, Req() with { HospitalCode = code });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); Assert.Contains("Hospital Name or Clinic Name", await res.Content.ReadAsStringAsync());
        var ok = await Client().PostAsJsonAsync(Url, Req() with { HospitalCode = code, HospitalName = "CITY DIALYSIS SDN BHD" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }
}

