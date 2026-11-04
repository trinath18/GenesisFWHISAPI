using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class CreditDebitNoteEndpoints
{
    /// <summary>Legacy USRAccess position of Worksheet > Generate Credit/Debit Note (mnuCreditDebitNote_Click: CheckAccess(65)).</summary>
    public const int Screen = 65;

    public static void MapCreditDebitNotes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/worksheets/credit-debit-note").WithTags("Worksheet: Credit/Debit Note").RequireScreen(Screen);
        g.MapGet("/missing", async (ICreditDebitNoteRepository repo, CancellationToken ct) => Results.Ok(await repo.MissingAsync(ct)));
        g.MapGet("/{caseNumber}/{version:int}", async (string caseNumber, int version, ICreditDebitNoteRepository repo, CancellationToken ct) =>
            await repo.FindAsync(caseNumber, version, ct) is { } note ? Results.Ok(note) : Results.NotFound(new { error = "Record Not Found" }));
        g.MapPost("/{caseNumber}/{version:int}", async (string caseNumber, int version, ClaimsPrincipal user, ICreditDebitNoteRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GenerateAsync(caseNumber, version, user.UserCode(), ct)));
    }
}
