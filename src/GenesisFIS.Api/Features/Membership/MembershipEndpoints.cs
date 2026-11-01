using System.Security.Claims;
using System.Text.RegularExpressions;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;
using GenesisFIS.Reports.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static partial class MembershipEndpoints
{
    /// <summary>Legacy USRAccess position for the "Membership > Enquiry" menu (CheckAccess(3) in mdiMain).</summary>
    public const int EnquiryScreen = 3;

    [GeneratedRegex(@"^[\w\- .()]{1,120}$")]
    private static partial Regex SafeFileName();

    public static void MapMembership(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/members").WithTags("Membership Enquiry").RequireScreen(EnquiryScreen);

        g.MapGet("/", async (string? q, IMembershipEnquiryRepository repo, CancellationToken ct) =>
            MemberNumber.IsValidSearch(q)
                ? Results.Ok(await repo.SearchAsync(q!, ct))
                : Results.BadRequest(new { error = $"Enter at least {MemberNumber.MinSearchLength} characters (letters, digits, * and -)." }));

        g.MapGet("/{number}", async (string number, IMembershipEnquiryRepository repo, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var member = await repo.GetAsync(number, ct);
            return member is null ? Results.NotFound(new { error = "No record found." }) : Results.Ok(member);
        });

        List(g, "amendments", (r, _, n, ct) => r.GetAmendmentsAsync(n, ct));
        List(g, "cases", (r, _, n, ct) => r.GetCasesAsync(n, ct));
        List(g, "account-summary", (r, _, n, ct) => r.GetAccountSummaryAsync(n, ct));
        List(g, "account-history", (r, _, n, ct) => r.GetAccountHistoryAsync(n, ct));
        List(g, "exclusions", (r, _, n, ct) => r.GetExclusionsAsync(n, ct));
        List(g, "remarks", (r, _, n, ct) => r.GetRemarksAsync(n, ct));

        List(g, "covered-persons", (_, t, n, ct) => t.GetCoveredPersonsAsync(n, ct));
        List(g, "non-disclosure", (_, t, n, ct) => t.GetNonDisclosureAsync(n, ct));
        List(g, "policy-terms", (_, t, n, ct) => t.GetPolicyTermsAsync(n, ct));
        List(g, "lifetime-limit", (_, t, n, ct) => t.GetLifetimeLimitAsync(n, ct));
        List(g, "takeover-claims", (_, t, n, ct) => t.GetTakeoverClaimsAsync(n, ct));

        Item(g, "benefits", (t, n, ct) => t.GetBenefitsAsync(n, ct));
        Item(g, "member-history", (t, n, ct) => t.GetMemberHistoryAsync(n, ct));
        Item(g, "case-history", (t, n, ct) => t.GetCaseHistoryAsync(n, ct));
        Item(g, "fees", (t, n, ct) => t.GetFeesAsync(n, ct));

        g.MapGet("/{number}/policy-wording", async (string number, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var folder = await tabs.GetPolicyWordingFolderAsync(number, ct);
            var files = folder is null || !store.IsConfigured ? [] : store.List(folder);
            return Results.Ok(new PolicyWordingListing(folder, store.IsConfigured, files));
        });

        g.MapGet("/{number}/policy-wording/{file}", async (string number, string file, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var folder = await tabs.GetPolicyWordingFolderAsync(number, ct);
            var content = folder is null ? null : store.Open(folder, file);
            return content is null ? Results.NotFound(new { error = "Document not found." }) : Results.File(content.Content, content.ContentType, content.FileName);
        });

        var print = g.MapGroup("/{number}/print");

        print.MapGet("/enquiry.pdf", async (string number, ClaimsPrincipal user, IMembershipEnquiryRepository repo, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var member = await repo.GetAsync(number, ct);
            if (member is null) return Results.NotFound(new { error = "Report cannot be printed without any record." });
            var cases = await repo.GetCasesAsync(number, ct);
            return Results.File(MemberEnquiryReport.ToPdf(member, cases, user.Identity?.Name ?? "unknown"), "application/pdf", FileName(number, "enquiry", "pdf"));
        });

        print.MapGet("/exclusion-form.xlsx", async (string number, IMembershipEnquiryRepository repo, IMembershipTabsRepository tabs, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var member = await repo.GetAsync(number, ct);
            if (member is null) return Results.NotFound(new { error = "Report cannot be printed without any record." });
            var bytes = MemberWorkbooks.ExclusionForm(member, await tabs.GetCoveredPersonsAsync(number, ct),
                (await tabs.GetMemberHistoryAsync(number, ct))?.ByIc ?? [], await repo.GetAccountHistoryAsync(number, ct),
                await repo.GetExclusionsAsync(number, ct), await repo.GetRemarksAsync(number, ct), await tabs.GetFeesAsync(number, ct));
            return Results.File(bytes, Xlsx, FileName(number, "exclusion-form", "xlsx"));
        });

        print.MapGet("/history.xlsx", async (string number, IMembershipEnquiryRepository repo, IMembershipTabsRepository tabs, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var member = await repo.GetAsync(number, ct);
            var history = member is null ? null : await tabs.GetMemberHistoryAsync(number, ct);
            var cases = member is null ? null : await tabs.GetCaseHistoryAsync(number, ct);
            if (member is null || history is null || cases is null) return Results.NotFound(new { error = "Report cannot be printed without any record." });
            return Results.File(MemberWorkbooks.History(member, history, cases), Xlsx, FileName(number, "history", "xlsx"));
        });
    }

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string FileName(string number, string kind, string ext) =>
        $"member-{Regex.Replace(number.Trim(), "[^A-Za-z0-9-]", "_")}-{kind}.{ext}";

    private static void List<T>(RouteGroupBuilder g, string name,
        Func<IMembershipEnquiryRepository, IMembershipTabsRepository, string, CancellationToken, Task<IReadOnlyList<T>>> load) =>
        g.MapGet($"/{{number}}/{name}", async (string number, IMembershipEnquiryRepository repo, IMembershipTabsRepository tabs, CancellationToken ct) =>
            MemberNumber.IsValid(number)
                ? Results.Ok(await load(repo, tabs, number, ct))
                : Results.BadRequest(new { error = "Invalid membership number." }));

    private static void Item<T>(RouteGroupBuilder g, string name, Func<IMembershipTabsRepository, string, CancellationToken, Task<T?>> load) where T : class =>
        g.MapGet($"/{{number}}/{name}", async (string number, IMembershipTabsRepository tabs, CancellationToken ct) =>
        {
            if (!MemberNumber.IsValid(number)) return Results.BadRequest(new { error = "Invalid membership number." });
            var item = await load(tabs, number, ct);
            return item is null ? Results.NotFound(new { error = "No record found." }) : Results.Ok(item);
        });
}

