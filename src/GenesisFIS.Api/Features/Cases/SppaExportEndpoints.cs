using System.Globalization;
using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Writes;

namespace GenesisFIS.Api.Features.Cases;

public static class SppaExportEndpoints
{
    /// <summary>Case > Claims ASCII (To SPPA) and Claims ASCII Pymt (To SPPA) are checked against the Case ASCII position (CheckAccess(18)).</summary>
    public const int Screen = CaseMenuEndpoints.AsciiScreen;

    public static void MapSppaExport(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-ascii/sppa").WithTags("Claim / Case ASCII").RequireScreen(Screen);
        // The batch's files as one zip; batch and totals travel in headers (exposed to the browser in Program's CORS policy).
        g.MapPost("/", async (SppaExportRequest request, HttpContext http, ClaimsPrincipal user, ISppaClaimExportRepository repo, CancellationToken ct) =>
        {
            var export = await repo.ExportAsync(request, user.UserCode(), ct) ?? throw BusinessRuleException.NotFound("No record found!");
            var t = export.Totals;
            http.Response.Headers["X-Batch"] = export.Batch;
            http.Response.Headers["X-Records"] = t.Bills.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-Totals"] = string.Join(",", t.Bills, t.ClaimAmount.ToString("0.00", CultureInfo.InvariantCulture), t.BenefitLines, t.PaymentHeaders, t.PaymentDetails,
                t.Admissions, t.Notes, t.PaymentSlips, t.Requests);
            http.Response.Headers["X-Files"] = string.Join(",", export.Files.Select(f => f.Name));
            return Results.File(SppaZip.Create(export), "application/zip", $"SPPA-{export.Batch}.zip");
        });
    }
}
