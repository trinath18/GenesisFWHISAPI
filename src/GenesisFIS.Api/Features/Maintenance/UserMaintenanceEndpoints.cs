using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Users;

namespace GenesisFIS.Api.Features.Maintenance;

public static class UserMaintenanceEndpoints
{
    /// <summary>Legacy USRAccess position of Maintenance > Security > User Permission (mnuPermission_Click: CheckAccess(112)).</summary>
    public const int Screen = 112;

    public static void MapUserMaintenance(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance/users").WithTags("Maintenance: User Permission").RequireScreen(Screen);
        g.MapGet("/options", async (IUserMaintenanceRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/", async (string? search, IUserMaintenanceRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(search, ct)));
        g.MapGet("/{code}", async (string code, IUserMaintenanceRepository repo, CancellationToken ct) =>
            await repo.GetAsync(code, ct) is { } u ? Results.Ok(u) : Results.NotFound(new { error = "User not found." }));
        g.MapPost("/", async (UserSaveRequest request, ClaimsPrincipal user, IUserMaintenanceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SaveAsync(request, true, user.UserCode(), ct)));
        g.MapPut("/{code}", async (string code, UserSaveRequest request, ClaimsPrincipal user, IUserMaintenanceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SaveAsync(request with { Code = code }, false, user.UserCode(), ct)));
        g.MapDelete("/{code}", async (string code, ClaimsPrincipal user, IUserMaintenanceRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteAsync(code, user.UserCode(), ct);
            return Results.Ok(new { deleted = code });
        });
    }
}
