using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;

namespace Genesis.Tests;

public class PlanMaintenanceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();
    private HttpClient Client(int position) => factory.ClientFor(WriteApiFactory.Access(position));
    private static Dictionary<string, string> Premium(string amount = "1000", string effective = "2023-01-01", string version = "3") => new()
        { ["insured"] = "ZZ", ["health"] = "N", ["payor"] = "ZZ", ["plan"] = "ZZP1", ["age"] = "05", ["effective"] = effective, ["amount"] = amount, ["suppAmount"] = "400", ["suppStatus"] = "C", ["version"] = version };

    [Fact]
    public void Every_plan_tab_is_catalogued_with_its_legacy_position()
    {
        var plan = LookupCatalog.All.Where(d => d.Id.StartsWith("plan-")).ToDictionary(d => d.Id);
        Assert.Equal(10, plan.Count);
        Assert.Equal((80, 81, 82, 83, 84, 85), (plan["plan-health"].ScreenPosition, plan["plan-insured"].ScreenPosition, plan["plan-code"].ScreenPosition, plan["plan-premium"].ScreenPosition, plan["plan-annual-limit"].ScreenPosition, plan["plan-mco-fee"].ScreenPosition));
        Assert.False(plan["plan-code"].AllowDelete);
        Assert.Contains(plan["plan-code"].Fields, f => f.Key == "specialNote" && f.Multiline);
    }

    [DatabaseFact]
    public async Task Premium_rows_accept_dates_money_and_integers_stamp_the_user_and_can_be_edited_and_deleted()
    {
        var client = Client(83); var url = "/api/lookups/plan-premium/rows";
        var created = await client.PostAsJsonAsync(url, Premium());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal(("2023-01-01", "1000.00", "3", "C"), (row.Values["effective"], row.Values["amount"], row.Values["version"], row.Values["suppStatus"]));
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PRM WHERE PRMCode = @k AND PRMLastUpdateUser = 'TEST' AND PRMEffDate = '2023-01-01' AND PRMAmount = 1000 AND PRMVersion = 3", new { k = int.Parse(row.Key) }));

        var edit = await client.PutAsJsonAsync($"{url}?key={row.Key}", Premium("1200.5", "2024-02-29", "4"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(("2024-02-29", "1200.50"), ((await edit.Content.ReadFromJsonAsync<LookupRow>())!.Values["effective"], "1200.50"));
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={row.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{url}?key={row.Key}")).StatusCode);
    }

    [DatabaseFact]
    public async Task Case_management_fee_has_a_three_column_key_that_cannot_change_and_cannot_be_duplicated()
    {
        var client = Client(85); var url = "/api/lookups/plan-case-mgmt-fee/rows";
        var body = new Dictionary<string, string> { ["health"] = "ZZ", ["insured"] = "ZY", ["plan"] = "ZZP9", ["fee"] = "12.5" };
        var created = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal("ZZ|ZY|ZZP9", row.Key);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, body)).StatusCode);
        body["plan"] = "ZZP8";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, body)).StatusCode);

        var change = new Dictionary<string, string> { ["fee"] = "20" };
        var edit = await client.PutAsJsonAsync($"{url}?key={Uri.EscapeDataString(row.Key)}", change);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("20.00", (await edit.Content.ReadFromJsonAsync<LookupRow>())!.Values["fee"]);
        change["plan"] = "OTHR";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}?key={Uri.EscapeDataString(row.Key)}", change)).StatusCode);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance))
        {
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtHLTCode = 'ZZ' AND MgmtINSCode = 'ZY' AND MgmtPLNCode = 'ZZP9' AND MgmtFee = 20 AND MgmtLastUpdateUser = 'TEST'"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtPLNCode = 'ZZP8' AND MgmtFee = 12.5"));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{url}?key=ZZ")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={Uri.EscapeDataString(row.Key)}")).StatusCode);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtHLTCode = 'ZZ'"));
    }

    [DatabaseFact]
    public async Task Invalid_dates_integers_and_amounts_are_refused()
    {
        var client = Client(83); var url = "/api/lookups/plan-premium/rows";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(effective: "31/12/2023"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(effective: "2023-02-30"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(version: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(amount: "-5"))).StatusCode);
        var noDate = Premium(); noDate["effective"] = "";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, noDate)).StatusCode);
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PRM WHERE PLNCode = 'ZZP1'"));
    }

    [DatabaseFact]
    public async Task The_plan_tab_cannot_delete_plans_and_other_positions_are_refused()
    {
        var plan = (await Client(82).GetFromJsonAsync<List<LookupRow>>("/api/lookups/plan-code/rows"))!;
        Assert.NotEmpty(plan);
        var del = await Client(82).DeleteAsync($"/api/lookups/plan-code/rows?key={plan[0].Key}");
        Assert.NotEqual(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync("/api/lookups/plan-premium/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(84).GetAsync("/api/lookups/plan-premium/rows")).StatusCode);
    }

    [DatabaseFact]
    public async Task Plan_code_edits_keep_the_special_note_and_validate_the_choices()
    {
        var rows = (await Client(82).GetFromJsonAsync<List<LookupRow>>("/api/lookups/plan-code/rows"))!;
        var values = rows[0].Values.ToDictionary(k => k.Key, k => k.Value ?? "");
        values["specialNote"] = "Rolled back note"; values["proRate"] = "Y";
        var ok = await Client(82).PutAsJsonAsync($"/api/lookups/plan-code/rows?key={rows[0].Key}", values);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Rolled back note", (await ok.Content.ReadFromJsonAsync<LookupRow>())!.Values["specialNote"]);
        values["proRate"] = "Q";
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PutAsJsonAsync($"/api/lookups/plan-code/rows?key={rows[0].Key}", values)).StatusCode);
    }
}

