using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class CardSendingRuleTests
{
    private static CardSendingUpdate Ok() => new("By Post", new DateOnly(2024, 5, 1), [new("WMGA6639886*01", "00")]);

    [Fact]
    public void Valid_update_passes() => Assert.Null(CardSendingRules.ValidateUpdate(Ok()));

    [Fact]
    public void Update_requires_mode_date_and_members()
    {
        Assert.Equal("mode", CardSendingRules.ValidateUpdate(Ok() with { Mode = "" })!.Value.Field);
        Assert.Equal("mode", CardSendingRules.ValidateUpdate(Ok() with { Mode = "By Pigeon" })!.Value.Field);
        Assert.Equal("date", CardSendingRules.ValidateUpdate(Ok() with { Date = null })!.Value.Field);
        Assert.Equal("date", CardSendingRules.ValidateUpdate(Ok() with { Date = new DateOnly(1800, 1, 1) })!.Value.Field);
        Assert.Equal("members", CardSendingRules.ValidateUpdate(Ok() with { Members = [] })!.Value.Field);
        Assert.Equal("members", CardSendingRules.ValidateUpdate(Ok() with { Members = [new("a'; --", "00")] })!.Value.Field);
        Assert.Equal("members", CardSendingRules.ValidateUpdate(Ok() with { Members = [new("WM1", "0")] })!.Value.Field);
    }

    [Fact]
    public void Search_rejects_bad_ranges_and_values()
    {
        var empty = new CardSendingCriteria(null, null, null, null, null, null, null, null, null, null);
        Assert.Null(CardSendingRules.ValidateSearch(empty));
        Assert.Equal("payor", CardSendingRules.ValidateSearch(empty with { Payor = "WMX" })!.Value.Field);
        Assert.Equal("memberFrom", CardSendingRules.ValidateSearch(empty with { MemberFrom = "x'" })!.Value.Field);
        Assert.Equal("bordxTo", CardSendingRules.ValidateSearch(empty with { BordxFrom = new(2024, 2, 1), BordxTo = new(2024, 1, 1) })!.Value.Field);
        Assert.Equal("batchTo", CardSendingRules.ValidateSearch(empty with { BatchFrom = 5, BatchTo = 1 })!.Value.Field);
        Assert.Equal("mode", CardSendingRules.ValidateSearch(empty with { Mode = "x" })!.Value.Field);
    }
}

public class CardSendingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/card-sending";
    private const string Member = "XWGA00001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(1));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url)).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url, new CardSendingUpdate("By Hand", new DateOnly(2024, 1, 1), [new(Member, "00")]))).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_filters_principals_and_caps_results()
    {
        var c = Client();
        var one = await c.GetFromJsonAsync<CardSendingPage>($"{Url}?memberFrom={Uri.EscapeDataString(Member)}&memberTo={Uri.EscapeDataString(Member)}");
        var row = Assert.Single(one!.Rows);
        Assert.Equal((Member, "00", "MOHAMAD SUSANTO"), (row.MemberNumber, row.CoverId, row.Name));
        Assert.Equal(new DateOnly(2006, 4, 19), row.BordxDate);
        Assert.Equal((1, 0, false), (one.Principals, one.Supplementary, one.Truncated));

        var big = await c.GetFromJsonAsync<CardSendingPage>(Url);
        Assert.True(big!.Truncated);
        Assert.Equal(CardSendingRules.MaxPrincipals, big.Rows.Count);

        Assert.Empty((await c.GetFromJsonAsync<CardSendingPage>($"{Url}?batchFrom=999999"))!.Rows);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{Url}?memberFrom=a'b")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{Url}?mode=Nope")).StatusCode);
    }

    [DatabaseFact]
    public async Task Updating_sets_mode_and_date_for_exactly_the_listed_principals_and_audits()
    {
        var c = Client();
        var before = await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMSendingMode IS NOT NULL");
        var res = await c.PutAsJsonAsync(Url, new CardSendingUpdate("By Post", new DateOnly(2024, 5, 1), [new(Member, "00"), new(Member, "00")]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<CardSendingResult>())!.Updated);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @m AND MBMSendingMode = 'By Post' AND MBMCardDate = '2024-05-01'", new { m = Member }));
        Assert.Equal(before + 1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMSendingMode IS NOT NULL"));

        var after = await c.GetFromJsonAsync<CardSendingPage>($"{Url}?memberFrom={Uri.EscapeDataString(Member)}&memberTo={Uri.EscapeDataString(Member)}&mode=By%20Post");
        Assert.Equal(new DateOnly(2024, 5, 1), Assert.Single(after!.Rows).CardDate);
        var entry = Assert.Single(factory.Audit.Entries);
        Assert.Equal(("CardSending.Update", "TEST"), (entry.Action, entry.User));
    }

    [DatabaseFact]
    public async Task One_unknown_member_rolls_back_the_whole_update()
    {
        var res = await Client().PutAsJsonAsync(Url, new CardSendingUpdate("By Hand", new DateOnly(2024, 5, 1), [new(Member, "00"), new("NOSUCH*01", "00")]));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMSendingMode = 'By Hand'"));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Covered_persons_are_listed_for_insurer_F_and_H_and_updated_in_their_own_table()
    {
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await conn.ExecuteAsync("UPDATE MBM SET INSCode = 'F' WHERE MBMNumber = @m", new { m = Member });
            await conn.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName) VALUES (@m, '01', 'TEST SPOUSE')", new { m = Member });
            await conn.ExecuteAsync("INSERT INTO MBMCoveredPersonsTwo (MBMCNumber, MBMCCoverID) VALUES (@m, '01')", new { m = Member });
        }
        var c = Client();
        var page = await c.GetFromJsonAsync<CardSendingPage>($"{Url}?memberFrom={Uri.EscapeDataString(Member)}&memberTo={Uri.EscapeDataString(Member)}");
        Assert.Equal((1, 1), (page!.Principals, page.Supplementary));
        Assert.Contains(page.Rows, r => r.CoverId == "01" && r.Name == "TEST SPOUSE");

        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync(Url, new CardSendingUpdate("By Hand", new DateOnly(2024, 6, 2), [new(Member, "01")]))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @m AND MBMCCoverID = '01' AND MBMSendingMode = 'By Hand' AND MBMCardDate = '2024-06-02'", new { m = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @m AND MBMSendingMode IS NOT NULL", new { m = Member }));
    }
}
