using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Maintenance;

namespace Genesis.Api.Features.Maintenance;

public static class SuspensionEndpoints
{
    /// <summary>Legacy USRAccess position of Maintenance > Suspension.</summary>
    public const int Screen = 110;

    public static void MapSuspensions(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance/suspensions").WithTags("Maintenance: Suspension").RequireScreen(Screen);

        g.MapGet("/payors", async (ISuspensionRepository repo, CancellationToken ct) => Results.Ok(await repo.ListPayorsAsync(ct)));
        g.MapGet("/", async (string? kind, string? code, string? payor, ISuspensionRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(kind, code, payor, ct)));
        g.MapPost("/", async (SuspensionRequest request, ClaimsPrincipal user, ISuspensionRepository repo, CancellationToken ct) =>
            Results.Created("/api/maintenance/suspensions", await repo.CreateAsync(request, user.UserCode(), ct)));
        g.MapPut("/", async (SuspensionRequest request, ClaimsPrincipal user, ISuspensionRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UpdateAsync(request, user.UserCode(), ct)));
        g.MapDelete("/", async (string kind, string code, ClaimsPrincipal user, ISuspensionRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteAsync(kind, code, user.UserCode(), ct);
            return Results.Ok(new { deleted = code });
        });
    }
}
