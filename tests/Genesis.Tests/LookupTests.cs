using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Microsoft.Data.SqlClient;

namespace Genesis.Tests;

public class LookupCatalogTests
{
    [Fact]
    public void Definitions_are_consistent()
    {
        var id = new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");
        Assert.Equal(LookupCatalog.All.Count, LookupCatalog.All.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var d in LookupCatalog.All)
        {
            Assert.Matches(id, d.Table);
            Assert.Matches(id, d.OrderBy);
            Assert.All(d.Fields, f => { Assert.Matches(id, f.Column); Assert.True(f.MaxLength > 0); });
            Assert.Single(d.Fields, f => f.IsKey);
            Assert.All(d.References, r => { Assert.Matches(id, r.Table); Assert.Matches(id, r.Column); });
            if (d.UpdatedByColumn is not null) Assert.Matches(id, d.UpdatedByColumn);
            if (d.UpdatedDateColumn is not null) Assert.Matches(id, d.UpdatedDateColumn);
        }
    }

    [DatabaseFact]
    public void Every_definition_matches_the_real_schema_and_column_sizes()
    {
        var config = TestHost.Config();
        foreach (var d in LookupCatalog.All)
        {
            using var conn = new SqlConnection(config[$"ConnectionStrings:{d.Database}"]);
            conn.Open();
            var cols = d.Fields.Select(f => f.Column).Concat([d.UpdatedByColumn, d.UpdatedDateColumn]).Where(c => c is not null);
            var ex = Record.Exception(() => conn.Execute($"SELECT TOP 0 {string.Join(", ", cols)} FROM {d.Table} ORDER BY {d.OrderBy}"));
            Assert.True(ex is null, $"{d.Id}: {ex?.Message}");
            foreach (var f in d.Fields)
            {
                // The catalog's MaxLength must not exceed what the column can really hold.
                var size = conn.ExecuteScalar<int>("SELECT COLUMNPROPERTY(OBJECT_ID(@t), @c, 'PRECISION')", new { t = d.Table, c = f.Column });
                Assert.True(f.MaxLength <= size, $"{d.Id}.{f.Column}: catalog {f.MaxLength} > column {size}");
            }
            foreach (var r in d.References)
            {
                using var rc = new SqlConnection(config[$"ConnectionStrings:{r.Database}"]);
                rc.Open();
                var rex = Record.Exception(() => rc.Execute($"SELECT TOP 0 {r.Column} FROM {r.Table}"));
                Assert.True(rex is null, $"{d.Id} reference {r.Table}.{r.Column}: {rex?.Message}");
            }
        }
    }
}

public class DepartmentTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/lookups/department";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(111));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<int> Count(string where, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM GRP WHERE {where}", args);
    }

    [Fact]
    public async Task Requires_authentication_and_the_department_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url)).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/rows", new { code = "ZZT", description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{Url}/rows?key=ZZT")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync("/api/lookups/nope")).StatusCode);
    }

    [Fact]
    public async Task Menu_lists_only_permitted_code_tables()
    {
        Assert.Equal(["department"], (await Client().GetFromJsonAsync<List<Genesis.Core.Tracking.TrackingDefinitionSummary>>("/api/lookups"))!.Select(s => s.Id));
        Assert.Empty((await factory.ClientFor(WriteApiFactory.Access(3)).GetFromJsonAsync<List<Genesis.Core.Tracking.TrackingDefinitionSummary>>("/api/lookups"))!);
    }

    [DatabaseFact]
    public async Task Lists_and_searches_departments_with_literal_wildcards()
    {
        var c = Client();
        var all = await c.GetFromJsonAsync<List<LookupRow>>($"{Url}/rows");
        Assert.True(all!.Count >= 10);
        Assert.Contains(all, r => r.Key == "ACC" && r.Values["description"] == "ACCOUNTS");
        Assert.Single(await c.GetFromJsonAsync<List<LookupRow>>($"{Url}/rows?q=ACCOUNT") ?? []);
        Assert.Empty((await c.GetFromJsonAsync<List<LookupRow>>($"{Url}/rows?q=%25"))!);
    }

    [DatabaseFact]
    public async Task Create_update_and_delete_a_department_with_audit()
    {
        var c = Client();
        var created = await c.PostAsJsonAsync($"{Url}/rows", new { code = "ZZT", description = "  Test department  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Test department", (await created.Content.ReadFromJsonAsync<LookupRow>())!.Values["description"]);
        Assert.Equal(1, await Count("GRPCode = 'ZZT' AND GRPDescription = 'Test department'"));

        var updated = await c.PutAsJsonAsync($"{Url}/rows?key=ZZT", new { description = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1, await Count("GRPCode = 'ZZT' AND GRPDescription = 'Renamed'"));

        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"{Url}/rows?key=ZZT")).StatusCode);
        Assert.Equal(0, await Count("GRPCode = 'ZZT'"));

        Assert.Equal(["Lookup.Create", "Lookup.Update", "Lookup.Delete"], factory.Audit.Entries.Select(e => e.Action));
        Assert.All(factory.Audit.Entries, e => { Assert.Equal("TEST", e.User); Assert.Equal("ZZT", e.Key); });
    }

    [DatabaseFact]
    public async Task Duplicate_and_missing_records_are_reported_not_overwritten()
    {
        var c = Client();
        var dup = await c.PostAsJsonAsync($"{Url}/rows", new { code = "ACC", description = "Overwrite attempt" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("duplicated", await dup.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count("GRPCode = 'ACC' AND GRPDescription = 'ACCOUNTS'"));

        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"{Url}/rows?key=NOPE", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"{Url}/rows?key=NOPE")).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task A_department_used_by_users_cannot_be_deleted()
    {
        var res = await Client().DeleteAsync($"{Url}/rows?key=ACC");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("used by", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count("GRPCode = 'ACC'"));
        Assert.Empty(factory.Audit.Entries);
    }

    [Theory]
    [InlineData("", "Desc", "code")]
    [InlineData("  ", "Desc", "code")]
    [InlineData("ABCDE", "Desc", "code")]
    [InlineData("ZZ\tT", "Desc", "code")]
    [InlineData("ZZT", "", "description")]
    public async Task Validates_required_fields_lengths_and_characters(string code, string description, string field)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/rows", new { code, description });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(field, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Rejects_unknown_fields_and_key_changes_and_long_values()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Url}/rows", new { code = "ZZT", description = "x", bogus = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Url}/rows", new { code = "ZZT", description = new string('x', 256) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"{Url}/rows?key=ACC", new { code = "OTHER", description = "x" })).StatusCode);
    }

    [DatabaseFact]
    public async Task Sql_injection_text_is_stored_literally()
    {
        var c = Client();
        var evil = "x'; DELETE FROM GRP;--";
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync($"{Url}/rows", new { code = "ZZT", description = evil })).StatusCode);
        Assert.Equal(1, await Count("GRPDescription = @d", new { d = evil }));
        Assert.True(await Count("1 = 1") >= 11);
    }
}

