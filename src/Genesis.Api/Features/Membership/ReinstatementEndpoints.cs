using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class ReinstatementEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Member Reinstatement.</summary>
    public const int Screen = 1;

    public static void MapReinstatement(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/reinstatement").WithTags("Membership: Reinstatement").RequireScreen(Screen);

        g.MapGet("/", async (string memberNumber, IReinstatementRepository repo, CancellationToken ct) =>
            await repo.GetAsync(memberNumber, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { error = "Record not found." }));
        g.MapPost("/quote", async (ReinstatementRequest request, IReinstatementRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/", async (ReinstatementRequest request, ClaimsPrincipal user, IReinstatementRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ReinstateAsync(request, user.UserCode(), ct)));
    }
}
