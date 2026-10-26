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
        foreach (var tab in new[] { "amendments", "cases", "case-history", "account-summary", "account-history", "exclusions", "remarks" })
        {
            var res = await client.GetAsync($"/api/membership/members/{Uri.EscapeDataString(Member)}/{tab}");
            Assert.True(res.IsSuccessStatusCode, $"{tab} returned {(int)res.StatusCode}");
        }
    }
}
