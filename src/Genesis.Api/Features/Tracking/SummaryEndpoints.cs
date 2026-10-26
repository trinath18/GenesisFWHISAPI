using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Tracking;
using Genesis.Reports.Tracking;

namespace Genesis.Api.Features.Tracking;

public static class SummaryEndpoints
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapSummaries(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/summary").WithTags("Summary reports").RequireAuthorization();

        g.MapGet("/", (ClaimsPrincipal user) => Results.Ok(SummaryCatalog.All.Where(d => user.CanAccess(d.ScreenPosition))
            .Select(d => new TrackingDefinitionSummary(d.Id, d.Title, d.Module, d.ScreenPosition, d.Description))));

        g.MapGet("/{id}", (string id, ClaimsPrincipal user) =>
        {
            var def = SummaryCatalog.Find(id);
            if (def is null) return Results.NotFound(new { error = "Unknown report." });
            if (!user.CanAccess(def.ScreenPosition)) return Results.Forbid();
            return Results.Ok(new
            {
                def.Id, def.Title, def.Module, def.Description, def.RequiresFilter, maxDimensions = SummaryDefinition.MaxDimensions,
                filters = def.Filters.Select(f => new { f.Key, f.Label, kind = f.Kind.ToString(), choices = f.Choices }),
                dimensions = def.Dimensions.Select(d => new { d.Key, d.Header }),
                measures = def.Measures.Select(m => new { m.Key, m.Header, type = m.Type.ToString().ToLowerInvariant() }),
                def.DefaultDimensions, def.DefaultMeasures,
            });
        });

        g.MapGet("/{id}/run", (string id, HttpRequest request, ClaimsPrincipal user, ISummaryService service, CancellationToken ct) =>
            Run(id, request, user, async (def, req, filters) => Results.Ok(await service.RunAsync(def, req, filters, ct))));

        g.MapGet("/{id}/export.xlsx", (string id, HttpRequest request, ClaimsPrincipal user, ISummaryService service, CancellationToken ct) =>
            Run(id, request, user, async (def, req, filters) =>
            {
                var result = await service.RunAsync(def, req, filters, ct);
                return Results.File(TrackingWorkbook.ToExcel(def.Title, result, filters, user.Identity?.Name ?? "unknown"), Xlsx, $"{def.Id}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
            }));
    }

    /// <summary>Query string: <c>dims=a,b</c>, <c>measures=x,y</c>, optional <c>top=N</c>, and <c>f.&lt;key&gt;</c> filters.</summary>
    private static async Task<IResult> Run(string id, HttpRequest request, ClaimsPrincipal user,
        Func<SummaryDefinition, SummaryRequest, Dictionary<string, string>, Task<IResult>> action)
    {
        var def = SummaryCatalog.Find(id);
        if (def is null) return Results.NotFound(new { error = "Unknown report." });
        if (!user.CanAccess(def.ScreenPosition)) return Results.Forbid();

        // A parameter that is absent means "defaults"; present but empty means "none" (e.g. dims= for a grand total).
        static string[]? List(HttpRequest r, string key) =>
            !r.Query.ContainsKey(key) ? null : r.Query[key].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? top = int.TryParse(request.Query["top"], out var t) ? t : null;
        var summary = new SummaryRequest(List(request, "dims"), List(request, "measures"), top);
        var filters = request.Query.Where(q => q.Key.StartsWith("f.", StringComparison.Ordinal))
            .ToDictionary(q => q.Key[2..], q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        try
        {
            return await action(def, summary, filters);
        }
        catch (TrackingFilterException ex)
        {
            return Results.BadRequest(new { error = "Some options are not valid.", errors = ex.Errors });
        }
    }
}

