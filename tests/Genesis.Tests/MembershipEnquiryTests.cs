using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;
using Genesis.Core.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Genesis.Tests;

public class MemberNumberTests
{
    [Theory]
    [InlineData("WMGA6639886*01", true)]
    [InlineData("XLA06315*06", true)]
    [InlineData("ab-12 3", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a%", false)]
    [InlineData("a_b", false)]
    [InlineData("[a]", false)]
    [InlineData("a'; drop table x;--", false)]
    [InlineData("a/b", false)]
    public void IsValid_allows_only_member_number_characters(string value, bool expected) =>
        Assert.Equal(expected, MemberNumber.IsValid(value));

    [Theory]
    [InlineData("AB", false)]
    [InlineData("ABC", true)]
    [InlineData(" AB ", false)]
    public void IsValidSearch_requires_minimum_length(string value, bool expected) =>
        Assert.Equal(expected, MemberNumber.IsValidSearch(value));
}

/// <summary>Skipped automatically when the restored local SQL Server databases are not reachable.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        try
        {
            using var c = new SqlConnection(TestHost.Config()["ConnectionStrings:Main"]);
            c.Open();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM MBM WHERE MBMNumber = 'WMGA6639886*01'", c);
            return (int)cmd.ExecuteScalar()! == 1 ? null : "Fixture member WMGA6639886*01 not found in FWHSHISDB.";
        }
        catch (Exception ex) { return "Database not available: " + ex.GetType().Name; }
    });

    public DatabaseFactAttribute() { if (Reason.Value is { } r) Skip = r; }
}

internal static class TestHost
{
    public static IConfiguration Config() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true).Build();
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-signing-key-0123456789-abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = JwtKey }));
    }

    public HttpClient ClientFor(string access)
    {
        var tokens = Services.GetRequiredService<TokenService>();
        var (token, _) = tokens.Create(new UserRecord("TEST", "Test User", null, "A", "ITD", "ITD", access, false));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public class MembershipEnquiryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Member = "WMGA6639886*01";
    private static readonly string AllAccess = new('1', 20);
    private static readonly string NoEnquiry = "110" + new string('1', 17);

    [Fact]
    public async Task Requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/membership/members?q=WMGA")).StatusCode);

    [Fact]
    public async Task Requires_enquiry_screen_permission() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(NoEnquiry).GetAsync("/api/membership/members?q=WMGA")).StatusCode);

    [Theory]
    [InlineData("/api/membership/members?q=ab")]
    [InlineData("/api/membership/members?q=a%25b")]
    [InlineData("/api/membership/members/a'--")]
    public async Task Rejects_invalid_input(string url) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.ClientFor(AllAccess).GetAsync(url)).StatusCode);

    [DatabaseFact]
    public async Task Search_returns_prefix_matches()
    {
        var page = await factory.ClientFor(AllAccess).GetFromJsonAsync<MemberSearchPage>("/api/membership/members?q=WMGA66398");
        Assert.NotNull(page);
        Assert.Contains(page.Items, m => m.Number == Member);
        Assert.All(page.Items, m => Assert.StartsWith("WMGA66398", m.Number));
    }

    [DatabaseFact]
    public async Task Detail_returns_member_and_unknown_is_404()
    {
        var client = factory.ClientFor(AllAccess);
        var m = await client.GetFromJsonAsync<MemberDetail>($"/api/membership/members/{Uri.EscapeDataString(Member)}");
        Assert.Equal(Member, m!.Number);
        Assert.Equal("ASLINA TANJUNG", m.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/membership/members/ZZZZ0000000")).StatusCode);
    }

    [DatabaseFact]
    public async Task Cases_tab_lists_known_case()
    {
        var cases = await factory.ClientFor(AllAccess).GetFromJsonAsync<List<MemberCase>>($"/api/membership/members/{Uri.EscapeDataString(Member)}/cases");
        Assert.Contains(cases!, c => c.CaseNumber == "WM00435");
    }

    [DatabaseFact]
    public async Task Every_tab_endpoint_responds_ok()
    {
        var client = factory.ClientFor(AllAccess);
        foreach (var tab in new[] { "amendments", "cases", "case-history", "account-summary", "account-history", "exclusions", "remarks",
            "covered-persons", "benefits", "member-history", "fees", "non-disclosure", "policy-terms", "lifetime-limit", "takeover-claims", "policy-wording" })
        {
            var res = await client.GetAsync($"/api/membership/members/{Uri.EscapeDataString(Member)}/{tab}");
            Assert.True(res.IsSuccessStatusCode, $"{tab} returned {(int)res.StatusCode}");
        }
    }

    [DatabaseFact]
    public async Task Benefits_returns_latest_effective_block_for_the_members_plan()
    {
        var b = await factory.ClientFor(AllAccess).GetFromJsonAsync<MemberBenefits>($"/api/membership/members/{Uri.EscapeDataString(Member)}/benefits");
        Assert.Equal("FH01", b!.Principal.PlanCode);
        Assert.NotEmpty(b.Principal.Items);
        Assert.Contains(b.Principal.Items, i => i.Code == "203" && i.Description == "Surgical Fees");
    }

    [DatabaseFact]
    public async Task Member_and_case_history_find_the_member_by_ic_and_by_name_and_dob()
    {
        var client = factory.ClientFor(AllAccess);
        var mh = await client.GetFromJsonAsync<MemberHistory>($"/api/membership/members/{Uri.EscapeDataString(Member)}/member-history");
        Assert.Contains(mh!.ByIc, h => h.MemberNumber == Member);
        Assert.Contains(mh.ByNameDob, h => h.MemberNumber == Member);
        var ch = await client.GetFromJsonAsync<MemberCaseHistory>($"/api/membership/members/{Uri.EscapeDataString(Member)}/case-history");
        Assert.Contains(ch!.ByIc, c => c.CaseNumber == "WM00435");
        Assert.Contains(ch.ByNameDob, c => c.CaseNumber == "WM00435");
    }

    [DatabaseFact]
    public async Task Fees_come_from_MBMOthers()
    {
        var fees = await factory.ClientFor(AllAccess).GetFromJsonAsync<MemberFees>($"/api/membership/members/{Uri.EscapeDataString(Member)}/fees");
        Assert.Equal(105m, fees!.Premium);
        Assert.Equal(15m, fees.McoFee);
    }

    [DatabaseFact]
    public async Task Policy_wording_reports_when_store_is_not_configured()
    {
        var w = await factory.ClientFor(AllAccess).GetFromJsonAsync<PolicyWordingListing>($"/api/membership/members/{Uri.EscapeDataString(Member)}/policy-wording");
        Assert.False(w!.StoreConfigured);
        Assert.Empty(w.Files);
    }

    [DatabaseFact]
    public async Task Print_outputs_are_real_documents()
    {
        var client = factory.ClientFor(AllAccess);
        var n = Uri.EscapeDataString(Member);

        var pdf = await client.GetAsync($"/api/membership/members/{n}/print/enquiry.pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Equal("member-WMGA6639886_01-enquiry.pdf", pdf.Content.Headers.ContentDisposition!.FileName);

        foreach (var kind in new[] { "exclusion-form", "history" })
        {
            var xlsx = await client.GetAsync($"/api/membership/members/{n}/print/{kind}.xlsx");
            Assert.True(xlsx.IsSuccessStatusCode, kind);
            var bytes = await xlsx.Content.ReadAsByteArrayAsync();
            Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes[..2]));
            using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
            Assert.NotEmpty(wb.Worksheets);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/membership/members/ZZZZ0000000/print/enquiry.pdf")).StatusCode);
    }
}

