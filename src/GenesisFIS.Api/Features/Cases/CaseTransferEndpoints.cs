using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Reports.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class CaseTransferEndpoints
{
    /// <summary>Legacy USRAccess position of the Case menu items Case Transfer, Temp Case Transfer and Case PayClaim.</summary>
    public const int Screen = 11;

    public static void MapCaseTransfers(this IEndpointRouteBuilder app)
    {
        MapTransfer(app.MapGroup("/api/case-transfer").WithTags("Case Transfer").RequireScreen(Screen), false);
        MapTransfer(app.MapGroup("/api/case-temp-transfer").WithTags("Temp Case Transfer").RequireScreen(Screen), true);

        var p = app.MapGroup("/api/case-payclaim").WithTags("Case PayClaim").RequireScreen(Screen);
        p.MapGet("/{caseNumber}", async (string caseNumber, ICasePayClaimRepository repo, CancellationToken ct) => await repo.GetAsync(caseNumber, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No Record found !" }));
        p.MapPut("/{caseNumber}", async (string caseNumber, CasePayClaimRequest request, ClaimsPrincipal user, ICasePayClaimRepository repo, CancellationToken ct) => Results.Ok(new { updated = await repo.SaveAsync(caseNumber, request, user.UserCode(), ct) }));
        p.MapGet("/{caseNumber}/letter", async (string caseNumber, ClaimsPrincipal user, ICasePayClaimRepository repo, CancellationToken ct) =>
        {
            var v = await repo.GetAsync(caseNumber, ct);
            if (v is null) return Results.NotFound(new { message = "No Record found !" });
            return v.Record is null ? Results.BadRequest(new { error = "Save the Pay & Claim reasons first." }) : Results.File(PayClaimLetter.ToPdf(v, user.UserCode()), "application/pdf", $"payclaim-{v.CaseNumber}.pdf");
        });
    }

    private static void MapTransfer(RouteGroupBuilder g, bool temporary)
    {
        g.MapGet("/case/{caseNumber}", async (string caseNumber, ICaseTransferRepository repo, CancellationToken ct) => await repo.CurrentAsync(caseNumber, temporary, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No Record found !" }));
        g.MapGet("/member", async (string memberNumber, string coverId, ICaseTransferRepository repo, CancellationToken ct) => await repo.PartyAsync(memberNumber, coverId, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "Member not found !" }));
        g.MapPost("/{caseNumber}", async (string caseNumber, CaseTransferRequest request, ClaimsPrincipal user, ICaseTransferRepository repo, CancellationToken ct) =>
            Results.Ok(temporary ? await repo.TempTransferAsync(caseNumber, request, user.UserCode(), ct) : await repo.TransferAsync(caseNumber, request, user.UserCode(), ct)));
    }
}
