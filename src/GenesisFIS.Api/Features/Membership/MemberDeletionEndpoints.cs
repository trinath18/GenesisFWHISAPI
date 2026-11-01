using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class MemberDeletionEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Deletion.</summary>
    public const int Screen = 5;

    public static void MapMemberDeletion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/deletion").WithTags("Membership: Deletion").RequireScreen(Screen);

        g.MapGet("/health-plans", async (IMemberDeletionRepository repo, CancellationToken ct) => Results.Ok(await repo.ListHealthPlansAsync(ct)));

        // Preview is a POST only because the criteria travel in the body; it changes nothing.
        g.MapPost("/preview", async (DeletionCriteria criteria, IMemberDeletionRepository repo, CancellationToken ct) => Results.Ok(await repo.PreviewAsync(criteria, ct)));
        g.MapPost("/", async (DeletionRequest request, ClaimsPrincipal user, IMemberDeletionRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.DeleteAsync(request, user.UserCode(), ct)));
    }
}

