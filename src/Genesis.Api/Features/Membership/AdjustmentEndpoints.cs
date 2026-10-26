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
        g.MapPut("/amendment", async (PrincipalAmendment amendment, ClaimsPrincipal user, IAdjustmentRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AmendAsync(amendment, user.UserCode(), ct)));
    }
}
