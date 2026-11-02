using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class CaseInvoiceEndpoints
{
    /// <summary>Legacy USRAccess position of Invoice > Invoice Processing (CheckAccess(22), "Invoice Entry").</summary>
    public const int Screen = 22;

    public static void MapCaseInvoices(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-invoices").WithTags("Invoice Processing").RequireScreen(Screen);
        g.MapGet("/options", async (ICaseInvoiceRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        // The procedure entry: OPCS / investigation search and the MMA rates of a code.
        g.MapGet("/mma", async (string? code, string? narrative, bool? investigations, ICaseInvoiceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.MmaCodesAsync(code, narrative, investigations ?? false, ct)));
        g.MapGet("/mma/{code}", async (string code, ICaseInvoiceRepository repo, CancellationToken ct) =>
            await repo.MmaRateAsync(code, ct) is { } rate ? Results.Ok(rate) : Results.NotFound(new { message = "No MMA rate for this code." }));
        g.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        g.MapGet("/{caseNumber}", async (string caseNumber, ICaseInvoiceRepository repo, CancellationToken ct) =>
            await repo.GetAsync(caseNumber, ct) is { } screen ? Results.Ok(screen) : Results.NotFound(new { message = "No record found!" }));
        g.MapPost("/{caseNumber}", async (string caseNumber, CaseInvoiceRequest request, ClaimsPrincipal user, ICaseInvoiceRepository repo, CancellationToken ct) =>
        {
            var created = await repo.CreateAsync(caseNumber, request, user.UserCode(), ct);
            return Results.Created($"/api/case-invoices/{caseNumber.Trim()}/{created.Index}", created);
        });
        g.MapPut("/{caseNumber}/{index:int}", async (string caseNumber, int index, CaseInvoiceRequest request, ClaimsPrincipal user, ICaseInvoiceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UpdateAsync(caseNumber, index, request, user.UserCode(), ct)));
        g.MapDelete("/{caseNumber}/{index:int}", async (string caseNumber, int index, ClaimsPrincipal user, ICaseInvoiceRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteAsync(caseNumber, index, user.UserCode(), ct);
            return Results.Ok(new { deleted = index });
        });
        g.MapPut("/{caseNumber}/remarks", async (string caseNumber, CaseInvoiceRemarksRequest request, ClaimsPrincipal user, ICaseInvoiceRepository repo, CancellationToken ct) =>
        {
            await repo.SaveRemarksAsync(caseNumber, request.Remarks, user.UserCode(), ct);
            return Results.Ok(new { updated = true });
        });
    }
}
