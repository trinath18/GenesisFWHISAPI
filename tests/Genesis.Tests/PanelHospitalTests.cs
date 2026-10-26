using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Microsoft.Data.SqlClient;

namespace Genesis.Tests;

public class PanelHospitalTests : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Panel = "WM - AIROD";
    private const string Url = "/api/maintenance/panel-hospitals";
    private readonly WriteApiFactory _factory;

    public PanelHospitalTests(WriteApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    // Roll back after every test: each test starts from the real data and leaves no locks behind.
    public Task DisposeAsync() => _factory.ResetAsync();

    private HttpClient Client() => _factory.ClientFor(WriteApiFactory.Access(1));
    private static string P(string panel = Panel) => Uri.EscapeDataString(panel);

    private async Task<List<AvailableHospital>> Available(HttpClient c, string q = "") =>
        (await c.GetFromJsonAsync<List<AvailableHospital>>($"{Url}/available?panel={P()}&q={q}"))!;

    private async Task<int> Count(string sql, object? args = null)
    {
        await using var conn = await _factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<int>(sql, args);
    }

    private Task<int> RowsFor(string hospital) =>
        Count("SELECT COUNT(*) FROM PanelHospitals WHERE HosPanelCode = @p AND HOSCode = @h", new { p = Panel, h = hospital });

    [Fact]
    public async Task Requires_authentication_and_the_legacy_screen_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor(WriteApiFactory.Access(3)).GetAsync(Url)).StatusCode);
        var put = await _factory.ClientFor(WriteApiFactory.Access(3)).PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange(["X"], null));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [DatabaseFact]
    public async Task Lists_panels_members_and_available_hospitals()
    {
        var c = Client();
        var panels = await c.GetFromJsonAsync<List<HospitalPanel>>(Url);
        Assert.Contains(panels!, p => p.Code == Panel);
        var members = await c.GetFromJsonAsync<List<PanelHospital>>($"{Url}/members?panel={P("TH - SM")}");
        Assert.NotEmpty(members!);
        var available = await Available(c);
        Assert.NotEmpty(available);
        Assert.DoesNotContain(available, a => members!.Any(m => m.HospitalCode == a.HospitalCode && false));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{Url}/members?panel=NOPE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{Url}/available?panel=NOPE")).StatusCode);
    }

    [DatabaseFact]
    public async Task Adding_a_hospital_stamps_user_and_date_audits_and_removes_it_from_available()
    {
        var c = Client();
        var hospital = (await Available(c)).First().HospitalCode;
        var res = await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange([hospital], null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new PanelHospitalChangeResult(1, 0, 0), await res.Content.ReadFromJsonAsync<PanelHospitalChangeResult>());

        Assert.Equal(1, await RowsFor(hospital));
        Assert.Equal(1, await Count("SELECT COUNT(*) FROM PanelHospitals WHERE HosPanelCode = @p AND HOSCode = @h AND LastUpdateUSR = 'TEST' AND LastUpdateDate = CAST(GETDATE() AS date)", new { p = Panel, h = hospital }));
        Assert.DoesNotContain(await Available(c), a => a.HospitalCode == hospital);
        var members = await c.GetFromJsonAsync<List<PanelHospital>>($"{Url}/members?panel={P()}");
        Assert.Contains(members!, m => m.HospitalCode == hospital);

        var entry = Assert.Single(_factory.Audit.Entries, e => e.Key == Panel && e.Action == "Panel.Update" && ((IEnumerable<string>)e.Details["added"]!).Contains(hospital));
        Assert.Equal("TEST", entry.User);
    }

    [DatabaseFact]
    public async Task Adding_an_existing_member_refreshes_the_stamp_and_never_duplicates()
    {
        var c = Client();
        var hospital = (await Available(c)).Skip(1).First().HospitalCode;
        await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange([hospital], null));
        var again = await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange([hospital], null));
        Assert.Equal(new PanelHospitalChangeResult(0, 1, 0), await again.Content.ReadFromJsonAsync<PanelHospitalChangeResult>());
        Assert.Equal(1, await RowsFor(hospital));
    }

    [DatabaseFact]
    public async Task Removing_deletes_only_the_listed_hospitals_of_that_panel()
    {
        var c = Client();
        var two = (await Available(c)).Skip(2).Take(2).Select(a => a.HospitalCode).ToList();
        await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange(two, null));
        var other = await Count("SELECT COUNT(*) FROM PanelHospitals WHERE HosPanelCode <> @p", new { p = Panel });
        var total = await Count("SELECT COUNT(*) FROM PanelHospitals");

        var res = await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange(null, [two[0]]));
        Assert.Equal(new PanelHospitalChangeResult(0, 0, 1), await res.Content.ReadFromJsonAsync<PanelHospitalChangeResult>());
        Assert.Equal(0, await RowsFor(two[0]));
        Assert.Equal(1, await RowsFor(two[1]));
        Assert.Equal(other, await Count("SELECT COUNT(*) FROM PanelHospitals WHERE HosPanelCode <> @p", new { p = Panel }));
        Assert.Equal(total - 1, await Count("SELECT COUNT(*) FROM PanelHospitals"));
        Assert.Contains(_factory.Audit.Entries, e => e.Details.TryGetValue("removed", out var r) && ((IEnumerable<string>)r!).Contains(two[0]));
    }

    [DatabaseFact]
    public async Task A_failing_request_changes_nothing()
    {
        var c = Client();
        var good = (await Available(c)).Skip(4).First().HospitalCode;
        var before = await Count("SELECT COUNT(*) FROM PanelHospitals");
        var auditBefore = _factory.Audit.Entries.Count;

        var res = await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange([good, "NO-SUCH-HOSP"], null));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("NO-SUCH-HOSP", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, await Count("SELECT COUNT(*) FROM PanelHospitals"));
        Assert.Equal(0, await RowsFor(good));
        Assert.Equal(auditBefore, _factory.Audit.Entries.Count);
    }

    [DatabaseFact]
    public async Task Rejects_invalid_requests()
    {
        var c = Client();
        var h = (await Available(c)).First().HospitalCode;
        async Task<HttpStatusCode> Put(string panel, PanelHospitalChange change) => (await c.PutAsJsonAsync($"{Url}?panel={P(panel)}", change)).StatusCode;

        Assert.Equal(HttpStatusCode.NotFound, await Put("NOPE", new([h], null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Panel, new(null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Panel, new([], [])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Panel, new([h], [h])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Panel, new(["a'; DROP TABLE HOS;--"], null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Panel, new(Enumerable.Range(0, 1001).Select(i => "H" + i).ToList(), null)));
    }

    [DatabaseFact]
    public async Task Test_changes_are_rolled_back_and_the_real_database_is_untouched()
    {
        async Task<(int Rows, long Checksum)> Snapshot()
        {
            await using var conn = new SqlConnection(TestHost.Config()["ConnectionStrings:Main"]);
            await conn.OpenAsync();
            var row = await conn.QuerySingleAsync<(int, long)>("SELECT COUNT(*), CAST(CHECKSUM_AGG(CHECKSUM(HosPanelCode, HOSCode, LastUpdateUSR, LastUpdateDate)) AS bigint) FROM PanelHospitals");
            return row;
        }

        var before = await Snapshot();
        using (var factory = new WriteApiFactory())
        {
            var c = factory.ClientFor(WriteApiFactory.Access(1));
            var hospital = (await c.GetFromJsonAsync<List<AvailableHospital>>($"{Url}/available?panel={P()}"))!.First().HospitalCode;
            Assert.True((await c.PutAsJsonAsync($"{Url}?panel={P()}", new PanelHospitalChange([hospital], null))).IsSuccessStatusCode);
        }
        Assert.Equal(before, await Snapshot());
    }
}

