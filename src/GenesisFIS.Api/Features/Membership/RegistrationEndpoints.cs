using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class RegistrationEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Registration.</summary>
    public const int Screen = 1;

    public static void MapRegistration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/registration").WithTags("Membership: Registration").RequireScreen(Screen);

        g.MapGet("/options", async (IRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));

        // Code lists of a member's details (city, state, race, nationality, relationship), read by every screen that edits them, so any signed-in user.
        app.MapGet("/api/membership/lists", async (IRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.ListsAsync(ct)))
            .WithTags("Membership: Registration").RequireAuthorization();
        g.MapGet("/last-batch", async (string payor, DateOnly bordxDate, IRegistrationRepository repo, CancellationToken ct) => Results.Ok(new { lastBatch = await repo.LastBatchAsync(payor, bordxDate, ct) }));
        g.MapPost("/quote", async (RegistrationRequest request, IRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/", async (RegistrationRequest request, ClaimsPrincipal user, IRegistrationRepository repo, CancellationToken ct) =>
            Results.Created("/api/membership/registration", await repo.RegisterAsync(request, user.UserCode(), ct)));
    }
}
