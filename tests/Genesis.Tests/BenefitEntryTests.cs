using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;

namespace Genesis.Tests;

public class BenefitRuleTests
{
    private static BenefitSaveRequest Ok() => new("N", "G", "ZZP1", new(2023, 1, 1), [new("901", 5, "100", "FULL")]);

    [Fact]
    public void Valid_request_passes() => Assert.Null(BenefitRules.Validate(Ok()));

    [Fact]
    public void Header_and_lines_are_checked_with_the_legacy_messages()
    {
        Assert.Equal("health", BenefitRules.Validate(Ok() with { Health = "" })!.Value.Field);
        Assert.Equal("insured", BenefitRules.Validate(Ok() with { Insured = "" })!.Value.Field);
        Assert.Equal("plan", BenefitRules.Validate(Ok() with { Plan = "a'b" })!.Value.Field);
        Assert.Equal("effectiveDate", BenefitRules.Validate(Ok() with { EffectiveDate = null })!.Value.Field);
        Assert.Equal("lines", BenefitRules.Validate(Ok() with { Lines = [new("901", null, " ", null)] })!.Value.Field);
        Assert.Equal("lines", BenefitRules.Validate(Ok() with { Lines = [new("901", 1, null, null), new("901", 2, null, null)] })!.Value.Field);
        Assert.Equal("lines", BenefitRules.Validate(Ok() with { Lines = [new("901", 40000, null, null)] })!.Value.Field);
        Assert.Equal("lines", BenefitRules.Validate(Ok() with { Lines = [new("901", 1, new string('x', 31), null)] })!.Value.Field);
    }
}

