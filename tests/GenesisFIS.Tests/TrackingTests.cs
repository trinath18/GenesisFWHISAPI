using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisFIS.Core.Tracking;
using GenesisFIS.Infrastructure.Tracking;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace GenesisFIS.Tests;

public class TrackingQueryBuilderTests
{
    private static readonly TrackingDefinition Def = TrackingCatalog.Find("lg-tracking")!;
    private static Dictionary<string, string> F(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Builds_parameterised_sql_with_only_whitelisted_columns()
    {
        var q = TrackingQueryBuilder.Build(Def, F(("caseNo", "WM00"), ("member", "ASLINA"), ("lgDate.from", "2020-01-01"), ("lgDate.to", "2020-01-31")), 100);
        Assert.Contains("CASENo LIKE @p0", q.Sql);
        Assert.Contains("MBMName LIKE @p1", q.Sql);
        Assert.Contains("LGDate >= @p2", q.Sql);
        Assert.Contains("LGDate < @p3", q.Sql);
        Assert.DoesNotContain("WM00", q.Sql);
        Assert.DoesNotContain("ASLINA", q.Sql);
        Assert.Equal("WM00%", q.Parameters["p0"]);
        Assert.Equal("%ASLINA%", q.Parameters["p1"]);
        Assert.Equal(new DateTime(2020, 2, 1), q.Parameters["p3"]);
        Assert.Equal(101, q.Parameters["take"]);
    }

    [Fact]
    public void Date_to_is_inclusive_of_the_whole_last_day() =>
        Assert.Equal(new DateTime(2021, 3, 1), TrackingQueryBuilder.Build(Def, F(("lgDate.to", "2021-02-28")), 10).Parameters["p0"]);

    [Theory]
    [InlineData("a%b", "a[%]b")]
    [InlineData("a_b", "a[_]b")]
    [InlineData("a[b", "a[[]b")]
    public void Wildcards_in_user_values_are_escaped(string input, string escaped) =>
        Assert.Equal($"%{escaped}%", TrackingQueryBuilder.Build(Def, F(("member", input)), 10).Parameters["p0"]);

    [Fact]
    public void Sql_injection_text_stays_in_parameters()
    {
        var q = TrackingQueryBuilder.Build(Def, F(("member", "x'; DROP TABLE CAS;--")), 10);
        Assert.DoesNotContain("DROP", q.Sql);
        Assert.Equal("%x'; DROP TABLE CAS;--%", q.Parameters["p0"]);
    }

    [Theory]
    [InlineData("CASENoX", "x")]
    [InlineData("1=1; --", "x")]
    [InlineData("lgDate.between", "2020-01-01")]
    [InlineData("caseNo.from", "x")]
    public void Unknown_or_malformed_filter_keys_are_rejected(string key, string value)
    {
        var ex = Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(Def, F((key, value)), 10));
        Assert.Contains(ex.Errors, e => e.Key == key);
    }

