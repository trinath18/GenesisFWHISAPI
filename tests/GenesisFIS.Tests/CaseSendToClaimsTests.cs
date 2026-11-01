using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;

namespace GenesisFIS.Tests;

public class LegacyFilterTests
{
    [Fact]
    public void Builds_the_same_fragments_the_legacy_forms_built()
    {
        var f = new LegacyFilter().Text("CASNumber", ">=", "WM24000001", "from").Text("CASNumber", "<=", " ", "to").Date("CaseRegDate", ">=", new DateOnly(2024, 3, 5))
            .Number("ClaimVersion", "<=", 3).Empty("CASTokenDate", true).Empty("CaseRegDate", false).Constant("AND (CASStatus = 'O' OR CASStatus = 'C')");
        Assert.Equal(" AND CASNumber >= 'WM24000001' AND CaseRegDate >= '03/05/2024' AND ClaimVersion <= '3' AND (CASTokenDate IS NULL OR CASTokenDate = '') AND (CASStatus = 'O' OR CASStatus = 'C')", f.ToString());
        Assert.Equal(5, f.Count);
    }

    [Theory]
    [InlineData("x' OR 1=1 --")]
    [InlineData("ABC;DROP")]
    [InlineData("a\"b")]
    [InlineData("1234567890123456789012345678901")]
    public void Refuses_values_that_could_change_the_legacy_dynamic_sql(string value)
    {
        Assert.Throws<BusinessRuleException>(() => new LegacyFilter().Text("CASUserToken", "=", value, "passedBy"));
    }

    [Fact]
    public void Column_names_and_operators_are_never_taken_from_input()
    {
        Assert.Throws<ArgumentException>(() => new LegacyFilter().Text("CASNumber; --", "=", "A", "x"));
        Assert.Throws<ArgumentException>(() => new LegacyFilter().Text("CASNumber", "<>", "A", "x"));
    }

    [Fact]
    public void Rep_tick_rules_follow_the_legacy_messages()
    {
        var d = new DateOnly(2024, 1, 1);
        Assert.Null(SendToClaimsRules.RepCannotPass(d, d, d, "H1", false));
        Assert.Equal("Date of Register is Empty.", SendToClaimsRules.RepCannotPass(null, d, d, "H1", false));
        Assert.Equal("Date of Admitted is Empty.", SendToClaimsRules.RepCannotPass(d, null, d, "H1", false));
        Assert.Equal("Date of Discharge cannot be empty!", SendToClaimsRules.RepCannotPass(d, d, null, "H1", false));
        Assert.Equal("Hospital Name is Empty.", SendToClaimsRules.RepCannotPass(d, d, d, " ", false));
        Assert.Equal("File has been send out.", SendToClaimsRules.RepCannotPass(d, d, d, "H1", true));
    }
}

public class CaseSendToClaimsTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    [Fact]
    public async Task Send_to_screens_need_154_and_ascii_needs_18()
    {
        var other = factory.ClientFor(WriteApiFactory.Access(11, 13));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/case-send-to-claims-rep/passed-by")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/case-cru-send-to-claims/passed-by")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/case-ascii/payors")).StatusCode);
    }

    [Fact]
    public async Task Requests_are_validated_before_any_procedure_runs()
    {
        var cli = factory.ClientFor(WriteApiFactory.Access(154, 18));
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-send-to-claims-rep/search", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-send-to-claims-rep/search", new { passedBy = "x' or 1=1--" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-cru-send-to-claims/pass", new { items = new[] { new { caseNumber = "WM1", version = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-send-to-claims-rep/pass", new { caseNumbers = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-ascii/claim", new { payor = "WM" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cli.PostAsJsonAsync("/api/case-ascii/case", new { payor = "W'", from = "2024-01-01", to = "2024-01-31", endorsement = "0" })).StatusCode);
    }

    [DatabaseFact]
    public async Task The_legacy_procedures_answer_the_searches()
    {
        var cli = factory.ClientFor(WriteApiFactory.Access(154, 18));
        var res = await cli.PostAsJsonAsync("/api/case-send-to-claims-rep/search", new { caseNoFrom = "NOSUCHCASE01", exactCase = true });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Empty((await res.Content.ReadFromJsonAsync<SendToClaimsResult>(Json))!.Rows);
        res = await cli.PostAsJsonAsync("/api/case-cru-send-to-claims/search", new { caseNoFrom = "NOSUCHCASE01", exactCase = true });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/case-send-to-claims-rep/passed-by")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/case-cru-send-to-claims/passed-by")).StatusCode);
        Assert.NotEmpty((await cli.GetFromJsonAsync<List<CaseOption>>("/api/case-ascii/payors", Json))!);
        // A payor and range with nothing to export: the procedure runs and the API answers "No record found!" without taking a batch number.
        Assert.Equal(HttpStatusCode.NotFound, (await cli.PostAsJsonAsync("/api/case-ascii/claim", new { payor = "ZZ", from = "1991-01-01", to = "1991-01-02" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cli.PostAsJsonAsync("/api/case-ascii/case", new { payor = "ZZ", from = "1991-01-01", to = "1991-01-02", endorsement = "0" })).StatusCode);
    }
}
