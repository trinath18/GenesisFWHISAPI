using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class CaseLifetimeAuditBordxEndpoints
{
    public const int LifetimeScreen = 11, AuditScreen = 16, BordxScreen = 154;

    public static void MapLifetimeAuditBordx(this IEndpointRouteBuilder app)
    {
        var l = app.MapGroup("/api/lifetime-limit").WithTags("Lifetime Limit").RequireScreen(LifetimeScreen);
        l.MapGet("/", async (string memberNumber, string coverId, ILifetimeLimitRepository repo, CancellationToken ct) => await repo.GetAsync(memberNumber, coverId, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No Record Found ! " }));
        l.MapPost("/", async (string memberNumber, string coverId, ClaimsPrincipal user, ILifetimeLimitRepository repo, CancellationToken ct) => Results.Ok(await repo.AllocateAsync(memberNumber, coverId, user.UserCode(), ct)));

        var a = app.MapGroup("/api/case-audit-review").WithTags("Case Audit Review").RequireScreen(AuditScreen);
        a.MapGet("/{caseNumber}", async (string caseNumber, ICaseAuditReviewRepository repo, CancellationToken ct) => await repo.GetAsync(caseNumber, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No record found!" }));
        a.MapGet("/{caseNumber}/next", async (string caseNumber, bool? reviewedOnly, ICaseAuditReviewRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, true, reviewedOnly == true, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "Last Record Reached!" }));
        a.MapGet("/{caseNumber}/previous", async (string caseNumber, ICaseAuditReviewRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, false, false, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "First Record Reached!" }));
        a.MapPut("/{caseNumber}", async (string caseNumber, CaseAuditRequest request, ClaimsPrincipal user, ICaseAuditReviewRepository repo, CancellationToken ct) => Results.Ok(new { updated = await repo.SaveAsync(caseNumber, request, user.UserCode(), ct) }));

        var b = app.MapGroup("/api/case-send-to-bordx").WithTags("Send to Bordx Unit").RequireScreen(BordxScreen);
        b.MapGet("/options", async (ICaseSendToBordxRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        b.MapPost("/search", async (BordxSearchRequest request, ICaseSendToBordxRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(request, ct)));
        b.MapPost("/pass", async (BordxPassRequest request, ClaimsPrincipal user, ICaseSendToBordxRepository repo, CancellationToken ct) => Results.Ok(await repo.PassAsync(request.Items ?? [], user.UserCode(), ct)));
        b.MapPut("/{caseNumber}/{version:int}", async (string caseNumber, int version, BordxAmendRequest request, ClaimsPrincipal user, ICaseSendToBordxRepository repo, CancellationToken ct) =>
            await repo.AmendAsync(caseNumber, version, request.Dpo, user.UserCode(), ct) ? Results.Ok(new { updated = true }) : Results.BadRequest(new { error = "Only a record already passed to the Bordx Unit can be amended." }));
    }
}

public sealed record BordxPassRequest(IReadOnlyList<BordxPassItem>? Items);
public sealed record BordxAmendRequest(DateOnly? Dpo);
