using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class RegistrationEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Registration.</summary>
    public const int Screen = 1;

    public static void MapRegistration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/registration").WithTags("Membership: Registration").RequireScreen(Screen);

        g.MapGet("/options", async (IRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/last-batch", async (string payor, DateOnly bordxDate, IRegistrationRepository repo, CancellationToken ct) => Results.Ok(new { lastBatch = await repo.LastBatchAsync(payor, bordxDate, ct) }));
        g.MapPost("/quote", async (RegistrationRequest request, IRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.QuoteAsync(request, ct)));
        g.MapPost("/", async (RegistrationRequest request, ClaimsPrincipal user, IRegistrationRepository repo, CancellationToken ct) =>
            Results.Created("/api/membership/registration", await repo.RegisterAsync(request, user.UserCode(), ct)));
    }
}
