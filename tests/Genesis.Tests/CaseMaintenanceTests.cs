using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;

namespace Genesis.Tests;

public class CaseMaintenanceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Icd = "/api/maintenance/icd-selection";
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();
    private HttpClient Client(int position) => factory.ClientFor(WriteApiFactory.Access(position));

    public static IEnumerable<object[]> Tabs() => LookupCatalog.All.Where(d => d.Id.StartsWith("case-")).Select(d => new object[] { d.Id });

    [Fact]
    public void The_seven_code_tabs_use_their_legacy_positions()
    {
        var d = LookupCatalog.All.Where(x => x.Id.StartsWith("case-")).ToDictionary(x => x.Id);
        Assert.Equal(7, d.Count);
        Assert.Equal((91, 92, 93, 94, 95, 96, 97), (d["case-icd"].ScreenPosition, d["case-icd-category"].ScreenPosition, d["case-repudiation-code"].ScreenPosition, d["case-repudiation-category"].ScreenPosition,
            d["case-claim-nature"].ScreenPosition, d["case-treatment-modality"].ScreenPosition, d["case-hospitalisation-type"].ScreenPosition));
    }

    [DatabaseTheory]
    [MemberData(nameof(Tabs))]
    public async Task Each_tab_supports_add_edit_and_delete_with_stamps(string id)
    {
        var def = LookupCatalog.Find(id)!; var client = Client(def.ScreenPosition); var url = $"/api/lookups/{id}/rows";
        var code = new string('Z', def.KeyField.MaxLength);
        var other = def.Fields.Where(f => !f.IsKey && f.Required).ToDictionary(f => f.Key, f => "Test one");
        Dictionary<string, string> Body(string c, string text) { var b = other.ToDictionary(k => k.Key, k => text); b["code"] = c; return b; }
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, Body(code, "Test one"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, Body(code, "Again"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Body(code + "X", "Too long"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}?key={code}", Body(code, "Test two"))).StatusCode);
        await using (var conn = await factory.Db.OpenAsync(def.Database))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {def.Table} WHERE {def.KeyField.Column} = @code AND {def.UpdatedByColumn} = 'TEST'", new { code }));
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync(url)).StatusCode);
    }

    [DatabaseFact]
    public async Task A_category_or_nature_in_use_cannot_be_deleted()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("INSERT INTO ICDCategory (ICDCATCode, ICDCATDescription) VALUES ('ZZC', 'cat')");
            await main.ExecuteAsync("INSERT INTO ICD (ICDCode, ICDCatCode, ICDDescription) VALUES ('ZZZ1', 'ZZC', 'x')");
            await main.ExecuteAsync("INSERT INTO ClaimNature (ClaimNatureCode, ClaimNatureName) VALUES ('ZZN', 'nat')");
            await main.ExecuteAsync("INSERT INTO CAS (CASNumber, CASClaimAdmin, ClaimNatureCode) VALUES ('ZZCASEN01', 'N', 'ZZN')");
        }
        var cat = await Client(92).DeleteAsync("/api/lookups/case-icd-category/rows?key=ZZC");
        Assert.Equal(HttpStatusCode.Conflict, cat.StatusCode); Assert.Contains("ICD codes", await cat.Content.ReadAsStringAsync());
        var nat = await Client(95).DeleteAsync("/api/lookups/case-claim-nature/rows?key=ZZN");
        Assert.Equal(HttpStatusCode.Conflict, nat.StatusCode); Assert.Contains("cases", await nat.Content.ReadAsStringAsync());
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [DatabaseFact]
    public async Task Icd_selection_applies_and_unapplies_codes_in_icdoriginal()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO ICD (ICDCode, ICDCatCode, ICDDescription) VALUES ('ZZI1', NULL, 'Test disease one'), ('ZZI2', NULL, @longText)", new { longText = new string('d', 300) });
        var c = Client(90);
        var view = (await c.GetFromJsonAsync<IcdSelectionView>($"{Icd}?search=ZZI"))!;
        Assert.Equal(["ZZI1", "ZZI2"], view.Available.Select(r => r.Code).Where(c => c.StartsWith("ZZI"))); Assert.DoesNotContain(view.Applied, r => r.Code.StartsWith("ZZI"));

        var sel = await c.PostAsJsonAsync($"{Icd}/select", new IcdSelectionRequest(["ZZI1", "ZZI2", "ZZI1"]));
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        Assert.Equal(2, (await sel.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("changed").GetInt32());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM ICDOriginal WHERE ICDCode = 'ZZI1' AND ICDLastUpdateUser = 'TEST'"));
        Assert.Equal(255, await Scalar<int>("SELECT LEN(ICDDescription) FROM ICDOriginal WHERE ICDCode = 'ZZI2'"));
        view = (await c.GetFromJsonAsync<IcdSelectionView>($"{Icd}?search=ZZI"))!;
        Assert.DoesNotContain(view.Available, r => r.Code.StartsWith("ZZI")); Assert.Equal(2, view.Applied.Count(r => r.Code.StartsWith("ZZI")));
        var again = await c.PostAsJsonAsync($"{Icd}/select", new IcdSelectionRequest(["ZZI1"]));
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("changed").GetInt32());

        var un = await c.PostAsJsonAsync($"{Icd}/unselect", new IcdSelectionRequest(["ZZI1"]));
        Assert.Equal(1, (await un.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("changed").GetInt32());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM ICD WHERE ICDCode = 'ZZI1'"));
        Assert.Equal(["Icd.Select", "Icd.Unselect"], factory.Audit.Entries.Select(e => e.Action));
    }

    [DatabaseFact]
    public async Task Icd_selection_refuses_bad_input_and_other_positions()
    {
        var c = Client(90);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Icd}/select", new IcdSelectionRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Icd}/select", new IcdSelectionRequest(["TOOLONGCODE"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"{Icd}/select", new IcdSelectionRequest(["NOPE1"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(91).GetAsync(Icd)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Icd)).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }
}