public class BenefitEntryTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/maintenance/benefits";
    private HttpClient Entry() => factory.ClientFor(WriteApiFactory.Access(88));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static BenefitSaveRequest Req(DateOnly? eff = null, params BenefitLineInput[] lines) =>
        new("N", "G", "ZZP1", eff ?? new(2023, 1, 1), lines.Length > 0 ? lines : [new("901", 5, "100", "FULL"), new("804", null, "50", null), new("803", null, null, null)]);

    private async Task SeedPlan()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, GRPCompany) VALUES ('ZZP1', 'N', 'ZZ', '2020-01-01', 'ZZ GROUP LTD')");
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Entry_and_listing_have_their_own_legacy_positions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/listing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Entry().GetAsync($"{Url}/listing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(89)).GetAsync($"{Url}/options")).StatusCode);
    }

    [DatabaseFact]
    public async Task The_grid_lists_every_sub_category_highest_code_first_with_nothing_stored_yet()
    {
        var o = (await Entry().GetFromJsonAsync<BenefitOptions>($"{Url}/options"))!;
        Assert.Contains(o.HealthPlans, h => h.Code == "N"); Assert.NotEmpty(o.Insured); Assert.NotEmpty(o.Plans);
        var e = (await Entry().GetFromJsonAsync<BenefitEntry>($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=2023-01-01"))!;
        Assert.False(e.Exists);
        Assert.Equal(await Scalar<int>("SELECT COUNT(*) FROM BNFSubCat"), e.Lines.Count);
        Assert.Equal(e.Lines.Select(l => l.Code).OrderByDescending(c => c, StringComparer.Ordinal).ToList(), e.Lines.Select(l => l.Code).ToList());
        Assert.All(e.Lines, l => Assert.Null(l.Days));
        Assert.Equal(HttpStatusCode.BadRequest, (await Entry().GetAsync($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=1800-01-01")).StatusCode);
    }

    [DatabaseFact]
    public async Task Adding_stores_only_the_lines_with_values_stamps_them_and_refuses_a_duplicate_version()
    {
        await SeedPlan();
        var res = await Entry().PostAsJsonAsync($"{Url}/entry", Req());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE PLNCode = 'ZZP1' AND INSCode = 'G' AND BNFEffDate = '2023-01-01' AND HLTCode = 'N' AND BNFLastUpdateUser = 'TEST'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE BNFCode = '901' AND PLNCode = 'ZZP1' AND BNFNoOfDays = 5 AND BNFClaimAbleAmount = '100' AND BNFRMPerDis = 'FULL'"));
        var e = (await Entry().GetFromJsonAsync<BenefitEntry>($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=2023-01-01"))!;
        Assert.True(e.Exists);
        Assert.Equal(("FULL", "50"), (e.Lines.Single(l => l.Code == "901").PerDisability, e.Lines.Single(l => l.Code == "804").Amount));
        Assert.Equal("Benefit.Add", Assert.Single(factory.Audit.Entries).Action);
        var dup = await Entry().PostAsJsonAsync($"{Url}/entry", Req());
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("Duplicated", await dup.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await Entry().PostAsJsonAsync($"{Url}/entry", Req(new(2024, 1, 1)))).StatusCode);
    }

    [DatabaseFact]
    public async Task Updating_replaces_the_version_with_the_grid_sent()
    {
        await SeedPlan();
        await Entry().PostAsJsonAsync($"{Url}/entry", Req());
        var edit = await Entry().PutAsJsonAsync($"{Url}/entry", Req(null, new BenefitLineInput("901", 7, "120", "FULL"), new BenefitLineInput("804", null, null, null), new BenefitLineInput("803", 2, null, null)));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE BNFCode = '901' AND PLNCode = 'ZZP1' AND BNFNoOfDays = 7 AND BNFClaimAbleAmount = '120'"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE BNFCode = '804' AND PLNCode = 'ZZP1'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE BNFCode = '803' AND PLNCode = 'ZZP1' AND BNFNoOfDays = 2"));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE PLNCode = 'ZZP1'"));
        Assert.Equal(["Benefit.Add", "Benefit.Update"], factory.Audit.Entries.Select(x => x.Action));
        Assert.Equal(HttpStatusCode.NotFound, (await Entry().PutAsJsonAsync($"{Url}/entry", Req(new(2030, 1, 1)))).StatusCode);
    }

    [DatabaseFact]
    public async Task Invalid_saves_change_nothing()
    {
        await SeedPlan();
        Assert.Equal(HttpStatusCode.BadRequest, (await Entry().PostAsJsonAsync($"{Url}/entry", Req(null, new BenefitLineInput("901", null, null, null)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Entry().PostAsJsonAsync($"{Url}/entry", Req(null, new BenefitLineInput("NOPE", 1, null, null)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Entry().PostAsJsonAsync($"{Url}/entry", Req() with { Plan = "NOPE" })).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE PLNCode IN ('ZZP1', 'NOPE')"));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Deleting_removes_only_that_version_and_is_refused_while_members_use_the_plan()
    {
        await SeedPlan();
        await Entry().PostAsJsonAsync($"{Url}/entry", Req());
        await Entry().PostAsJsonAsync($"{Url}/entry", Req(new(2024, 1, 1)));
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMStatus, MBMName, MBMIcBcPp, HLTCode, PAYCode, INSCode, PLNCode) VALUES ('ZZBNF0001*01', 'A', 'M', 'IC', 'N', 'ZZ', 'G', 'ZZP1')");
        var blocked = await Entry().DeleteAsync($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=2023-01-01");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("used in Member table", await blocked.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("DELETE FROM MBM WHERE MBMNumber = 'ZZBNF0001*01'");
        Assert.Equal(HttpStatusCode.OK, (await Entry().DeleteAsync($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=2023-01-01")).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE PLNCode = 'ZZP1' AND BNFEffDate = '2023-01-01'"));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM BNF WHERE PLNCode = 'ZZP1' AND BNFEffDate = '2024-01-01'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Entry().DeleteAsync($"{Url}/entry?health=N&insured=G&plan=ZZP1&effectiveDate=2023-01-01")).StatusCode);
    }

    [DatabaseFact]
    public async Task Listing_filters_by_plan_date_code_payor_and_group()
    {
        await SeedPlan();
        await Entry().PostAsJsonAsync($"{Url}/entry", Req());
        await Entry().PostAsJsonAsync($"{Url}/entry", Req(new(2024, 1, 1)));
        var list = factory.ClientFor(WriteApiFactory.Access(89));
        async Task<List<BenefitListingRow>> Get(string q) => (await list.GetFromJsonAsync<List<BenefitListingRow>>($"{Url}/listing?{q}"))!;
        Assert.Equal(4, (await Get("plan=ZZP1")).Count);
        var one = await Get("plan=ZZP1&effectiveDate=2024-01-01&code=901");
        var row = Assert.Single(one);
        Assert.Equal(("901", new DateOnly(2024, 1, 1), 5, "100", "FULL"), (row.Code, row.EffectiveDate, row.Days, row.Amount, row.PerDisability));
        Assert.Equal(4, (await Get("payor=ZZ&plan=ZZP1")).Count);
        Assert.Empty(await Get("payor=XX&plan=ZZP1"));
        Assert.Equal(4, (await Get("group=ZZ%20GROUP&plan=ZZP1")).Count);
        Assert.Empty(await Get("group=OTHER&plan=ZZP1"));
        Assert.NotNull((await Get("plan=ZZP1")).First().Name);
    }
}
