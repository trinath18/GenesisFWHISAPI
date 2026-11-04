using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Maintenance;

namespace GenesisFIS.Tests;

/// <summary>Maintenance > Entity > Other Entity Tabs (legacy frmMaintenanceGeneral): Hospital Info, Payor Info and Payor Group.</summary>
public class EntityMaintenanceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private HttpClient Client(string id) => factory.ClientFor(WriteApiFactory.Access(LookupCatalog.Find(id)!.ScreenPosition));

    [Fact]
    public void The_tabs_keep_their_legacy_positions()
    {
        Assert.Equal((70, 72, 77), (LookupCatalog.Find("entity-hospital")!.ScreenPosition, LookupCatalog.Find("entity-payor")!.ScreenPosition, LookupCatalog.Find("entity-payor-group")!.ScreenPosition));
        Assert.All(new[] { "entity-hospital", "entity-payor", "entity-payor-group" }, id => Assert.Equal("Maintenance > Entity", LookupCatalog.Find(id)!.Module));
    }

    [DatabaseFact]
    public async Task A_hospital_is_added_edited_with_its_account_code_and_payee_and_deleted()
    {
        var c = Client("entity-hospital");
        const string url = "/api/lookups/entity-hospital/rows";
        var body = new Dictionary<string, string> { ["code"] = "ZZHOS1", ["name"] = "ZZ TEST HOSPITAL", ["group"] = "OTHER", ["address1"] = "1 JALAN ZZ" };
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, new Dictionary<string, string> { ["code"] = "ZZHOS2", ["name"] = "X", ["group"] = "NOSUCH" })).StatusCode);
        var edit = await c.PutAsJsonAsync($"{url}?key=ZZHOS1", new Dictionary<string, string>(body) { ["accCode"] = "H000099", ["payee"] = "ZZ PAYEE SDN BHD" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal(("H000099", "ZZ PAYEE SDN BHD", "TEST"), await main.QuerySingleAsync<(string, string, string)>("SELECT HOSAccCode, HOSPayee, HOSLastUpdateUser FROM HOS WHERE HOSCode = 'ZZHOS1'"));
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"{url}?key=ZZHOS1")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_hospital_or_payor_in_use_cannot_be_deleted_and_a_payor_without_a_group_can_still_be_edited()
    {
        string hos, payor;
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            hos = (await main.ExecuteScalarAsync<string>("SELECT TOP 1 CASHOSCode FROM CAS WHERE CASHOSCode IN (SELECT HOSCode FROM HOS)"))!.Trim();
            payor = (await main.ExecuteScalarAsync<string>("SELECT TOP 1 PAYCode FROM PAY WHERE PAYCode IN (SELECT PAYCode FROM MBM) AND ISNULL(PAYGRPCode, '') = ''"))!.Trim();
        }
        var deleteHos = await Client("entity-hospital").DeleteAsync($"/api/lookups/entity-hospital/rows?key={hos}");
        Assert.Equal(HttpStatusCode.Conflict, deleteHos.StatusCode);
        Assert.Contains("cases", await deleteHos.Content.ReadAsStringAsync());

        var p = Client("entity-payor");
        var row = (await p.GetFromJsonAsync<List<LookupRow>>($"/api/lookups/entity-payor/rows?q={payor}"))!.Single(r => r.Key == payor);
        var values = row.Values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!);
        values["accCode"] = "63ZZ001";
        Assert.Equal(HttpStatusCode.OK, (await p.PutAsJsonAsync($"/api/lookups/entity-payor/rows?key={payor}", values)).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal("63ZZ001", await main.ExecuteScalarAsync<string>("SELECT PAYAccCode FROM PAY WHERE PAYCode = @payor", new { payor }));
        var deletePayor = await p.DeleteAsync($"/api/lookups/entity-payor/rows?key={payor}");
        Assert.Equal(HttpStatusCode.Conflict, deletePayor.StatusCode);
        Assert.Contains("members", await deletePayor.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("entity-hospital").GetAsync("/api/lookups/entity-payor/rows")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_payor_group_used_by_a_payor_cannot_be_deleted()
    {
        var c = Client("entity-payor-group");
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/lookups/entity-payor-group/rows", new Dictionary<string, string> { ["code"] = "ZZG", ["name"] = "ZZ GROUP" })).StatusCode);
        var used = await c.DeleteAsync("/api/lookups/entity-payor-group/rows?key=MAA");
        Assert.Equal(HttpStatusCode.Conflict, used.StatusCode);
        Assert.Contains("payors", await used.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/lookups/entity-payor-group/rows?key=ZZG")).StatusCode);
    }
}