    [Fact]
    public void Invalid_dates_and_over_long_values_are_reported_together()
    {
        var ex = Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(Def,
            F(("lgDate.from", "31/12/2020"), ("lgDate.to", "nope"), ("member", new string('x', 61))), 10));
        Assert.Equal(3, ex.Errors.Count);
    }

    [Fact]
    public void Choice_filters_accept_only_listed_values()
    {
        var def = TrackingCatalog.Find("case-tracking")!;
        Assert.Contains("CASStatus = @p0", TrackingQueryBuilder.Build(def, F(("status", "O")), 10).Sql);
        Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(def, F(("status", "O' OR '1'='1")), 10));
    }

    [Fact]
    public void Filter_keys_are_case_insensitive() =>
        Assert.Contains("CASENo LIKE @p0", TrackingQueryBuilder.Build(Def, F(("CASENO", "WM")), 10).Sql);

    [Fact]
    public void Integer_filters_bind_a_number_and_reject_text()
    {
        var def = TrackingCatalog.Find("worksheet-tracking")!;
        Assert.Equal(2, TrackingQueryBuilder.Build(def, F(("version", "2")), 10).Parameters["p0"]);
        Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(def, F(("version", "2 OR 1=1")), 10));
    }

    [Fact]
    public void Heavy_screens_require_at_least_one_filter()
    {
        var def = TrackingCatalog.Find("member-tracking")!;
        Assert.True(def.RequiresFilter);
        var ex = Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(def, F(), 10));
        Assert.Contains("at least one filter", ex.Errors[0].Message);
        Assert.Contains("MBMNumber LIKE @p0", TrackingQueryBuilder.Build(def, F(("memberNo", "WMGA")), 10).Sql);
    }

    [Fact]
    public void Number_ranges_bind_whole_numbers_inclusively()
    {
        var def = TrackingCatalog.Find("claim-report3-case")!;
        var q = TrackingQueryBuilder.Build(def, F(("dayInsured.from", "-5"), ("dayInsured.to", "30")), 10);
        Assert.Contains("DayInsured >= @p0", q.Sql);
        Assert.Contains("DayInsured <= @p1", q.Sql);
        Assert.Equal(-5, q.Parameters["p0"]);
        Assert.Equal(30, q.Parameters["p1"]);
        Assert.StartsWith("SELECT TOP (@take)", q.Sql);
        Assert.Contains(") AS CaseLag WHERE", q.Sql);
        var ex = Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(def, F(("dayInsured.from", "1.5"), ("dayInsured.to", "3 OR 1=1")), 10));
        Assert.Equal(2, ex.Errors.Count);
    }

    [Fact]
    public void Empty_filters_are_ignored() =>
        Assert.DoesNotContain("@p0", TrackingQueryBuilder.Build(Def, F(("member", "  "), ("lgDate.from", "")), 10).Sql);
}

