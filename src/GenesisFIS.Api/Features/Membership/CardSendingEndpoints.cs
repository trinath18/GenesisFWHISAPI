using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class CardSendingEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Card Sending Mode.</summary>
    public const int Screen = 1;

    public static void MapCardSending(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/card-sending").WithTags("Membership: Card Sending Mode").RequireScreen(Screen);

        g.MapGet("/modes", () => Results.Ok(CardSendingModes.All));

        g.MapGet("/", async (string? payor, string? memberFrom, string? memberTo, DateOnly? bordxFrom, DateOnly? bordxTo, int? batchFrom, int? batchTo,
            DateOnly? sentFrom, DateOnly? sentTo, string? mode, ICardSendingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchAsync(new CardSendingCriteria(payor, memberFrom, memberTo, bordxFrom, bordxTo, batchFrom, batchTo, sentFrom, sentTo, mode), ct)));

        g.MapPut("/", async (CardSendingUpdate update, ClaimsPrincipal user, ICardSendingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UpdateAsync(update, user.UserCode(), ct)));
    }
}
