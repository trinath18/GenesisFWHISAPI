using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class CaseAdjustmentRuleTests
{
    [Fact]
    public void Status_must_be_O_C_or_D_and_the_form_is_validated_like_registration()
    {
        var today = new DateOnly(2024, 6, 15);
        var ok = new CaseRegistrationRequest("M", "00", "AA", "00001", null, new(2024, 6, 10), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N", null, null, "x", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["r"], [new("A00", "c", null)], null, false, false, false);
        Assert.Null(CaseAdjustmentRules.Validate(new("C", ok), "M", "00", today));
        Assert.Equal("status", CaseAdjustmentRules.Validate(new("X", ok), "M", "00", today)!.Value.Field);
        Assert.Equal("form", CaseAdjustmentRules.Validate(new("O", null), "M", "00", today)!.Value.Field);
        Assert.Equal("reserveAmount", CaseAdjustmentRules.Validate(new("O", ok with { ReserveAmount = null }), "M", "00", today)!.Value.Field);
    }
}

[Collection("CaseRegistration")]
public class CaseAdjustmentTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Reg = "/api/case-registration", Url = "/api/case-adjustment";
    private HttpClient Reg11() => factory.ClientFor(WriteApiFactory.Access(11));
    private HttpClient Adj() => factory.ClientFor(WriteApiFactory.Access(12));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static CaseRegistrationRequest Req() => new("WMZCASE01*01", "00", "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N",
        null, "Dr Referrer", "fever", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["remark one\nline two", "remark two"], [new("A00", "cholera", null)], [new("A00", "final", null)], false, false, false);

    private async Task<string> Seed()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd)
                VALUES ('WMZCASE01*01', '00', 'A', 'CASE PRINCIPAL', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLC1', DATEADD(day, -200, CAST(GETDATE() AS date)), DATEADD(day, 165, CAST(GETDATE() AS date)), 20000, 'N')
                """);
        var res = await Reg11().PostAsJsonAsync(Reg, Req());
        return (await res.Content.ReadFromJsonAsync<CaseRegistrationResult>())!.CaseNumber;
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<CaseAdjustmentView> View(string no) => (await Adj().GetFromJsonAsync<CaseAdjustmentView>($"{Url}/{no}"))!;

    [Fact]
    public async Task Needs_position_12()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reg11().GetAsync($"{Url}/WM00001")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_registered_case_loads_back_with_all_its_fields()
    {
        var no = await Seed();
        var v = await View(no);
        Assert.Equal(("O", "CASE PRINCIPAL", "AA", "00001", 5000m), (v.Status, v.PatientName, v.Form.Claimability, v.Form.HospitalCode, v.Form.ReserveAmount));
        Assert.Equal("remark one\nline two", v.Form.Remarks![0]);
        Assert.Equal(("A00", "cholera"), (v.Form.FirstDiagnosis![0].Code, v.Form.FirstDiagnosis[0].Text));
        Assert.Equal("35", v.Form.PatientAge); Assert.Equal("WMZCASE01*01", v.Member!.MemberNumber);
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().GetAsync($"{Url}/NOCASE1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().GetAsync($"{Url}/BAD'CASE")).StatusCode);
    }

    [DatabaseFact]
    public async Task Next_and_previous_step_through_case_numbers_and_stop_at_the_ends()
    {
        var no = await Seed();
        var next = await Adj().GetAsync($"{Url}/{no}/next");
        if (next.StatusCode == HttpStatusCode.OK) Assert.True(string.CompareOrdinal((await next.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("caseNumber").GetString(), no) > 0);
        else Assert.Contains("Last Record", await next.Content.ReadAsStringAsync());
        var prev = await Adj().GetAsync($"{Url}/{no}/previous");
        Assert.Equal(HttpStatusCode.OK, prev.StatusCode);
        Assert.True(string.CompareOrdinal((await prev.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("caseNumber").GetString(), no) < 0);
        var first = await Adj().GetAsync($"{Url}/0/previous");
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); Assert.Contains("First Record", await first.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Updating_changes_the_case_its_children_and_the_cross_reference_but_not_member_patient_or_opener()
    {
        var no = await Seed();
        var v = await View(no);
        var form = v.Form with
        {
            Claimability = "PP", ReserveAmount = 7000m, Remarks = ["changed remark", "second"], FirstDiagnosis = [new("A00", "new prelim", null)], FinalDiagnosis = [new("A00", "new final", null)], AttendingDoctors = ["Dr New"],
            Procedures = [new("Surgery", "S1")], MemberNumber = "SOMEONE", PatientId = "05", PatientAge = "40", Pending = "Y"
        };
        var res = await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("C", form));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var n = new { n = no };
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CAS WHERE CASNumber = @n AND CASStatus = 'C' AND CASClaimability = 'PP' AND CASReserveAmount = 7000 AND CASPending = 'Y' AND MBMNumber = 'WMZCASE01*01' AND MBMPatientCovID = '00' AND CaseOpenBy = 'TEST' AND CASLastUpdateUser = 'TEST' AND CASLastUpdateDate = CAST(GETDATE() AS date)", n));
        Assert.Equal("changed remark", await Scalar<string>(GenesisDb.Main, "SELECT CASORemarks FROM CASRemarks WHERE CASNumber = @n", n));
        Assert.Equal("new prelim", await Scalar<string>(GenesisDb.Main, "SELECT DIAPrelimDiag1 FROM DIAFirst WHERE CASNumber = @n", n));
        Assert.Equal("new final", await Scalar<string>(GenesisDb.Main, "SELECT DIAFinalDiag1 FROM DIAFinal WHERE CASNumber = @n", n));
        Assert.Equal(("40", "Surgery"), (await Scalar<string>(GenesisDb.Main, "SELECT CASPatientAge FROM CASOthers WHERE CASNumber = @n", n), await Scalar<string>(GenesisDb.Main, "SELECT CASOPROCRequired1 FROM CASOthers WHERE CASNumber = @n", n)));
        Assert.Equal("Dr New", await Scalar<string>(GenesisDb.Main, "SELECT CASAttendDoctor1 FROM CASOthersTwo WHERE CASNumber = @n", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM CASCrossReference WHERE CASNumber = @n AND CASStatus = 'C' AND CASClaimability = 'P' AND CASPending = 'Y'", n));
        Assert.Equal(20000m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = 'WMZCASE01*01'"));
        Assert.Equal("Case.Adjust", factory.Audit.Entries.Last().Action);
    }

    [DatabaseFact]
    public async Task A_repudiated_status_stores_the_repudiation_and_invalid_edits_change_nothing()
    {
        var no = await Seed();
        var v = await View(no);
        var rep = await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("O", v.Form with { Claimability = "RR", RepudiationCodes = ["RC1"], Document = "D0001", Personnel = "P0001", Attention = "A0001", Underlying = true }));
        Assert.Equal(HttpStatusCode.OK, rep.StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASRepudiation WHERE CASNumber = @n AND CASREPCode1 = 'RC1' AND CASUnderlying = 'Y' AND CASREPIssuedBy = 'TEST'", new { n = no }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("X", v.Form))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("O", v.Form with { Claimability = "RR" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("O", v.Form with { HospitalCode = "NOHOSP" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("O", v.Form with { FinalDiagnosis = [new("QQQQQ", null, null)] }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().PutAsJsonAsync($"{Url}/NOCASE1", new CaseAdjustmentRequest("O", v.Form))).StatusCode);
        var long_ = await Adj().PutAsJsonAsync($"{Url}/{no}", new CaseAdjustmentRequest("O", v.Form with { DateDischarge = v.Form.DateAdmitted!.Value.AddDays(9) }));
        Assert.Equal(HttpStatusCode.Conflict, long_.StatusCode);
        Assert.Equal("RR", await Scalar<string>(GenesisDb.Main, "SELECT CASClaimability FROM CAS WHERE CASNumber = @n", new { n = no }));
    }
}
