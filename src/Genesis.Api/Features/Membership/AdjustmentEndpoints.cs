using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class AdjustmentEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Adjustment.</summary>
    public const int Screen = 2;

    public static void MapAdjustment(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/adjustment").WithTags("Membership: Adjustment").RequireScreen(Screen);

        g.MapGet("/", async (string memberNumber, IAdjustmentRepository repo, CancellationToken ct) =>
            await repo.GetAsync(memberNumber, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { error = "Record not found." }));
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
        g.MapPut("/amendment", async (PrincipalAmendment amendment, ClaimsPrincipal user, IAdjustmentRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AmendAsync(amendment, user.UserCode(), ct)));
    }
}




