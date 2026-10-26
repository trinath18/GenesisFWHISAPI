using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Security;
using Genesis.Core.Tracking;
using Genesis.Reports.Tracking;

namespace Genesis.Api.Features.Tracking;

public static class TrackingEndpoints
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapTracking(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tracking").WithTags("Tracking").RequireAuthorization();

        // Menu: only the screens the user may open (same USRAccess rule as the legacy menu).
        g.MapGet("/", (ClaimsPrincipal user) => Results.Ok(TrackingCatalog.All.Where(d => user.CanAccess(d.ScreenPosition))
            .Select(d => new TrackingDefinitionSummary(d.Id, d.Title, d.Module, d.ScreenPosition, d.Description))));

        g.MapGet("/{id}", (string id, ClaimsPrincipal user) =>
        {
            var def = TrackingCatalog.Find(id);
            if (def is null) return Results.NotFound(new { error = "Unknown screen." });
            if (!user.CanAccess(def.ScreenPosition)) return Results.Forbid();
            return Results.Ok(new
            {
                def.Id, def.Title, def.Module, def.Description, def.RequiresFilter, maxRows = TrackingDefinition.DefaultMaxRows,
                filters = def.Filters.Select(f => new { f.Key, f.Label, kind = f.Kind.ToString(), choices = f.Choices }),
                columns = def.Columns.Select(c => new { key = c.Column, c.Header, type = c.Type.ToString().ToLowerInvariant() }),
            });
        });

        g.MapGet("/{id}/search", (string id, HttpRequest request, ClaimsPrincipal user, ITrackingService service, CancellationToken ct) =>
            Run(id, request, user, TrackingDefinition.DefaultMaxRows, async (def, filters) => Results.Ok(await service.SearchAsync(def, filters, TrackingDefinition.DefaultMaxRows, ct))));

        g.MapGet("/{id}/export.xlsx", (string id, HttpRequest request, ClaimsPrincipal user, ITrackingService service, CancellationToken ct) =>
            Run(id, request, user, TrackingDefinition.ExportMaxRows, async (def, filters) =>
            {
                var result = await service.SearchAsync(def, filters, TrackingDefinition.ExportMaxRows, ct);
                return Results.File(TrackingWorkbook.ToExcel(def, result, filters, user.Identity?.Name ?? "unknown"), Xlsx, $"{def.Id}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
            }));
    }

    /// <summary>Resolves the definition, enforces the legacy screen permission, reads "f.&lt;key&gt;" query values and maps filter errors to 400.</summary>
    private static async Task<IResult> Run(string id, HttpRequest request, ClaimsPrincipal user, int _, Func<TrackingDefinition, Dictionary<string, string>, Task<IResult>> action)
    {
        var def = TrackingCatalog.Find(id);
        if (def is null) return Results.NotFound(new { error = "Unknown screen." });
        if (!user.CanAccess(def.ScreenPosition)) return Results.Forbid();
        var filters = request.Query.Where(q => q.Key.StartsWith("f.", StringComparison.Ordinal))
            .ToDictionary(q => q.Key[2..], q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        try
        {
            return await action(def, filters);
        }
        catch (TrackingFilterException ex)
        {
            return Results.BadRequest(new { error = "Some filters are not valid.", errors = ex.Errors });
        }
    }
}