public class TrackingCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_identifiers_are_safe()
    {
        Assert.Equal(TrackingCatalog.All.Count, TrackingCatalog.All.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var identifier = new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$");
        var source = new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*( INNER JOIN [A-Za-z_][A-Za-z0-9_]* ON [A-Za-z_.]+ = [A-Za-z_.]+)?$");
        foreach (var d in TrackingCatalog.All)
        {
            Assert.Matches(source, d.Source);
            Assert.DoesNotContain(";", d.FixedWhere ?? "");
            Assert.DoesNotContain("--", d.FixedWhere ?? "");
            Assert.DoesNotContain(";", d.SourceQuery ?? "");
            Assert.DoesNotContain("--", d.SourceQuery ?? "");
            Assert.All(d.Columns, c => Assert.Matches(identifier, c.Column));
            Assert.All(d.Filters, f => Assert.Matches(identifier, f.Column));
            Assert.Equal(d.Filters.Count, d.Filters.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(d.OrderBy.Split(','), part => Assert.Matches(@"^\s*[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?(\s+(ASC|DESC))?\s*$", part));
            Assert.NotEmpty(d.Columns);
        }
    }

    [DatabaseFact]
    public void Every_definition_matches_the_real_schema()
    {
        var config = TestHost.Config();
        foreach (var d in TrackingCatalog.All)
        {
            using var conn = new SqlConnection(config[$"ConnectionStrings:{d.Database}"]);
            conn.Open();
            var allColumns = d.Columns.Select(c => c.Column).Concat(d.Filters.Select(f => f.Column)).Distinct();
            // Selects every column a screen uses (columns, filters) and the order-by, returning no rows: fails on any wrong name.
            using var cmd = new SqlCommand($"SELECT TOP 0 {string.Join(", ", allColumns)} FROM {d.From} WHERE {d.FixedWhere ?? "1 = 1"} ORDER BY {d.OrderBy}", conn);
            var ex = Record.Exception(() => cmd.ExecuteNonQuery());
            Assert.True(ex is null, $"{d.Id} ({d.Source}): {ex?.Message}");
        }
    }
}

public class TrackingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    private HttpClient Client(params int[] positions) => factory.ClientFor(Access(positions));

    [Fact]
    public async Task Requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/tracking")).StatusCode);

    [Fact]
    public async Task Menu_lists_only_screens_the_user_may_open()
    {
        var all = await Client(Enumerable.Range(1, 200).ToArray()).GetFromJsonAsync<List<TrackingDefinitionSummary>>("/api/tracking");
        Assert.Equal(TrackingCatalog.All.Count, all!.Count);
        var some = await Client(17).GetFromJsonAsync<List<TrackingDefinitionSummary>>("/api/tracking");
        Assert.Equal(["lg-tracking"], some!.Select(s => s.Id));
        Assert.Empty((await Client().GetFromJsonAsync<List<TrackingDefinitionSummary>>("/api/tracking"))!);
    }

    [Fact]
    public async Task Each_screen_enforces_its_legacy_permission()
    {
        var denied = Client(3);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/tracking/lg-tracking")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/tracking/lg-tracking/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/tracking/lg-tracking/export.xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(17).GetAsync("/api/tracking/nope")).StatusCode);
    }

    [Fact]
    public async Task Invalid_filters_return_400_with_the_field_errors()
    {
        var res = await Client(17).GetAsync("/api/tracking/lg-tracking/search?f.lgDate.from=bad&f.unknown=1");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("lgDate.from", body);
        Assert.Contains("unknown", body);
    }

    [DatabaseFact]
    public async Task Search_returns_rows_columns_and_respects_filters()
    {
        var c = Client(17);
        var r = await c.GetFromJsonAsync<JsonElement>("/api/tracking/lg-tracking/search?f.caseNo=TA00035615");
        Assert.Equal(JsonValueKind.Array, r.GetProperty("rows").ValueKind);
        Assert.True(r.GetProperty("rows").GetArrayLength() >= 2);
        Assert.Equal("Case No", r.GetProperty("columns")[0].GetProperty("header").GetString());
        Assert.All(r.GetProperty("rows").EnumerateArray(), row => Assert.StartsWith("TA00035615", row[0].GetString()));
        var none = await c.GetFromJsonAsync<JsonElement>("/api/tracking/lg-tracking/search?f.caseNo=ZZZ9999");
        Assert.Equal(0, none.GetProperty("rows").GetArrayLength());
    }

    [DatabaseFact]
    public async Task Heavy_screen_without_a_filter_is_rejected_quickly()
    {
        var res = await Client(6).GetAsync("/api/tracking/member-tracking/search");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("at least one filter", await res.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Search_caps_rows_and_flags_truncation()
    {
        var r = await Client(14).GetFromJsonAsync<JsonElement>("/api/tracking/case-tracking/search");
        Assert.True(r.GetProperty("truncated").GetBoolean());
        Assert.Equal(TrackingDefinition.DefaultMaxRows, r.GetProperty("rows").GetArrayLength());
    }

    [DatabaseFact]
    public async Task Date_filter_limits_results_to_the_range()
    {
        var r = await Client(17).GetFromJsonAsync<JsonElement>("/api/tracking/lg-tracking/search?f.caseNo=TA00035615&f.lgDate.from=2021-08-03&f.lgDate.to=2021-08-06");
        Assert.All(r.GetProperty("rows").EnumerateArray(), row =>
        {
            var d = row[2].GetString()!;
            Assert.InRange(string.CompareOrdinal(d, "2021-08-03"), 0, int.MaxValue);
            Assert.InRange(string.CompareOrdinal(d, "2021-08-06"), int.MinValue, 0);
        });
        Assert.True(r.GetProperty("rows").GetArrayLength() >= 1);
    }

    [DatabaseFact]
    public async Task Every_screen_searches_and_exports_without_error()
    {
        var c = Client(Enumerable.Range(1, 200).ToArray());
        foreach (var d in TrackingCatalog.All)
        {
            var meta = await c.GetAsync($"/api/tracking/{d.Id}");
            Assert.True(meta.IsSuccessStatusCode, $"{d.Id} meta {(int)meta.StatusCode}");
            // The most selective single filter keeps this fast; an unknown value just returns no rows.
            var first = d.Filters.First(f => f.Kind is TrackingFilterKind.Prefix or TrackingFilterKind.Contains);
            var res = await c.GetAsync($"/api/tracking/{d.Id}/search?f.{first.Key}=ZZZ-NO-SUCH-VALUE");
            Assert.True(res.IsSuccessStatusCode, $"{d.Id} search {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        }
        var xlsx = await c.GetAsync("/api/tracking/lg-tracking/export.xlsx?f.caseNo=TA00035615");
        Assert.True(xlsx.IsSuccessStatusCode);
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes[..2]));
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First();
        Assert.Equal("LG Tracking", ws.Cell(1, 1).GetString());
        Assert.Equal("Case No", ws.Cell(5, 1).GetString());
        Assert.StartsWith("TA00035615", ws.Cell(6, 1).GetString());
    }
}




public class SummaryBuilderTests
{
    private static readonly SummaryDefinition Def = SummaryCatalog.Find("claim-summary")!;
    private static Dictionary<string, string> F(params (string, string)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Builds_group_by_from_whitelisted_expressions_only()
    {
        var q = Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(["payor", "regYear", "regMonth"], ["cases", "approved"]), F(("payor", "WM")));
        Assert.Contains("PAYCode AS [payor], YEAR(CaseRegDate) AS [regYear], MONTH(CaseRegDate) AS [regMonth]", q.Sql);
        Assert.Contains("COUNT(DISTINCT CASNumber) AS [cases], SUM(CLMTotalApproved) AS [approved]", q.Sql);
        Assert.Contains("GROUP BY PAYCode, YEAR(CaseRegDate), MONTH(CaseRegDate)", q.Sql);
        Assert.Contains("PAYCode = @p0", q.Sql);
        Assert.DoesNotContain("'WM'", q.Sql);
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_chosen()
    {
        var q = Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(null, null), F(("payor", "WM")));
        Assert.Contains("[payor]", q.Sql);
        Assert.Contains("[actual]", q.Sql);
    }

    [Theory]
    [InlineData("payor; DROP TABLE CAS")]
    [InlineData("PAYCode")]
    [InlineData("YEAR(CaseRegDate)")]
    public void Unknown_dimensions_are_rejected(string dim) =>
        Assert.Throws<TrackingFilterException>(() => Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest([dim], ["cases"]), F(("payor", "WM"))));

    [Fact]
    public void Limits_dimensions_measures_top_and_requires_a_filter_on_big_views()
    {
        Assert.Throws<TrackingFilterException>(() => Infrastructure.Tracking.SummaryQueryBuilder.Build(Def,
            new SummaryRequest(["payor", "insurer", "plan", "sex", "ageBand"], ["cases"]), F(("payor", "WM"))));
        Assert.Throws<TrackingFilterException>(() => Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(["payor"], ["nope"]), F(("payor", "WM"))));
        Assert.Throws<TrackingFilterException>(() => Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(["payor"], ["cases"], 0), F(("payor", "WM"))));
        var ex = Assert.Throws<TrackingFilterException>(() => Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(["payor"], ["cases"]), F()));
        Assert.Contains("at least one filter", ex.Errors[0].Message);
    }

    [Fact]
    public void Top_orders_by_the_first_measure_descending()
    {
        var q = Infrastructure.Tracking.SummaryQueryBuilder.Build(Def, new SummaryRequest(["hospital"], ["approved"], 20), F(("payor", "WM")));
        Assert.Contains("ORDER BY SUM(CLMTotalApproved) DESC", q.Sql);
        Assert.Equal(21, q.Parameters["take"]);
    }

    [Fact]
    public void Catalog_is_consistent()
    {
        var id = new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9]*$");
        foreach (var d in SummaryCatalog.All)
        {
            Assert.All(d.Dimensions, x => Assert.Matches(id, x.Key));
            Assert.All(d.Measures, x => Assert.Matches(id, x.Key));
            Assert.All(d.DefaultDimensions, k => Assert.Contains(d.Dimensions, x => x.Key == k));
            Assert.All(d.DefaultMeasures, k => Assert.Contains(d.Measures, x => x.Key == k));
        }
    }

    [DatabaseFact]
    public void Every_summary_runs_against_the_real_schema_with_all_dimensions_and_measures()
    {
        var config = TestHost.Config();
        foreach (var d in SummaryCatalog.All)
        {
            var filters = new Dictionary<string, string>();
            if (d.RequiresFilter) filters[d.Filters.First(f => f.Kind is TrackingFilterKind.Exact or TrackingFilterKind.Prefix or TrackingFilterKind.Contains).Key] = "ZZZ-NO-SUCH";
            // Every dimension and every measure at once is rejected (max 4 dims), so test each dimension alone with all measures.
            foreach (var dim in d.Dimensions)
            {
                var q = Infrastructure.Tracking.SummaryQueryBuilder.Build(d, new SummaryRequest([dim.Key], d.Measures.Select(m => m.Key).ToList()), filters);
                using var conn = new SqlConnection(config[$"ConnectionStrings:{d.Database}"]);
                conn.Open();
                using var cmd = new SqlCommand(q.Sql.Replace("SELECT TOP (@take)", "SELECT TOP 0"), conn);
                foreach (var p in q.Parameters.Where(p => p.Key != "take")) cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
                var ex = Record.Exception(() => cmd.ExecuteNonQuery());
                Assert.True(ex is null, $"{d.Id}/{dim.Key}: {ex?.Message}");
            }
        }
    }
}

