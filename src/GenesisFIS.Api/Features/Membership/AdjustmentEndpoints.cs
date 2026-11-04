using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class AdjustmentEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Adjustment.</summary>
    public const int Screen = 2;

    public static void MapAdjustment(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/adjustment").WithTags("Membership: Adjustment").RequireScreen(Screen);

        g.MapGet("/", async (string memberNumber, IAdjustmentRepository repo, CancellationToken ct) =>
            await repo.GetAsync(memberNumber, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { error = "Record not found." }));
        g.MapGet("/search", async (string? q, IMembershipEnquiryRepository enquiry, CancellationToken ct) =>
            MemberNumber.IsValidSearch(q) ? Results.Ok(await enquiry.SearchAsync(q!, ct))
                : Results.BadRequest(new { error = $"Enter at least {MemberNumber.MinSearchLength} characters (letters, digits, * and -)." }));
        g.MapGet("/neighbour", async (string? memberNumber, string direction, IAdjustmentRepository repo, CancellationToken ct) =>
            await repo.NeighbourAsync(memberNumber ?? "", !string.Equals(direction, "previous", StringComparison.OrdinalIgnoreCase), ct) is { } n ? Results.Ok(new { memberNumber = n })
                : Results.NotFound(new { error = string.Equals(direction, "previous", StringComparison.OrdinalIgnoreCase) ? "First Record Reached!" : "Last Record Reached!" }));
        g.MapGet("/overview", async (string memberNumber, IAdjustmentRepository repo, IMembershipEnquiryRepository enquiry, CancellationToken ct) =>
            await enquiry.GetAsync(memberNumber.Trim(), ct) is { } d ? Results.Ok(new AdjustmentOverview(d, await repo.GetMgmtNoteAsync(memberNumber, ct))) : Results.NotFound(new { error = "Record not found." }));
        g.MapGet("/amendments", async (string memberNumber, IMembershipEnquiryRepository enquiry, CancellationToken ct) => Results.Ok(await enquiry.GetAmendmentsAsync(memberNumber.Trim(), ct)));
        g.MapGet("/covered-persons", async (string memberNumber, IMembershipTabsRepository tabs, CancellationToken ct) => Results.Ok(await tabs.GetCoveredPersonsAsync(memberNumber.Trim(), ct)));
        g.MapGet("/member-history", async (string memberNumber, IMembershipTabsRepository tabs, CancellationToken ct) => Results.Ok(await tabs.GetMemberHistoryAsync(memberNumber.Trim(), ct)));
        g.MapGet("/fees", async (string memberNumber, IMembershipTabsRepository tabs, CancellationToken ct) => Results.Ok(await tabs.GetFeesAsync(memberNumber.Trim(), ct)));
        g.MapPut("/mgmt-note", async (MgmtNoteRequest request, ClaimsPrincipal user, IAdjustmentRepository repo, CancellationToken ct) =>
        {
            await repo.SaveMgmtNoteAsync(request.MemberNumber ?? "", request.Note, user.UserCode(), ct);
            return Results.Ok(new { saved = true });
        });
        g.MapPost("/cancellation/quote", async (CancellationRequest request, ICancellationRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/cancellation", async (CancellationRequest request, ClaimsPrincipal user, ICancellationRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.CancelAsync(request, user.UserCode(), ct)));
        g.MapPost("/plan-change/quote", async (PlanChangeRequest request, IPlanChangeRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/plan-change", async (PlanChangeRequest request, ClaimsPrincipal user, IPlanChangeRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ChangeAsync(request, user.UserCode(), ct)));
        g.MapPost("/status-endorsement", async (StatusEndorsementRequest request, ClaimsPrincipal user, IStatusEndorsementRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ApplyAsync(request, user.UserCode(), ct)));
        g.MapPost("/supplementary/quote", async (SupplementaryRequest request, ISupplementaryRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/supplementary", async (SupplementaryRequest request, ClaimsPrincipal user, ISupplementaryRepository repo, CancellationToken ct) =>
            Results.Created("/api/membership/adjustment/supplementary", await repo.AddAsync(request, user.UserCode(), ct)));
        g.MapPost("/supplementary/cancel/quote", async (SupplementaryCancelRequest request, ISupplementaryCancelRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/supplementary/cancel", async (SupplementaryCancelRequest request, ClaimsPrincipal user, ISupplementaryCancelRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.CancelAsync(request, user.UserCode(), ct)));
        g.MapPost("/supplementary/delete/quote", async (SupplementaryDeleteRequest request, ISupplementaryDeleteRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/supplementary/delete", async (SupplementaryDeleteRequest request, ClaimsPrincipal user, ISupplementaryDeleteRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.DeleteAsync(request, user.UserCode(), ct)));
        g.MapPut("/amendment", async (PrincipalAmendment amendment, ClaimsPrincipal user, IAdjustmentRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AmendAsync(amendment, user.UserCode(), ct)));
    }
}








