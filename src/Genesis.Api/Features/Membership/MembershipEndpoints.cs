using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class MembershipEndpoints
{
    /// <summary>Legacy USRAccess position for the "Membership > Enquiry" menu (CheckAccess(3) in mdiMain).</summary>
    public const int EnquiryScreen = 3;

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

        Tab(g, "amendments", (r, n, ct) => r.GetAmendmentsAsync(n, ct));
        Tab(g, "cases", (r, n, ct) => r.GetCasesAsync(n, ct));
        Tab(g, "case-history", (r, n, ct) => r.GetCaseHistoryAsync(n, ct));
        Tab(g, "account-summary", (r, n, ct) => r.GetAccountSummaryAsync(n, ct));
        Tab(g, "account-history", (r, n, ct) => r.GetAccountHistoryAsync(n, ct));
        Tab(g, "exclusions", (r, n, ct) => r.GetExclusionsAsync(n, ct));
        Tab(g, "remarks", (r, n, ct) => r.GetRemarksAsync(n, ct));
    }

    private static void Tab<T>(RouteGroupBuilder g, string name,
        Func<IMembershipEnquiryRepository, string, CancellationToken, Task<IReadOnlyList<T>>> load) =>
        g.MapGet($"/{{number}}/{name}", async (string number, IMembershipEnquiryRepository repo, CancellationToken ct) =>
            MemberNumber.IsValid(number)
                ? Results.Ok(await load(repo, number, ct))
                : Results.BadRequest(new { error = "Invalid membership number." }));
}

