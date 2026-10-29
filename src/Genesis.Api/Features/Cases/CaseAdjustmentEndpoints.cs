using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Cases;

namespace Genesis.Api.Features.Cases;

public static class CaseAdjustmentEndpoints
{
    /// <summary>Legacy USRAccess position of Case > Case Adjustment.</summary>
    public const int Screen = 12;

    public static void MapCaseAdjustment(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-adjustment").WithTags("Case Adjustment").RequireScreen(Screen);
        g.MapGet("/options", async (ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        CaseRegistrationEndpoints.MapMemberTabs(g);
        g.MapGet("/{caseNumber}", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.GetAsync(caseNumber, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No record found!" }));
        g.MapGet("/{caseNumber}/next", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, true, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "Last Record Reached!" }));
        g.MapGet("/{caseNumber}/previous", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, false, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "First Record Reached!" }));
        g.MapPut("/{caseNumber}", async (string caseNumber, CaseAdjustmentRequest request, ClaimsPrincipal user, ICaseAdjustmentRepository repo, CancellationToken ct) => { await repo.UpdateAsync(caseNumber, request, user.UserCode(), ct); return Results.Ok(new { updated = true }); });
    }
}

