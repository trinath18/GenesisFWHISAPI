using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;

namespace Genesis.Tests;

public class MemberCodeTableTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    public static IEnumerable<object[]> Tables() =>
        LookupCatalog.All.Where(d => d.Id.StartsWith("member-") || d.Id.StartsWith("benefit-")).Select(d => new object[] { d.Id });

    [Fact]
    public void All_thirteen_legacy_tabs_except_age_band_are_catalogued_with_their_legacy_positions()
    {
        var defs = LookupCatalog.All.Where(d => d.Id.StartsWith("member-")).ToDictionary(d => d.Id);
        Assert.Equal(12, defs.Count);
        Assert.Equal(99, defs["member-occupation"].ScreenPosition);
        Assert.Equal(108, defs["member-policy-status"].ScreenPosition);
        Assert.All(defs.Values, d => Assert.Equal("Maintenance > Member", d.Module));
    }

    [DatabaseTheory]
    [MemberData(nameof(Tables))]
    public async Task Each_table_supports_add_edit_and_delete_with_stamps_and_validation(string id)
    {
        var def = LookupCatalog.Find(id)!;
        var client = factory.ClientFor(WriteApiFactory.Access(def.ScreenPosition));
        var key = def.KeyField;
        var code = new string('Z', key.MaxLength);
        var url = $"/api/lookups/{id}/rows";

        // Any other required field (e.g. the category of a sub category) gets a filler value.
        Dictionary<string, string> Body(string c, string d) => def.Fields.Where(f => f.Required && !f.IsKey && f.Key != "description").ToDictionary(f => f.Key, f => new string('C', Math.Min(f.MaxLength, 3))) is var extra
            ? new Dictionary<string, string>(extra) { ["code"] = c, ["description"] = d } : new();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, Body(code, "Test one"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, Body(code, "Duplicate"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Body(code + "X", "Too long"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Body("Y", ""))).StatusCode);

        var edit = await client.PutAsJsonAsync($"{url}?key={code}", Body(code, "Test two"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("Test two", (await edit.Content.ReadFromJsonAsync<LookupRow>())!.Values["description"]);

        await using (var conn = await factory.Db.OpenAsync(def.Database))
        {
            var stamped = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {def.Table} WHERE {key.Column} = @code AND {def.UpdatedByColumn} = 'TEST' AND CAST({def.UpdatedDateColumn} AS date) = CAST(GETDATE() AS date)", new { code });
            Assert.Equal(1, stamped);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{url}?key={code}")).StatusCode);
    }

    [DatabaseTheory]
    [InlineData("benefit-category", "SELECT TOP 1 BNFCatCode FROM BNFSubCat WHERE BNFCatCode IS NOT NULL", "sub categories")]
    [InlineData("benefit-subcategory", "SELECT TOP 1 BNFCode FROM BNF WHERE BNFCode IN (SELECT BNFSubCode FROM BNFSubCat)", "plan benefits")]
    [InlineData("member-race", "SELECT TOP 1 RACCode FROM MBM WHERE RACCode IS NOT NULL AND LTRIM(RTRIM(RACCode)) <> '' AND LEN(LTRIM(RTRIM(RACCode))) <= 2", "members")]
    [InlineData("member-state", "SELECT TOP 1 STACode FROM MBM WHERE STACode IS NOT NULL AND LTRIM(RTRIM(STACode)) <> '' AND LEN(LTRIM(RTRIM(STACode))) <= 3", "members")]
    [InlineData("member-policy-status", "SELECT TOP 1 MBMStatus FROM MBM", "members")]
    public async Task A_code_that_members_use_cannot_be_deleted(string id, string findUsedCode, string word)
    {
        string? used;
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main)) used = (await conn.ExecuteScalarAsync<string?>(findUsedCode))?.Trim();
        if (used is null) return;
        var def = LookupCatalog.Find(id)!;
        var res = await factory.ClientFor(WriteApiFactory.Access(def.ScreenPosition)).DeleteAsync($"/api/lookups/{id}/rows?key={used}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains(word, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Other_positions_are_refused()
    {
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/lookups/member-occupation/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/lookups/member-occupation/rows", new Dictionary<string, string> { ["code"] = "ZZ", ["description"] = "x" })).StatusCode);
    }
}


