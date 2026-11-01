using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Maintenance;

namespace GenesisFIS.Tests;

/// <summary>Maintenance > Entity > Group Company (the employers / clients), legacy frmMaintenanceGeneral SaveCompanyGrp.</summary>
public class EmployerTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/lookups/entity-group-company";
    private HttpClient Client(int position = 76) => factory.ClientFor(WriteApiFactory.Access(position));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static Dictionary<string, string> Employer(string name = "ZETA TEST HOLDINGS SDN BHD") => new()
    {
        ["payor"] = "ZZ", ["name"] = name, ["status"] = "B", ["effective"] = "2024-01-01", ["address1"] = "1 JALAN UJIAN", ["tax"] = "N", ["conversion"] = "N", ["prePost"] = "Y",
    };

    private async Task SeedAsync(string? counter)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        await conn.ExecuteAsync("INSERT INTO PAY (PAYCode, PAYCompanyName) VALUES ('ZZ', 'TEST PAYOR'); DELETE FROM CoGrpSeq WHERE CoGrpCode = 'Z';");
        if (counter is not null) await conn.ExecuteAsync("INSERT INTO CoGrpSeq (CoGrpCode, CoGrpSeq) VALUES ('Z', @counter)", new { counter });
    }

    [Fact]
    public void Is_catalogued_under_the_legacy_group_company_permission()
    {
        var d = LookupCatalog.Find("entity-group-company")!;
        Assert.Equal((76, "CompanyGRP", false), (d.ScreenPosition, d.Table, d.AllowDelete));
        Assert.Equal(["code", "payor"], d.KeyFields.Select(f => f.Key));
        Assert.Equal(("code", "name"), (d.CodeSequence!.CodeField, d.CodeSequence.SourceField));
    }

    [DatabaseFact]
    public async Task New_employers_get_a_code_from_the_first_letter_of_their_name()
    {
        await SeedAsync("041");
        var res = await Client().PostAsJsonAsync($"{Url}/rows", Employer());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var row = (await res.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal(("Z042|ZZ", "Z042", "B", "Y"), (row.Key, row.Values["code"], row.Values["status"], row.Values["prePost"]));

        // A letter with no counter yet starts at 001.
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            await conn.ExecuteAsync("DELETE FROM CoGrpSeq WHERE CoGrpCode = 'Z'");
        row = (await (await Client().PostAsJsonAsync($"{Url}/rows", Employer("ZETA SECOND TEST BHD"))).Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal("Z001", row.Values["code"]);

        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            Assert.Equal("001", (await conn.ExecuteScalarAsync<string>("SELECT CoGrpSeq FROM CoGrpSeq WHERE CoGrpCode = 'Z'"))!.Trim());
            Assert.Equal("TEST", (await conn.ExecuteScalarAsync<string>("SELECT CoGRPLastUpdateUser FROM CompanyGRP WHERE CoGRPCode = 'Z042'"))!.Trim());
        }
    }

    [DatabaseFact]
    public async Task Typed_codes_duplicate_names_and_missing_fields_are_refused()
    {
        await SeedAsync(null);
        var typed = Employer(); typed["code"] = "MINE";
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/rows", typed)).StatusCode);
        var noPayor = Employer(); noPayor.Remove("payor");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/rows", noPayor)).StatusCode);
        var badStatus = Employer(); badStatus["status"] = "X";
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/rows", badStatus)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/rows", Employer("- NO LETTER"))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync($"{Url}/rows", Employer())).StatusCode);
        var dup = await Client().PostAsJsonAsync($"{Url}/rows", Employer());
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("duplicated", await dup.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(82).PostAsJsonAsync($"{Url}/rows", Employer("ZETA OTHER BHD"))).StatusCode);
    }

    [DatabaseFact]
    public async Task An_employer_can_be_edited_but_not_deleted()
    {
        await SeedAsync(null);
        var key = (await (await Client().PostAsJsonAsync($"{Url}/rows", Employer())).Content.ReadFromJsonAsync<LookupRow>())!.Key;
        var edit = Employer(); edit["address2"] = "TAMAN UJIAN"; edit["status"] = "I";
        var res = await Client().PutAsJsonAsync($"{Url}/rows?key={key}", edit);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("TAMAN UJIAN", "I"), ((await res.Content.ReadFromJsonAsync<LookupRow>())!.Values["address2"], (await Client().GetFromJsonAsync<List<LookupRow>>($"{Url}/rows?q=ZETA TEST"))!.Single().Values["status"]));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().DeleteAsync($"{Url}/rows?key={key}")).StatusCode);
    }
}
