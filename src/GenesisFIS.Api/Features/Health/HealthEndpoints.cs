using Dapper;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Api.Features.Health;

public static class HealthEndpoints
{
    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (IDbConnectionFactory db, CancellationToken ct) =>
        {
            var results = new Dictionary<string, string>();
            foreach (var name in Enum.GetValues<GenesisDb>())
            {
                try
                {
                    await using var conn = await db.OpenAsync(name, ct);
                    results[name.ToString()] = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT DB_NAME()", cancellationToken: ct)) ?? "ok";
                }
                catch (Exception ex)
                {
                    results[name.ToString()] = "ERROR: " + ex.GetType().Name;
                }
            }
            var healthy = results.Values.All(v => !v.StartsWith("ERROR"));
            return Results.Json(new { status = healthy ? "Healthy" : "Degraded", databases = results },
                statusCode: healthy ? 200 : 503);
        }).WithTags("Health").AllowAnonymous();
    }
}
