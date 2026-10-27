using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Maintenance;

namespace Genesis.Api.Features.Maintenance;

public static class IcdSelectionEndpoints
{
    /// <summary>Legacy USRAccess position of Maintenance > Case > ICD Selection.</summary>
    public const int Screen = 90;

    public static void MapIcdSelection(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance/icd-selection").WithTags("Maintenance: Case").RequireScreen(Screen);
        g.MapGet("/", async (string? search, IIcdSelectionRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(search, ct)));
        g.MapPost("/select", async (IcdSelectionRequest request, ClaimsPrincipal user, IIcdSelectionRepository repo, CancellationToken ct) => Results.Ok(new { changed = await repo.SelectAsync(request, user.UserCode(), ct) }));
        g.MapPost("/unselect", async (IcdSelectionRequest request, ClaimsPrincipal user, IIcdSelectionRepository repo, CancellationToken ct) => Results.Ok(new { changed = await repo.UnselectAsync(request, user.UserCode(), ct) }));
    }
}