public class PolicyWordingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-" + Guid.NewGuid().ToString("N"));
    private readonly IPolicyWordingStore _store;

    public PolicyWordingStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "PLAN1"));
        File.WriteAllText(Path.Combine(_root, "PLAN1", "wording.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_root, "PLAN1", "notes.txt"), "no");
        File.WriteAllText(Path.Combine(_root, "secret.pdf"), "secret");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStore:PolicyWordingRoot"] = _root }).Build();
        _store = new Genesis.Infrastructure.Membership.FileSystemPolicyWordingStore(cfg);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Lists_only_document_types_in_the_plan_folder()
    {
        Assert.True(_store.IsConfigured);
        Assert.Equal(["wording.pdf"], _store.List("PLAN1").Select(f => f.Name));
    }

    [Fact]
    public void Opens_a_file_with_the_right_content_type()
    {
        var content = _store.Open("PLAN1", "wording.pdf");
        Assert.NotNull(content);
        using (content.Content) Assert.Equal("application/pdf", content.ContentType);
    }

    [Theory]
    [InlineData("..", "secret.pdf")]
    [InlineData("PLAN1", "..\\secret.pdf")]
    [InlineData("PLAN1", "../secret.pdf")]
    [InlineData("PLAN1", "notes.txt")]
    [InlineData("..\\", "secret.pdf")]
    [InlineData("PLAN1/..", "secret.pdf")]
    [InlineData("", "wording.pdf")]
    public void Rejects_traversal_and_non_document_types(string folder, string file) =>
        Assert.Null(_store.Open(folder, file));

    [Theory]
    [InlineData("..")]
    [InlineData("..\\")]
    [InlineData("PLAN1/..")]
    [InlineData("PLAN1\\..")]
    [InlineData("")]
    [InlineData("MISSING")]
    public void Lists_nothing_for_unsafe_or_unknown_folders(string folder) =>
        Assert.Empty(_store.List(folder));

    [Fact]
    public void Unconfigured_store_returns_nothing()
    {
        var empty = new Genesis.Infrastructure.Membership.FileSystemPolicyWordingStore(new ConfigurationBuilder().Build());
        Assert.False(empty.IsConfigured);
        Assert.Empty(empty.List("PLAN1"));
    }
}


