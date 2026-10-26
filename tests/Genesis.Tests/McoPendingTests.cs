using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class McoPendingRuleTests
{
    [Fact]
    public void Change_requires_status_and_valid_members()
    {
        Assert.Null(McoPendingRules.ValidateChange(new("P", ["XWGA00001*01"])));
        Assert.Equal("status", McoPendingRules.ValidateChange(new("B", ["X1"]))!.Value.Field);
        Assert.Equal("status", McoPendingRules.ValidateChange(new(null, ["X1"]))!.Value.Field);
        Assert.Equal("members", McoPendingRules.ValidateChange(new("P", []))!.Value.Field);
        Assert.Equal("members", McoPendingRules.ValidateChange(new("P", ["a'; --"]))!.Value.Field);
    }

    [Fact]
    public void Search_validates_inputs()
    {
        Assert.Null(McoPendingRules.ValidateSearch(new(null, null, null, null, null)));
        Assert.Equal("memberFrom", McoPendingRules.ValidateSearch(new("x'", null, null, null, null))!.Value.Field);
        Assert.Equal("bordxTo", McoPendingRules.ValidateSearch(new(null, null, new(2024, 2, 1), new(2024, 1, 1), null))!.Value.Field);
        Assert.Equal("status", McoPendingRules.ValidateSearch(new(null, null, null, null, "Z"))!.Value.Field);
    }
}

public class McoPendingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/mco-pending";
    private const string Member = "XWGA00001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(3));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<int> Count(string where, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM MCOPending WHERE {where}", args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url)).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(2));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url, new McoPendingChange("P", [Member]))).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_reads_only_fwhs_members_and_shows_existing_pending_rows()
    {
        var c = Client();
        var pending = await c.GetFromJsonAsync<McoPendingPage>($"{Url}?status=P");
        Assert.Equal(4, pending!.Rows.Count);
        Assert.Contains(pending.Rows, r => r.MemberNumber == "ALIA02021*01" && r.Status == "P" && r.BordxDate == new DateOnly(2005, 10, 28));

        var page = await c.GetFromJsonAsync<McoPendingPage>(Url);
        Assert.True(page!.Truncated);
        Assert.Equal(McoPendingRules.MaxRows, page.Rows.Count);
        var one = await c.GetFromJsonAsync<McoPendingPage>($"{Url}?memberFrom={Uri.EscapeDataString(Member)}&memberTo={Uri.EscapeDataString(Member)}");
        Assert.Equal(new DateOnly(2006, 4, 19), Assert.Single(one!.Rows).BordxDate);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{Url}?status=Z")).StatusCode);
    }

    [DatabaseFact]
    public async Task Pending_then_release_adds_and_removes_the_row_with_stamps_and_audit()
    {
        var c = Client();
        var add = await c.PutAsJsonAsync(Url, new McoPendingChange("P", [Member, Member]));
        Assert.Equal(new McoPendingResult(1, 0, 0), await add.Content.ReadFromJsonAsync<McoPendingResult>());
        Assert.Equal(1, await Count("MBMNumber = @m AND Status = 'P' AND LastUpdateUser = 'TEST' AND LastUpdateDate = CAST(GETDATE() AS date) AND MBMBordxDate = '2006-04-19'", new { m = Member }));

        var again = await c.PutAsJsonAsync(Url, new McoPendingChange("P", [Member]));
        Assert.Equal(new McoPendingResult(0, 0, 1), await again.Content.ReadFromJsonAsync<McoPendingResult>());
        Assert.Equal(1, await Count("MBMNumber = @m", new { m = Member }));

        var rel = await c.PutAsJsonAsync(Url, new McoPendingChange("R", [Member]));
        Assert.Equal(new McoPendingResult(0, 1, 0), await rel.Content.ReadFromJsonAsync<McoPendingResult>());
        Assert.Equal(0, await Count("MBMNumber = @m", new { m = Member }));
        Assert.Equal(["McoPending.Pend", "McoPending.Pend", "McoPending.Release"], factory.Audit.Entries.Select(e => e.Action));
    }

    [DatabaseFact]
    public async Task Billed_members_cannot_be_changed_and_nothing_else_in_the_request_is_applied()
    {
        var res = await Client().PutAsJsonAsync(Url, new McoPendingChange("R", ["ALIA02478*01", "ALIA02021*01"]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("billed", await res.Content.ReadAsStringAsync());
        Assert.Equal(5, await Count("1 = 1"));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Unknown_members_and_bad_input_are_refused_without_changes()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync(Url, new McoPendingChange("P", [Member, "NOSUCH*01"]))).StatusCode);
        Assert.Equal(0, await Count("MBMNumber = @m", new { m = Member }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync(Url, new McoPendingChange("B", [Member]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync(Url, new McoPendingChange("P", ["a'; DROP TABLE MCOPending;--"]))).StatusCode);
        Assert.Equal(5, await Count("1 = 1"));
    }
}
