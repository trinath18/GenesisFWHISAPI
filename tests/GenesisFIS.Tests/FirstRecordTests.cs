using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisFIS.Api.Features.Cases;
using GenesisFIS.Api.Features.Membership;

namespace GenesisFIS.Tests;

/// <summary>Legacy CmdNext searched "number > box", so Next on an empty box loaded the first record. Each Next screen keeps that.</summary>
public class FirstRecordTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    private async Task<string> FirstAsync(int screen, string url, string field)
    {
        var res = await factory.ClientFor(Access(screen)).GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var n = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(field).GetString();
        Assert.False(string.IsNullOrWhiteSpace(n), url);
        return n!;
    }

    [DatabaseFact]
    public async Task Next_on_an_empty_member_box_loads_the_first_member()
    {
        var first = await FirstAsync(MembershipEndpoints.EnquiryScreen, "/api/membership/members/first", "number");
        // Nothing comes before it, and Next from it does not return it again.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(Access(MembershipEndpoints.EnquiryScreen)).GetAsync($"/api/membership/members/{Uri.EscapeDataString(first)}/previous")).StatusCode);
        Assert.Equal(first, await FirstAsync(CaseMenuEndpoints.NonDisclosureScreen, "/api/case-non-disclosure/first", "memberNumber"));
        Assert.Equal(first.Trim(), await FirstAsync(AdjustmentEndpoints.Screen, "/api/membership/adjustment/neighbour?memberNumber=&direction=next", "memberNumber"));
    }

    [DatabaseFact]
    public async Task Next_on_an_empty_case_box_loads_the_first_case()
    {
        var first = await FirstAsync(CaseEndpoints.InquiryScreen, "/api/cases/first", "caseNumber");
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(Access(CaseEndpoints.InquiryScreen)).GetAsync($"/api/cases/{first}/previous")).StatusCode);
        Assert.Equal(first, await FirstAsync(CaseAdjustmentEndpoints.Screen, "/api/case-adjustment/first", "caseNumber"));
        // Audit Review walks CAS (or CASAuditReview for Next Rev Case).
        await FirstAsync(CaseLifetimeAuditBordxEndpoints.AuditScreen, "/api/case-audit-review/first", "caseNumber");
        // CASAuditReview may be empty: then Next Rev Case answers the legacy "Last Record Reached!".
        var reviewed = await factory.ClientFor(Access(CaseLifetimeAuditBordxEndpoints.AuditScreen)).GetAsync("/api/case-audit-review/first?reviewedOnly=true");
        Assert.True(reviewed.StatusCode is HttpStatusCode.OK || (reviewed.StatusCode is HttpStatusCode.NotFound && (await reviewed.Content.ReadAsStringAsync()).Contains("Last Record Reached!")));
    }

    [DatabaseFact]
    public async Task Previous_still_needs_a_number_and_first_needs_the_screen()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.ClientFor(Access(AdjustmentEndpoints.Screen)).GetAsync("/api/membership/adjustment/neighbour?memberNumber=&direction=previous")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Access(1)).GetAsync("/api/cases/first")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Access(1)).GetAsync("/api/membership/members/first")).StatusCode);
    }
}
