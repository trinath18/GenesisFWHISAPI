using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Maintenance;
using Genesis.Core.Tracking;
using Genesis.Core.Writes;

namespace Genesis.Api.Features.Maintenance;

public static class LookupEndpoints
{
    /// <summary>Legacy USRAccess position of Maintenance > Date.</summary>
    public const int ValidDatesScreen = 109;

    public static void MapLookups(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/lookups").WithTags("Maintenance: code tables").RequireAuthorization();

        g.MapGet("/", (ClaimsPrincipal user) => Results.Ok(LookupCatalog.All.Where(d => user.CanAccess(d.ScreenPosition))
            .Select(d => new TrackingDefinitionSummary(d.Id, d.Title, d.Module, d.ScreenPosition, null))));

        g.MapGet("/{id}", (string id, ClaimsPrincipal user) => With(id, user, def => Results.Ok(new
        {
            def.Id, def.Title, def.Module, def.AllowDelete,
            fields = def.Fields.Select(f => new { f.Key, f.Label, f.MaxLength, f.Required, f.IsKey }),
        })));

        g.MapGet("/{id}/rows", (string id, string? q, ClaimsPrincipal user, ILookupRepository repo, CancellationToken ct) =>
            With(id, user, async def => Results.Ok(await repo.ListAsync(def, q, ct))));

        g.MapPost("/{id}/rows", (string id, Dictionary<string, string?> values, ClaimsPrincipal user, ILookupRepository repo, CancellationToken ct) =>
            With(id, user, async def => Results.Created($"/api/lookups/{def.Id}/rows", await repo.CreateAsync(def, values, user.UserCode(), ct))));

        // The key travels in the query string: codes can contain spaces and punctuation.
        g.MapPut("/{id}/rows", (string id, string key, Dictionary<string, string?> values, ClaimsPrincipal user, ILookupRepository repo, CancellationToken ct) =>
            With(id, user, async def => Results.Ok(await repo.UpdateAsync(def, key, values, user.UserCode(), ct))));

        g.MapDelete("/{id}/rows", (string id, string key, ClaimsPrincipal user, ILookupRepository repo, CancellationToken ct) =>
            With(id, user, async def => { await repo.DeleteAsync(def, key, user.UserCode(), ct); return Results.Ok(new { deleted = key }); }));

        var dates = app.MapGroup("/api/maintenance/valid-dates").WithTags("Maintenance: Date").RequireScreen(ValidDatesScreen);
        dates.MapGet("/", async (IValidDateRangeRepository repo, CancellationToken ct) => Results.Ok(await repo.GetAsync(ct)));
        dates.MapPut("/", async (ValidDateRanges ranges, ClaimsPrincipal user, IValidDateRangeRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SaveAsync(ranges, user.UserCode(), ct)));
    }

    private static IResult With(string id, ClaimsPrincipal user, Func<LookupDefinition, IResult> action) =>
        Resolve(id, user, out var def) is { } denied ? denied : action(def!);

    private static async Task<IResult> With(string id, ClaimsPrincipal user, Func<LookupDefinition, Task<IResult>> action) =>
        Resolve(id, user, out var def) is { } denied ? denied : await action(def!);

    private static IResult? Resolve(string id, ClaimsPrincipal user, out LookupDefinition? def)
    {
        def = LookupCatalog.Find(id);
        if (def is null) return Results.NotFound(new { error = "Unknown screen." });
        return user.CanAccess(def.ScreenPosition) ? null : Results.Forbid();
    }
}
