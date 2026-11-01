using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class WorksheetEndpoints
{
    /// <summary>Legacy USRAccess positions: Worksheet Enquiry opens with CheckAccess(13) (Case Inquiry); Worksheet Registration with CheckAccess(64).</summary>
    public const int EnquiryScreen = 13;
    public const int RegistrationScreen = 64;
    /// <summary>Legacy USRAccess position of Worksheet > WorkSheet Adjustment (CheckAccess(65)).</summary>
    public const int AdjustmentScreen = 65;

    public static void MapWorksheets(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/worksheet-enquiry").WithTags("Worksheet Enquiry").RequireScreen(EnquiryScreen)
            .MapGet("/{caseNumber}", async (string caseNumber, IWorksheetRepository repo, CancellationToken ct) =>
                await repo.EnquiryAsync(caseNumber, ct) is { } e ? Results.Ok(e) : Results.NotFound(new { message = "No record found!" }));

        var reg = app.MapGroup("/api/worksheet-registration").WithTags("Worksheet Registration").RequireScreen(RegistrationScreen);
        reg.MapGet("/{caseNumber}/preview", async (string caseNumber, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.PreviewAsync(caseNumber, null, ct) is { } p ? Results.Ok(p) : Results.NotFound(new { message = "No record found!" }));
        // Recalculates with the limits the claims officer typed into the grid; nothing is saved.
        reg.MapPost("/{caseNumber}/calculate", async (string caseNumber, WorksheetCalculateRequest request, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.PreviewAsync(caseNumber, request.LimitOverrides, ct) is { } p ? Results.Ok(p) : Results.NotFound(new { message = "No record found!" }));
        reg.MapPost("/{caseNumber}", async (string caseNumber, WorksheetSaveRequest request, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
        {
            var saved = await repo.SaveAsync(caseNumber, request, user.UserCode(), ct);
            return Results.Created($"/api/worksheet-enquiry/{caseNumber.Trim()}", saved);
        });

        var adj = app.MapGroup("/api/worksheet-adjustment").WithTags("Worksheet Adjustment").RequireScreen(AdjustmentScreen);
        adj.MapGet("/{caseNumber}", async (string caseNumber, int? version, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.AdjustmentAsync(caseNumber, version, null, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No worksheet found for this case." }));
        // Recalculates the version with the limits the claims officer typed; nothing is saved.
        adj.MapPost("/{caseNumber}/{version:int}/calculate", async (string caseNumber, int version, WorksheetCalculateRequest request, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.AdjustmentAsync(caseNumber, version, request.LimitOverrides, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No worksheet found for this case." }));
        adj.MapPut("/{caseNumber}/{version:int}", async (string caseNumber, int version, WorksheetSaveRequest request, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AmendAsync(caseNumber, version, request, user.UserCode(), ct)));
    }
}