public class ValidDateRangeTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/maintenance/valid-dates";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(109));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static ValidDateRanges Sample() => new(new(2020, 1, 1), new(2030, 12, 31), new(2021, 1, 1), new(2031, 12, 31), new(1930, 1, 1), new(2025, 12, 31));

    [Fact]
    public void Rules_require_all_dates_in_range_and_ordered()
    {
        Assert.Null(ValidDateRangeRules.Validate(Sample()));
        Assert.Equal("EffectiveStart", ValidDateRangeRules.Validate(Sample() with { EffectiveStart = null })!.Value.Field);
        Assert.Equal("BirthEnd", ValidDateRangeRules.Validate(Sample() with { BirthEnd = null })!.Value.Field);
        Assert.Equal("ExpiryEnd", ValidDateRangeRules.Validate(Sample() with { ExpiryEnd = new(2020, 12, 31) })!.Value.Field);
        Assert.Equal("EffectiveStart", ValidDateRangeRules.Validate(Sample() with { EffectiveStart = new(1800, 1, 1) })!.Value.Field);
        Assert.Equal("BirthEnd", ValidDateRangeRules.Validate(Sample() with { BirthEnd = new(2200, 1, 1) })!.Value.Field);
    }

    [Fact]
    public async Task Requires_authentication_and_the_date_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url)).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url, Sample())).StatusCode);
    }

    [DatabaseFact]
    public async Task Reads_the_single_row()
    {
        var r = await Client().GetFromJsonAsync<ValidDateRanges>(Url);
        Assert.Equal(new DateOnly(1999, 1, 1), r!.EffectiveStart);
        Assert.Equal(new DateOnly(2011, 12, 31), r.ExpiryEnd);
    }

    [DatabaseFact]
    public async Task Saving_updates_the_row_stamps_user_and_audits_and_never_adds_a_second_row()
    {
        var c = Client();
        var res = await c.PutAsJsonAsync(Url, Sample());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var saved = await res.Content.ReadFromJsonAsync<ValidDateRanges>();
        Assert.Equal(Sample().EffectiveEnd, saved!.EffectiveEnd);
        Assert.Equal("TEST", saved.UpdatedBy);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), saved.UpdatedOn);
        Assert.Equal(Sample().BirthStart, (await c.GetFromJsonAsync<ValidDateRanges>(Url))!.BirthStart);

        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MBMValidDateRange"));
        Assert.Equal("ValidDates.Save", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Invalid_dates_are_rejected_and_change_nothing()
    {
        var c = Client();
        var before = await c.GetFromJsonAsync<ValidDateRanges>(Url);
        var res = await c.PutAsJsonAsync(Url, Sample() with { ExpiryEnd = new(2020, 1, 1) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("ExpiryEnd", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, await c.GetFromJsonAsync<ValidDateRanges>(Url));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync(Url, new { effectiveStart = "2020-01-01" })).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }
}

