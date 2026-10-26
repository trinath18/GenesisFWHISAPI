using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;

namespace Genesis.Tests;

public class SuspensionRuleTests
{
    private static SuspensionRequest Ok() => new("01", "WMTEST0001*01", "Under review", "WM");

    [Fact]
    public void Valid_request_passes() => Assert.Null(SuspensionRules.Validate(Ok()));

    [Theory]
    [InlineData(null, "WMX", "r", "WM", "kind")]
    [InlineData("03", "WMX", "r", "WM", "kind")]
    [InlineData("01", "", "r", "WM", "code")]
    [InlineData("01", "WMX", "", "WM", "remarks")]
    [InlineData("01", "WMX", "r", "", "payorCode")]
    [InlineData("01", "WMX", "r", "WMX", "payorCode")]
    [InlineData("01", "ABX", "r", "WM", "code")]
    public void Invalid_requests_name_the_field(string? kind, string code, string remarks, string payor, string field) =>
        Assert.Equal(field, SuspensionRules.Validate(new(kind, code, remarks, payor))!.Value.Field);

    [Fact]
    public void Length_limits_follow_the_real_columns()
    {
        Assert.NotNull(SuspensionRules.Validate(Ok() with { Code = "WM" + new string('1', 49) }));
        Assert.NotNull(SuspensionRules.Validate(new("02", new string('A', 16), "r", "WM")));
        Assert.Null(SuspensionRules.Validate(new("02", new string('A', 15), "r", "WM")));
        Assert.NotNull(SuspensionRules.Validate(Ok() with { Remarks = new string('x', 101) }));
    }

    [Fact]
    public void Agent_codes_do_not_need_the_payor_prefix() => Assert.Null(SuspensionRules.Validate(new("02", "AGENT7", "r", "WM")));
}

public class SuspensionTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/maintenance/suspensions";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(110));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<int> Count(string where, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM SuspensionDetail WHERE {where}", args);
    }

    [Fact]
    public async Task Requires_authentication_and_the_suspension_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url)).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, new SuspensionRequest("01", "WMX", "r", "WM"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{Url}?kind=01&code=WMX")).StatusCode);
    }

    [DatabaseFact]
    public async Task Lists_payors_and_starts_empty()
    {
        var c = Client();
        Assert.Contains((await c.GetFromJsonAsync<List<Payor>>($"{Url}/payors"))!, p => p.Code == "WM");
        Assert.Empty((await c.GetFromJsonAsync<List<Suspension>>(Url))!);
    }

    [DatabaseFact]
    public async Task Member_suspension_create_search_update_delete_with_stamps_and_audit()
    {
        var c = Client();
        var created = await c.PostAsJsonAsync(Url, new SuspensionRequest("01", " WMTEST0001*01 ", "  Under review ", "WM"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var s = (await created.Content.ReadFromJsonAsync<Suspension>())!;
        Assert.Equal(("01", "WMTEST0001*01", "Under review", "WM"), (s.Kind, s.Code, s.Remarks, s.PayorCode));
        Assert.Equal("TEST", s.UpdatedBy);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), s.UpdatedOn);
        Assert.Equal(1, await Count("SuspensionCode = '01' AND MembershipNo = 'WMTEST0001*01' AND PayorCode = 'WM'"));

        Assert.Single((await c.GetFromJsonAsync<List<Suspension>>($"{Url}?kind=01&code=WMTEST"))!);
        Assert.Empty((await c.GetFromJsonAsync<List<Suspension>>($"{Url}?kind=02"))!);
        Assert.Empty((await c.GetFromJsonAsync<List<Suspension>>($"{Url}?code=%25"))!);

        var updated = await c.PutAsJsonAsync(Url, new SuspensionRequest("01", "WMTEST0001*01", "Cleared soon", "WM"));
        Assert.Equal("Cleared soon", (await updated.Content.ReadFromJsonAsync<Suspension>())!.Remarks);

        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"{Url}?kind=01&code=WMTEST0001*01")).StatusCode);
        Assert.Equal(0, await Count("MembershipNo = 'WMTEST0001*01'"));
        Assert.Equal(["Suspension.Create", "Suspension.Update", "Suspension.Delete"], factory.Audit.Entries.Select(e => e.Action));
        Assert.All(factory.Audit.Entries, e => Assert.Equal("01:WMTEST0001*01", e.Key));
    }

    [DatabaseFact]
    public async Task Agent_suspension_uses_the_agent_code_column()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url, new SuspensionRequest("02", "AGENT7", "Agent on hold", "WM"))).StatusCode);
        Assert.Equal(1, await Count("SuspensionCode = '02' AND AgentCode = 'AGENT7'"));
        var list = await c.GetFromJsonAsync<List<Suspension>>($"{Url}?kind=02&code=AGENT");
        Assert.Equal("AGENT7", Assert.Single(list!).Code);
    }

    [DatabaseFact]
    public async Task Duplicates_unknown_payors_and_wrong_prefixes_are_refused()
    {
        var c = Client();
        await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "WMTEST0002*01", "x", "WM"));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "WMTEST0002*01", "again", "WM"))).StatusCode);
        Assert.Equal(1, await Count("MembershipNo = 'WMTEST0002*01'"));

        var before = factory.Audit.Entries.Count;
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "QQTEST1*01", "x", "QQ"))).StatusCode);
        var wrong = await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "AITEST1*01", "x", "WM"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("code", JsonDocument.Parse(await wrong.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync(Url, new SuspensionRequest("01", "WMNOSUCH*01", "x", "WM"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"{Url}?kind=01&code=WMNOSUCH*01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync($"{Url}?kind=09&code=X")).StatusCode);
        Assert.Equal(before, factory.Audit.Entries.Count);
    }

    [DatabaseFact]
    public async Task Sql_injection_text_is_stored_literally()
    {
        var c = Client();
        var evil = "x'; DELETE FROM SuspensionDetail;--";
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "WMTEST0003*01", evil, "WM"))).StatusCode);
        Assert.Equal(1, await Count("Remarks = @r", new { r = evil }));
    }

    [DatabaseFact]
    public async Task A_category_in_use_cannot_be_deleted_but_an_unused_one_can()
    {
        var c = Client();
        await c.PostAsJsonAsync(Url, new SuspensionRequest("01", "WMTEST0004*01", "x", "WM"));
        var blocked = await c.DeleteAsync("/api/lookups/suspension-category/rows?key=01");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("suspension records", await blocked.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/lookups/suspension-category/rows", new { code = "09", description = "Test category" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Url, new SuspensionRequest("09", "X1", "x", "WM"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/lookups/suspension-category/rows?key=09")).StatusCode);
    }

    [DatabaseFact]
    public async Task Category_edits_stamp_the_real_update_columns()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/lookups/suspension-category/rows", new { code = "08", description = "Stamp check" })).StatusCode);
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SuspensionCat WHERE SuspensionCode = '08' AND LastUpdateUser = 'TEST' AND LastUpdateDate = CAST(GETDATE() AS date)"));
    }
}

