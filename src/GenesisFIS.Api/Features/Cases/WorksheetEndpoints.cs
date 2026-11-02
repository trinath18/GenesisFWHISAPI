using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Reports.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class WorksheetEndpoints
{
    /// <summary>Legacy USRAccess positions: Worksheet Enquiry opens with CheckAccess(13) (Case Inquiry); Worksheet Registration with CheckAccess(64).</summary>
    public const int EnquiryScreen = 13;
    public const int RegistrationScreen = 64;
    /// <summary>Legacy USRAccess position of Worksheet > WorkSheet Adjustment (CheckAccess(65)).</summary>
    public const int AdjustmentScreen = 65;

    public static void MapWorksheets(this IEndpointRouteBuilder app)
    {
        var enq = app.MapGroup("/api/worksheet-enquiry").WithTags("Worksheet Enquiry").RequireScreen(EnquiryScreen);
        enq.MapGet("/{caseNumber}", async (string caseNumber, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.EnquiryAsync(caseNumber, ct) is { } e ? Results.Ok(e) : Results.NotFound(new { message = "No record found!" }));
        enq.MapDocuments();

        var reg = app.MapGroup("/api/worksheet-registration").WithTags("Worksheet Registration").RequireScreen(RegistrationScreen);
        reg.MapGet("/{caseNumber}/preview", async (string caseNumber, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.PreviewAsync(caseNumber, null, ct) is { } p ? Results.Ok(p) : Results.NotFound(new { message = "No record found!" }));
        // Recalculates with the limits the claims officer typed into the grid; nothing is saved.
        reg.MapPost("/{caseNumber}/calculate", async (string caseNumber, WorksheetCalculateRequest request, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.PreviewAsync(caseNumber, request.LimitOverrides, ct) is { } p ? Results.Ok(p) : Results.NotFound(new { message = "No record found!" }));
        reg.MapPost("/{caseNumber}", async (string caseNumber, WorksheetSaveRequest request, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
        {
            var saved = await repo.SaveAsync(caseNumber, request, user.UserCode(), ct);
            return Results.Created($"/api/worksheet-enquiry/{caseNumber.Trim()}", saved);
        });
        reg.MapDocuments();

        var adj = app.MapGroup("/api/worksheet-adjustment").WithTags("Worksheet Adjustment").RequireScreen(AdjustmentScreen);
        adj.MapGet("/{caseNumber}", async (string caseNumber, int? version, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.AdjustmentAsync(caseNumber, version, null, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No worksheet found for this case." }));
        // Recalculates the version with the limits the claims officer typed; nothing is saved.
        adj.MapPost("/{caseNumber}/{version:int}/calculate", async (string caseNumber, int version, WorksheetCalculateRequest request, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.AdjustmentAsync(caseNumber, version, request.LimitOverrides, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No worksheet found for this case." }));
        adj.MapPut("/{caseNumber}/{version:int}", async (string caseNumber, int version, WorksheetSaveRequest request, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AmendAsync(caseNumber, version, request, user.UserCode(), ct)));
        adj.MapDocuments();
    }

    /// <summary>
    /// The documents of a saved version (the CN/DN Note, Exc Letter, Chq Letter and Export buttons the legacy enquiry, registration and adjustment forms all had),
    /// under each screen's own access: which ones apply, then each one as a PDF (letters) or an Excel workbook (export).
    /// </summary>
    private static void MapDocuments(this RouteGroupBuilder g)
    {
        // The drop-down lists and the "Invoices & Diagnosis" / "Reimbursement Not Cover" tabs of the legacy forms.
        g.MapGet("/options", async (IWorksheetRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/{caseNumber}/{version:int}/details", async (string caseNumber, int version, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.DetailAsync(caseNumber, version, ct) is { } d ? Results.Ok(d) : Results.NotFound(new { message = "No record found!" }));
        g.MapGet("/{caseNumber}/{version:int}/documents", async (string caseNumber, int version, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
            await repo.DocumentAsync(caseNumber, version, user.UserCode(), ct) is { } d
                ? Results.Ok(new { available = d.Available, debitCreditNote = d.Version.DebitCreditRefNo?.Trim(), excessNumber = d.Excess?.Number, noteAmount = d.NoteAmount })
                : Results.NotFound(new { message = "No worksheet found for this case." }));
        g.MapGet("/{caseNumber}/{version:int}/documents/{kind}", async (string caseNumber, int version, string kind, ClaimsPrincipal user, IWorksheetRepository repo, CancellationToken ct) =>
        {
            if (await repo.DocumentAsync(caseNumber, version, user.UserCode(), ct) is not { } d) return Results.NotFound(new { message = "No worksheet found for this case." });
            if (!d.Available.Contains(kind)) return Results.NotFound(new { message = "This document does not apply to this worksheet." });
            var name = $"{kind}-{d.Case.CaseNumber}-{version}";
            return kind == WorksheetDocumentKinds.Export
                ? Results.File(WorksheetDocuments.Export(d), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx")
                : Results.File(WorksheetDocuments.ToPdf(kind, d), "application/pdf", name + ".pdf");
        });
    }
}
