using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Maintenance;
using GenesisFIS.Core.Writes;

namespace GenesisFIS.Api.Features.Maintenance;

public static class PanelHospitalEndpoints
{
    /// <summary>Legacy USRAccess position of Maintenance > Panel Hospital Maintenance.</summary>
    public const int Screen = 1;

    public static void MapPanelHospitals(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance/panel-hospitals").WithTags("Maintenance: Panel Hospitals").RequireScreen(Screen);

        g.MapGet("/", async (IPanelHospitalRepository repo, CancellationToken ct) => Results.Ok(await repo.ListPanelsAsync(ct)));

        g.MapGet("/members", async (string panel, IPanelHospitalRepository repo, CancellationToken ct) =>
            await repo.GetPanelAsync(panel, ct) is null
                ? Results.NotFound(new { error = $"Panel '{panel}' does not exist." })
                : Results.Ok(await repo.ListMembersAsync(panel, ct)));

        g.MapGet("/available", async (string panel, string? q, IPanelHospitalRepository repo, CancellationToken ct) =>
            await repo.GetPanelAsync(panel, ct) is null
                ? Results.NotFound(new { error = $"Panel '{panel}' does not exist." })
                : Results.Ok(await repo.ListAvailableAsync(panel, q, ct)));

        // The panel code (for example "WM - AIROD") travels in the query string so it never needs path escaping.
        g.MapPut("/", async (string panel, PanelHospitalChange change, ClaimsPrincipal user, IPanelHospitalRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ApplyAsync(panel, change, user.UserCode(), ct)));
    }
}
