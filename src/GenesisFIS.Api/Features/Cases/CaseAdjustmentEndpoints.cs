using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Reports.Cases;
using System.Text.RegularExpressions;

namespace GenesisFIS.Api.Features.Cases;

public static class CaseAdjustmentEndpoints
{
    /// <summary>Legacy USRAccess position of Case > Case Adjustment.</summary>
    public const int Screen = 12;

    public static void MapCaseAdjustment(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-adjustment").WithTags("Case Adjustment").RequireScreen(Screen);
        g.MapGet("/options", async (ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        CaseRegistrationEndpoints.MapMemberTabs(g);
        g.MapGet("/search", async (string? by, string? q, string? plan, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CaseSearchBy>(by, true, out var kind)) return Results.BadRequest(new { error = "Unknown search type." });
            if (!CaseNumber.IsValidSearch(kind, q)) return Results.BadRequest(new { error = "Search String must be at least 3 characters!" });
            if (plan is not null && !Regex.IsMatch(plan, "^[A-Za-z0-9]{1,10}$")) return Results.BadRequest(new { error = "Invalid plan code." });
            return Results.Ok(await repo.SearchAsync(kind, q!, plan, ct));
        });
        g.MapGet("/{caseNumber}/lg", async (string caseNumber, ICaseAdjustmentTabsRepository repo, CancellationToken ct) => Results.Ok(await repo.LgListAsync(caseNumber, ct)));
        g.MapPost("/{caseNumber}/lg", async (string caseNumber, CaseLgRequest request, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Created($"/api/case-adjustment/{caseNumber}/lg", await repo.IssueLgAsync(caseNumber, request, user.UserCode(), ct)));
        g.MapPut("/{caseNumber}/lg/{lgNo:int}/remark", async (string caseNumber, int lgNo, CaseLgRemarkRequest request, ClaimsPrincipal user, ICaseAdjustmentTabsRepository repo, CancellationToken ct) =>
            await repo.UpdateLgRemarkAsync(caseNumber, lgNo, request.Remark, user.UserCode(), ct) ? Results.Ok(new { updated = true }) : Results.NotFound(new { message = "The guarantee letter does not exist." }));
        g.MapGet("/{caseNumber}/lg/{lgNo:int}/letter", async (string caseNumber, int lgNo, ClaimsPrincipal user, ICaseRegistrationRepository repo, ICaseAdjustmentTabsRepository tabs, CancellationToken ct) =>
        {
            var kind = (await tabs.LgListAsync(caseNumber, ct)).FirstOrDefault(x => x.LgNo == lgNo)?.Letter == "B" ? CaseLetterKinds.Btbg : CaseLetterKinds.Lg;
            return await repo.LetterAsync(caseNumber, kind, user.UserCode(), ct, lgNo) is { } d ? Results.File(CaseLetters.ToPdf(d), "application/pdf", $"{kind}-{d.CaseNumber}-{lgNo}.pdf") : Results.NotFound(new { error = "The case does not exist." });
        });
        g.MapGet("/{caseNumber}/worksheets", async (string caseNumber, ICaseAdjustmentTabsRepository repo, CancellationToken ct) => Results.Ok(await repo.WorksheetsAsync(caseNumber, ct)));
        g.MapPut("/{caseNumber}/worksheets/{version:int}/approval", async (string caseNumber, int version, CaseWorksheetApprovalRequest request, ClaimsPrincipal user, ICaseAdjustmentTabsRepository repo, CancellationToken ct) =>
            await repo.UpdateWorksheetApprovalAsync(caseNumber, version, request, user.UserCode(), ct) ? Results.Ok(new { updated = true }) : Results.NotFound(new { message = "The worksheet version does not exist." }));        g.MapGet("/{caseNumber}", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.GetAsync(caseNumber, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "No record found!" }));
        g.MapGet("/{caseNumber}/next", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, true, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "Last Record Reached!" }));
        g.MapGet("/{caseNumber}/previous", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) => await repo.NeighbourAsync(caseNumber, false, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { message = "First Record Reached!" }));
        g.MapPut("/{caseNumber}", async (string caseNumber, CaseAdjustmentRequest request, ClaimsPrincipal user, ICaseAdjustmentRepository repo, CancellationToken ct) => { await repo.UpdateAsync(caseNumber, request, user.UserCode(), ct); return Results.Ok(new { updated = true }); });
    }
}


