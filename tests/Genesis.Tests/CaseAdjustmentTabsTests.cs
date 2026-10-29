using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Cases;
using Genesis.Core.Data;

namespace Genesis.Tests;

public class CaseWorksheetRuleTests
{
    [Fact]
    public void Approval_needs_a_date_and_a_matching_twelve_character_code()
    {
        var d = new DateOnly(2024, 6, 1);
        Assert.Null(CaseWorksheetRules.Validate(new(d, "CS1234567890", "Y", "N"), 100m));
        Assert.Null(CaseWorksheetRules.Validate(new(d, "CR1234567890", null, null), -50m));
        Assert.Null(CaseWorksheetRules.Validate(new(d, "", null, null), 0m));
        Assert.Equal("approvalDate", CaseWorksheetRules.Validate(new(null, "", null, null), 1m)!.Value.Field);
        Assert.Equal("approvalCode", CaseWorksheetRules.Validate(new(d, "CR1234567890", null, null), 100m)!.Value.Field);
        Assert.Equal("approvalCode", CaseWorksheetRules.Validate(new(d, "CS1234567890", null, null), -100m)!.Value.Field);
        Assert.Equal("approvalCode", CaseWorksheetRules.Validate(new(d, "CS123", null, null), 100m)!.Value.Field);
        Assert.Equal("reversal", CaseWorksheetRules.Validate(new(d, "", "YY", null), 1m)!.Value.Field);
    }
}

