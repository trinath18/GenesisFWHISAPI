using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class AdjustmentScreenTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate)
            VALUES ('ZZNBRA*01', 'A', 'N', 'NEIGHBOUR A', 'ICA', 'POL1', 'N', 'ZZ', 'G', 'ZZP1', '2023-01-01', '2023-12-31'), ('ZZNBRB*01', 'A', 'N', 'NEIGHBOUR B', 'ICB', 'POL1', 'N', 'ZZ', 'G', 'ZZP1', '2023-01-01', '2023-12-31')
            """);
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES ('ZZNBRA*01'), ('ZZNBRB*01')");
        await main.ExecuteAsync("INSERT INTO MBMAmendHistory (MBMNumber, MBMAmendEdtType, MBMAmendUser, MBMAmendDate, MBMAEndtEffDate) VALUES ('ZZNBRA*01', 'A', 'TEST', '2023-03-01', '2023-03-01')");
    }

    [Fact]
    public async Task The_new_tab_endpoints_need_the_adjustment_permission()
    {
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        foreach (var path in new[] { "neighbour?memberNumber=A&direction=next", "overview?memberNumber=A", "amendments?memberNumber=A", "covered-persons?memberNumber=A", "member-history?memberNumber=A", "fees?memberNumber=A" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/{path}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"{Url}/mgmt-note", new MgmtNoteRequest("ZZNBRA*01", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/neighbour?memberNumber=A&direction=next")).StatusCode);
    }

    [DatabaseFact]
    public async Task Pre_and_next_step_to_the_neighbouring_membership_number_and_stop_at_the_ends()
    {
        await Seed();
        var next = await Client().GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/neighbour?memberNumber=ZZNBRA*01&direction=next");
        Assert.Equal("ZZNBRB*01", next.GetProperty("memberNumber").GetString());
        var prev = await Client().GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/neighbour?memberNumber=ZZNBRB*01&direction=previous");
        Assert.Equal("ZZNBRA*01", prev.GetProperty("memberNumber").GetString());
        var last = await Client().GetAsync($"{Url}/neighbour?memberNumber=ZZZZZZZZZZ*99&direction=next");
        Assert.Equal(HttpStatusCode.NotFound, last.StatusCode);
        Assert.Contains("Last Record Reached", await last.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}/neighbour?memberNumber=a'b&direction=next")).StatusCode);
    }

    [DatabaseFact]
    public async Task Overview_and_the_list_tabs_work_under_the_adjustment_permission()
    {
        await Seed();
        var o = (await Client().GetFromJsonAsync<AdjustmentOverview>($"{Url}/overview?memberNumber=ZZNBRA*01"))!;
        Assert.Equal(("ZZNBRA*01", "A"), (o.Detail.Number, o.Detail.Status));
        Assert.Null(o.ManagementNote);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/overview?memberNumber=ZZNONE*01")).StatusCode);
        var amendments = (await Client().GetFromJsonAsync<List<MemberAmendment>>($"{Url}/amendments?memberNumber=ZZNBRA*01"))!;
        Assert.Equal("A", Assert.Single(amendments).Type);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/covered-persons?memberNumber=ZZNBRA*01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/member-history?memberNumber=ZZNBRA*01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/fees?memberNumber=ZZNBRA*01")).StatusCode);
    }

    [DatabaseFact]
    public async Task The_management_note_is_saved_cleared_audited_and_validated()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/mgmt-note", new MgmtNoteRequest("ZZNBRA*01", " VIP member "))).StatusCode);
        Assert.Equal("VIP member", (await Client().GetFromJsonAsync<AdjustmentOverview>($"{Url}/overview?memberNumber=ZZNBRA*01"))!.ManagementNote);
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/mgmt-note", new MgmtNoteRequest("ZZNBRA*01", "  "))).StatusCode);
        Assert.Null((await Client().GetFromJsonAsync<AdjustmentOverview>($"{Url}/overview?memberNumber=ZZNBRA*01"))!.ManagementNote);
        Assert.Equal(2, factory.Audit.Entries.Count(e => e.Action == "Member.MgmtNote"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/mgmt-note", new MgmtNoteRequest("ZZNBRA*01", new string('x', 8001)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync($"{Url}/mgmt-note", new MgmtNoteRequest("ZZNONE*01", "x"))).StatusCode);
    }
}
