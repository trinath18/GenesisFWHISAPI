using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class NonDisclosureRuleTests
{
    private static readonly DateOnly Today = new(2024, 6, 15);

    [Fact]
    public void Categories_follow_the_legacy_lists_and_dates_must_be_real()
    {
        Assert.Null(NonDisclosureRules.Validate(new(Today, "2", "HOSPITAL", Today, "1", "CLINIC"), Today));
        Assert.Null(NonDisclosureRules.Validate(new(null, null, null, null, null, null), Today));
        Assert.Equal("preExistingCategory", NonDisclosureRules.Validate(new(null, "3", null, null, null, null), Today)!.Value.Field);
        Assert.Equal("underlyingCategory", NonDisclosureRules.Validate(new(null, null, null, null, "2", null), Today)!.Value.Field);
        Assert.Equal("preExistingDate", NonDisclosureRules.Validate(new(Today.AddDays(1), null, null, null, null, null), Today)!.Value.Field);
        Assert.Equal("underlyingInformedBy", NonDisclosureRules.Validate(new(null, null, null, null, null, new string('x', 51)), Today)!.Value.Field);
    }
}

public class CaseNonDisclosureTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Member = "WMZND01*01";
    private HttpClient Cli() => factory.ClientFor(WriteApiFactory.Access(11));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string ins, bool principalDetail = true)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate)
            VALUES (@m, '00', 'A', 'ND PRINCIPAL', 'IC1', 'WM', 'N', @ins, 'ZZP1', 'POLND1', '2024-01-01', '2024-12-31')
            """, new { m = Member, ins });
        if (principalDetail) await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@m)", new { m = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCStatus) VALUES (@m, '01', 'ND SPOUSE', 'A'), (@m, '02', 'ND CHILD', 'A')", new { m = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersonsTwo (MBMCNumber, MBMCCoverID) VALUES (@m, '01')", new { m = Member });
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, new { m = Member });
    }

    [Fact]
    public async Task Needs_position_11()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(13)).GetAsync("/api/case-non-disclosure/WMZND01*01")).StatusCode);
    }

    [DatabaseFact]
    public async Task Family_membership_lists_the_covered_persons_and_saves_principal_and_covered_person()
    {
        await Seed("F");
        var view = (await Cli().GetFromJsonAsync<NonDisclosureView>($"/api/case-non-disclosure/{Member}", Json))!;
        Assert.Equal(["00", "01", "02"], view.Persons.Select(p => p.CoverId));
        Assert.Equal("ND PRINCIPAL", view.Persons[0].Name);
        Assert.False(view.Persons[2].HasDetailRecord);

        var res = await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/00", new NonDisclosureRequest(new DateOnly(2023, 3, 2), "1", "HOSPITAL A", null, null, null));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @m AND MBMPreIllDate = '2023-03-02' AND MBMPreIllCAT = '1' AND MBMPreIllInfBy = 'HOSPITAL A' AND MBMUndIllDate IS NULL"));

        res = await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/01", new NonDisclosureRequest(null, null, null, new DateOnly(2022, 6, 1), "0", "CLINIC"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @m AND MBMCCoverID = '01' AND MBMCUndIllCAT = '0' AND MBMCUndInfBy = 'CLINIC' AND MBMCUndIllDate = '2022-06-01'"));
        Assert.Equal("Member.NonDisclosure", factory.Audit.Entries.Last().Action);

        // Cover 02 has no MBMCoveredPersonsTwo row: refused like the legacy form, which silently saved nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/02", new NonDisclosureRequest(null, "0", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/09", new NonDisclosureRequest(null, "0", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/00", new NonDisclosureRequest(null, "7", null, null, null, null))).StatusCode);
    }

    [DatabaseFact]
    public async Task Single_membership_shows_only_the_principal_and_unknown_members_are_not_found()
    {
        await Seed("S", principalDetail: false);
        var view = (await Cli().GetFromJsonAsync<NonDisclosureView>($"/api/case-non-disclosure/{Member}", Json))!;
        Assert.Equal("00", Assert.Single(view.Persons).CoverId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PutAsJsonAsync($"/api/case-non-disclosure/{Member}/00", new NonDisclosureRequest(null, "0", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli().GetAsync("/api/case-non-disclosure/NOSUCHMEMBER1")).StatusCode);
        Assert.NotNull((await (await Cli().GetAsync($"/api/case-non-disclosure/{Member}/next")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memberNumber").GetString());
    }
}