public class SummaryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Client(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return factory.ClientFor(new string(chars));
    }

    [Fact]
    public async Task Requires_authentication_and_the_legacy_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync("/api/summary/claim-summary/run?f.payor=WM")).StatusCode);
        Assert.Equal(["claim-summary"], (await Client(44).GetFromJsonAsync<List<TrackingDefinitionSummary>>("/api/summary"))!.Select(s => s.Id));
    }

    [Fact]
    public async Task Invalid_options_return_400()
    {
        var c = Client(44);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/summary/claim-summary/run")).StatusCode);
        var res = await c.GetAsync("/api/summary/claim-summary/run?f.payor=WM&dims=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Unknown dimension", await res.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Groups_and_totals_are_consistent()
    {
        var c = Client(44);
        var byPayor = await c.GetFromJsonAsync<JsonElement>("/api/summary/claim-summary/run?f.caseNo.from=JE19400&f.caseNo.to=JE19499&dims=payor&measures=cases,claims,approved");
        Assert.Equal("Payor", byPayor.GetProperty("columns")[0].GetProperty("header").GetString());
        Assert.True(byPayor.GetProperty("rows").GetArrayLength() >= 1);
        var total = await c.GetFromJsonAsync<JsonElement>("/api/summary/claim-summary/run?f.caseNo.from=JE19400&f.caseNo.to=JE19499&dims=&measures=claims");
        Assert.Equal(1, total.GetProperty("rows").GetArrayLength());
        var byYear = await c.GetFromJsonAsync<JsonElement>("/api/summary/claim-summary/run?f.caseNo.from=JE19400&f.caseNo.to=JE19499&dims=regYear&measures=claims");
        var sum = byYear.GetProperty("rows").EnumerateArray().Sum(r => r[1].GetDecimal());
        Assert.Equal(total.GetProperty("rows")[0][0].GetDecimal(), sum);
    }

    [DatabaseFact]
    public async Task Every_summary_definition_and_export_works()
    {
        var c = Client(Enumerable.Range(1, 200).ToArray());
        foreach (var d in SummaryCatalog.All)
        {
            Assert.True((await c.GetAsync($"/api/summary/{d.Id}")).IsSuccessStatusCode, d.Id);
            var first = d.Filters.First(f => f.Kind is TrackingFilterKind.Exact or TrackingFilterKind.Prefix or TrackingFilterKind.Contains);
            var res = await c.GetAsync($"/api/summary/{d.Id}/run?f.{first.Key}=ZZZ-NO-SUCH");
            Assert.True(res.IsSuccessStatusCode, $"{d.Id}: {await res.Content.ReadAsStringAsync()}");
        }
        var xlsx = await c.GetAsync("/api/summary/claim-summary/export.xlsx?f.payor=WM&dims=payor&measures=cases,approved");
        Assert.True(xlsx.IsSuccessStatusCode);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]));
    }
}