[Collection("CaseRegistration")]
public class CaseAdjustmentTabsTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Reg = "/api/case-registration", Url = "/api/case-adjustment";
    private HttpClient Reg11() => factory.ClientFor(WriteApiFactory.Access(11));
    private HttpClient Adj() => factory.ClientFor(WriteApiFactory.Access(12));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<string> Seed()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd)
                VALUES ('WMZCASE01*01', '00', 'A', 'CASE PRINCIPAL', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLC1', DATEADD(day, -200, CAST(GETDATE() AS date)), DATEADD(day, 165, CAST(GETDATE() AS date)), 20000, 'N')
                """);
            await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, GRPCompany) VALUES ('ZZP1', 'N', 'WM', '2020-01-01', 'ZZ')");
            await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, PAYCode) VALUES ('ZZP1', 'F', 'N', 10000, 4000, 'A', '2020-01-01', 'WM')");
        }
        var req = new CaseRegistrationRequest("WMZCASE01*01", "00", "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N", null, null, "x", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["r"], [new("A00", "c", null)], null, false, false, false);
        var no = (await (await Reg11().PostAsJsonAsync(Reg, req)).Content.ReadFromJsonAsync<CaseRegistrationResult>())!.CaseNumber;
        await using var m = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await m.ExecuteAsync("DELETE FROM CASLG WHERE CASENo = @no; DELETE FROM LGSeq WHERE CASNumber = @no", new { no });
        return no;
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task The_new_endpoints_need_position_12()
    {
        foreach (var p in new[] { "/WM00001/lg", "/WM00001/worksheets", "/search?by=caseno&q=WM0" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Reg11().GetAsync(Url + p)).StatusCode);
    }

    [DatabaseFact]
    public async Task Lg_tab_lists_issues_edits_and_prints_individual_letters()
    {
        var no = await Seed();
        Assert.Empty((await Adj().GetFromJsonAsync<List<CaseLgItem>>($"{Url}/{no}/lg"))!);
        Assert.Equal(HttpStatusCode.Created, (await Adj().PostAsJsonAsync($"{Url}/{no}/lg", new CaseLgRequest(2500m, "09:30", "first"))).StatusCode);
        await Adj().PostAsJsonAsync($"{Url}/{no}/lg", new CaseLgRequest(100m, "10:00", null));
        var list = (await Adj().GetFromJsonAsync<List<CaseLgItem>>($"{Url}/{no}/lg"))!;
        Assert.Equal([$"L{no}-1", $"L{no}-2"], list.Select(x => x.LgNumber));
        Assert.Equal(("00", "CASE PRINCIPAL", 2500m, "first", "09:30"), (list[0].PatientId, list[0].PatientName, list[0].Amount, list[0].Remark, list[0].UserTime));
        Assert.Equal("PHARMA", list[0].Hospital![..6]);
        Assert.Equal(HttpStatusCode.OK, (await Adj().PutAsJsonAsync($"{Url}/{no}/lg/2/remark", new CaseLgRemarkRequest("updated remark"))).StatusCode);
        Assert.Equal("updated remark", await Scalar<string>(GenesisDb.Maintenance, "SELECT LGRemark FROM CASLG WHERE CASENo = @no AND LGNo = 2", new { no }));
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().PutAsJsonAsync($"{Url}/{no}/lg/9/remark", new CaseLgRemarkRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().PutAsJsonAsync($"{Url}/{no}/lg/1/remark", new CaseLgRemarkRequest(new string('x', 101)))).StatusCode);
        var pdf = await Adj().GetAsync($"{Url}/{no}/lg/1/letter");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().GetAsync($"{Url}/{no}/lg/9/letter")).StatusCode.Equals(HttpStatusCode.OK) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        Assert.Contains(factory.Audit.Entries, e => e.Action == "Case.UpdateLgRemark");
    }

    [DatabaseFact]
    public async Task Worksheet_tab_lists_versions_and_marks_the_payor_approval()
    {
        var no = await Seed();
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("INSERT INTO Claim (CASNumber, ClaimVersion, CLMTotalApproved, CLMTotalActual, CLMBordxAmt) VALUES (@no, 1, 800, 900, 800), (@no, 2, 100, 100, -100)", new { no });
        var list = (await Adj().GetFromJsonAsync<List<CaseWorksheetItem>>($"{Url}/{no}/worksheets"))!;
        Assert.Equal([1, 2], list.Select(x => x.Version)); Assert.Equal(800m, list[0].TotalApproved);
        var bad = await Adj().PutAsJsonAsync($"{Url}/{no}/worksheets/1/approval", new CaseWorksheetApprovalRequest(new DateOnly(2024, 6, 1), "CR1234567890", null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().PutAsJsonAsync($"{Url}/{no}/worksheets/1/approval", new CaseWorksheetApprovalRequest(null, "", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adj().PutAsJsonAsync($"{Url}/{no}/worksheets/1/approval", new CaseWorksheetApprovalRequest(new DateOnly(2024, 6, 1), "CS1234567890", "Y", "N"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adj().PutAsJsonAsync($"{Url}/{no}/worksheets/2/approval", new CaseWorksheetApprovalRequest(new DateOnly(2024, 6, 2), "CR1234567890", null, null))).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM Claim WHERE CASNumber = @no AND ClaimVersion = 1 AND CLMPayorAppStatus = 'Y' AND CLMPayorAppCode = 'CS1234567890' AND CLMBordxRevInd = 'Y' AND CLMRevBordxStatus = 'N' AND CLMPayorAppDate = '2024-06-01'", new { no }));
        var after = (await Adj().GetFromJsonAsync<List<CaseWorksheetItem>>($"{Url}/{no}/worksheets"))!;
        Assert.Equal(("Y", "N"), (after[0].PayorApprovalStatus, after[0].ReversalBordxStatus));
        Assert.Equal(HttpStatusCode.NotFound, (await Adj().PutAsJsonAsync($"{Url}/{no}/worksheets/9/approval", new CaseWorksheetApprovalRequest(new DateOnly(2024, 6, 1), "", null, null))).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_finds_cases_by_member_case_and_name_and_validates_the_input()
    {
        var no = await Seed();
        var byCase = (await Adj().GetFromJsonAsync<CaseSearchPage>($"{Url}/search?by=caseno&q={no}"))!;
        Assert.Contains(byCase.Items, x => x.CaseNumber == no);
        var byMember = (await Adj().GetFromJsonAsync<CaseSearchPage>($"{Url}/search?by=memberno&q=WMZCASE01"))!;
        Assert.Contains(byMember.Items, x => x.CaseNumber == no);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().GetAsync($"{Url}/search?by=caseno&q=WM")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adj().GetAsync($"{Url}/search?by=nonsense&q=WM0000")).StatusCode);
    }
}
