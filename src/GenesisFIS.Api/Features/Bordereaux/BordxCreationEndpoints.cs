using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Bordereaux;

namespace GenesisFIS.Api.Features.Bordereaux;

public static class BordxCreationEndpoints
{
    /// <summary>Legacy USRAccess position of Claim Bordx > Worksheet > Insert Worksheet (mnuInsertWorksheet_Click: CheckAccess(30)).</summary>
    public const int Screen = 30;

    /// <summary>Legacy USRAccess position of Claim Bordx > Worksheet > Remove Worksheet (mnuBordxRevise_Click: CheckAccess(31)).</summary>
    public const int ReviseScreen = 31;

    public static void MapBordxCreation(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/bordx/insert-worksheet").WithTags("Claim Bordx: Insert Worksheet").RequireScreen(Screen);
        g.MapPost("/pending", async (PendingBordxSearch search, IBordxCreationRepository repo, CancellationToken ct) => Results.Ok(await repo.PendingAsync(search, ct)));
        g.MapPost("/", async (CreateBordxRequest request, ClaimsPrincipal user, IBordxCreationRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.CreateAsync(request, user.UserCode(), ct)));

        var revise = app.MapGroup("/api/bordx/remove-worksheet").WithTags("Claim Bordx: Remove Worksheet").RequireScreen(ReviseScreen);
        revise.MapGet("/{bordxNo}", async (string bordxNo, IBordxCreationRepository repo, CancellationToken ct) =>
            await repo.ReviseAsync(bordxNo, ct) is { } bordx ? Results.Ok(bordx) : Results.NotFound(new { error = "No record found!" }));
        revise.MapPost("/{bordxNo}/remove", async (string bordxNo, RemoveFromBordxRequest request, ClaimsPrincipal user, IBordxCreationRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.RemoveAsync(bordxNo, request, user.UserCode(), ct)));
    }
}
