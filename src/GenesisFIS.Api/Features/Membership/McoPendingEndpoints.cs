using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class McoPendingEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > MCO Pending.</summary>
    public const int Screen = 3;

    public static void MapMcoPending(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/mco-pending").WithTags("Membership: MCO Pending").RequireScreen(Screen);

        g.MapGet("/", async (string? memberFrom, string? memberTo, DateOnly? bordxFrom, DateOnly? bordxTo, string? status, IMcoPendingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchAsync(new McoPendingCriteria(memberFrom, memberTo, bordxFrom, bordxTo, status), ct)));

        g.MapPut("/", async (McoPendingChange change, ClaimsPrincipal user, IMcoPendingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ChangeAsync(change, user.UserCode(), ct)));
    }
}
