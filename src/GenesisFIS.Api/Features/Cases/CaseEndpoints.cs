using System.Security.Claims;
using System.Text.RegularExpressions;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Membership;
using GenesisFIS.Reports.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static partial class CaseEndpoints
{
    /// <summary>Legacy USRAccess positions: Case Inquiry menu is CheckAccess(13); the payment print button needs CheckAccess(178).</summary>
    public const int InquiryScreen = 13;
    public const int PaymentPrintScreen = 178;

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapCases(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/cases").WithTags("Case Inquiry").RequireScreen(InquiryScreen);

        g.MapGet("/", async (string? by, string? q, string? plan, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CaseSearchBy>(by, true, out var kind)) return Results.BadRequest(new { error = "Unknown search type." });
            if (!CaseNumber.IsValidSearch(kind, q))
                return Results.BadRequest(new { error = "Enter a valid search value (at least 3 characters for member no, case no and name)." });
            if (plan is not null && !PlanCode().IsMatch(plan)) return Results.BadRequest(new { error = "Invalid plan code." });
            return Results.Ok(await repo.SearchAsync(kind, q!, plan, ct));
        });

        // Legacy cmdPrev_Click / cmdNext_Click: the previous or next case number (CASCrossReference order), as on Case Adjustment.
        g.MapGet("/{caseNumber}/next", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) =>
            await repo.NeighbourAsync(caseNumber, true, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { error = "Last Record Reached!" }));
        g.MapGet("/{caseNumber}/previous", async (string caseNumber, ICaseAdjustmentRepository repo, CancellationToken ct) =>
            await repo.NeighbourAsync(caseNumber, false, ct) is { } n ? Results.Ok(new { caseNumber = n }) : Results.NotFound(new { error = "First Record Reached!" }));

        g.MapGet("/{caseNumber}", async (string caseNumber, ICaseInquiryRepository repo, ICaseDocumentStore docs, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var detail = await repo.GetAsync(caseNumber, ct);
            if (detail is null) return Results.NotFound(new { error = "No record found!" });
            var hasDocs = docs.IsConfigured(CaseDocumentArea.Scanned) && docs.List(CaseDocumentArea.Scanned, caseNumber).Count > 0;
            return Results.Ok(detail with { HasScannedDocuments = hasDocs });
        });

        Tab(g, "guarantees", (r, c, ct) => r.GetGuaranteesAsync(c, ct));
        Tab(g, "worksheets", (r, c, ct) => r.GetWorksheetsAsync(c, ct));
        Tab(g, "invoices", (r, c, ct) => r.GetInvoicesAsync(c, ct));
        Tab(g, "payments", (r, c, ct) => r.GetPaymentsAsync(c, ct));
        Tab(g, "reminders", (r, c, ct) => r.GetRemindersAsync(c, ct));
        Tab(g, "cartons", (r, c, ct) => r.GetCartonsAsync(c, ct));
        Tab(g, "surgical-schedule", (r, c, ct) => r.GetSurgicalScheduleAsync(c, ct));
        Optional(g, "account-summary", (r, c, ct) => r.GetAccountSummaryAsync(c, ct));
        Optional(g, "repudiation", (r, c, ct) => r.GetRepudiationAsync(c, ct));
        Optional(g, "benefits", (r, c, ct) => r.GetBenefitsAsync(c, ct));
        Optional(g, "exclusions", (r, c, ct) => r.GetExclusionsAsync(c, ct));

        // Member-level tabs reuse the Membership Enquiry implementation through the case's member number.
        Member(g, "non-disclosure", (t, m, ct) => t.GetNonDisclosureAsync(m, ct));
        Member(g, "policy-terms", (t, m, ct) => t.GetPolicyTermsAsync(m, ct));
        Member(g, "lifetime-limit", (t, m, ct) => t.GetLifetimeLimitAsync(m, ct));

        g.MapGet("/{caseNumber}/documents/{area}", async (string caseNumber, string area, ICaseDocumentStore docs, CancellationToken _) =>
        {
            if (!CaseNumber.IsValid(caseNumber) || !Enum.TryParse<CaseDocumentArea>(area, true, out var a)) return Results.BadRequest(new { error = "Invalid request." });
            var configured = docs.IsConfigured(a);
            return Results.Ok(new CaseDocumentListing(a.ToString(), configured, configured ? docs.List(a, caseNumber) : []));
        });

        g.MapGet("/{caseNumber}/documents/{area}/{file}", (string caseNumber, string area, string file, ICaseDocumentStore docs) =>
        {
            if (!CaseNumber.IsValid(caseNumber) || !Enum.TryParse<CaseDocumentArea>(area, true, out var a)) return Results.BadRequest(new { error = "Invalid request." });
            var c = docs.Open(a, caseNumber, file);
            return c is null ? Results.NotFound(new { error = "Document not found." }) : Results.File(c.Content, c.ContentType, c.FileName);
        });

        g.MapGet("/{caseNumber}/policy-wording", async (string caseNumber, ICaseInquiryRepository repo, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var member = await repo.GetMemberNumberAsync(caseNumber, ct);
            if (member is null) return Results.NotFound(new { error = "No record found!" });
            var folder = await tabs.GetPolicyWordingFolderAsync(member, ct);
            return Results.Ok(new PolicyWordingListing(folder, store.IsConfigured, folder is null || !store.IsConfigured ? [] : store.List(folder)));
        });

        g.MapGet("/{caseNumber}/policy-wording/{file}", async (string caseNumber, string file, ICaseInquiryRepository repo, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var member = await repo.GetMemberNumberAsync(caseNumber, ct);
            var folder = member is null ? null : await tabs.GetPolicyWordingFolderAsync(member, ct);
            var c = folder is null ? null : store.Open(folder, file);
            return c is null ? Results.NotFound(new { error = "Document not found." }) : Results.File(c.Content, c.ContentType, c.FileName);
        });

        var print = g.MapGroup("/{caseNumber}/print");

        print.MapGet("/enquiry.xlsx", async (string caseNumber, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var detail = await repo.GetAsync(caseNumber, ct);
            return detail is null
                ? Results.NotFound(new { error = "Report cannot be printed without any record." })
                : Results.File(CaseReports.EnquiryWorkbook(detail, await repo.GetWorksheetsAsync(caseNumber, ct)), Xlsx, $"case-{caseNumber.Trim()}-enquiry.xlsx");
        });

        print.MapGet("/notes.pdf", async (string caseNumber, string? kind, ClaimsPrincipal user, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var detail = await repo.GetAsync(caseNumber, ct);
            if (detail is null) return Results.NotFound(new { error = "Report cannot be printed without any record." });
            var internalNotes = string.Equals(kind, "internal", StringComparison.OrdinalIgnoreCase);
            var r = detail.Remarks;
            var text = internalNotes ? r.Internal : string.Concat(r.Management, r.ManagementTwo, r.Page3, r.Page4, r.Page5);
            return Results.File(CaseReports.NotesPdf(caseNumber.Trim(), internalNotes, text ?? "", user.Identity?.Name ?? "unknown"), "application/pdf",
                $"case-{caseNumber.Trim()}-{(internalNotes ? "internal-notes" : "notes")}.pdf");
        });

        print.MapGet("/payments.xlsx", async (string caseNumber, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            if (await repo.GetMemberNumberAsync(caseNumber, ct) is null) return Results.NotFound(new { error = "Report cannot be printed without any record." });
            return Results.File(CaseReports.PaymentsWorkbook(caseNumber.Trim(), await repo.GetPaymentsAsync(caseNumber, ct)), Xlsx, $"case-{caseNumber.Trim()}-payments.xlsx");
        }).RequireScreen(PaymentPrintScreen);
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,10}$")]
    private static partial Regex PlanCode();

    private static void Tab<T>(RouteGroupBuilder g, string name, Func<ICaseInquiryRepository, string, CancellationToken, Task<T>> load) =>
        g.MapGet($"/{{caseNumber}}/{name}", async (string caseNumber, ICaseInquiryRepository repo, CancellationToken ct) =>
            CaseNumber.IsValid(caseNumber) ? Results.Ok(await load(repo, caseNumber, ct)) : Results.BadRequest(new { error = "Invalid case number." }));

    private static void Optional<T>(RouteGroupBuilder g, string name, Func<ICaseInquiryRepository, string, CancellationToken, Task<T?>> load) where T : class =>
        g.MapGet($"/{{caseNumber}}/{name}", async (string caseNumber, ICaseInquiryRepository repo, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var item = await load(repo, caseNumber, ct);
            return item is null ? Results.Ok((object?)null) : Results.Ok(item);
        });

    private static void Member<T>(RouteGroupBuilder g, string name, Func<IMembershipTabsRepository, string, CancellationToken, Task<IReadOnlyList<T>>> load) =>
        g.MapGet($"/{{caseNumber}}/{name}", async (string caseNumber, ICaseInquiryRepository repo, IMembershipTabsRepository tabs, CancellationToken ct) =>
        {
            if (!CaseNumber.IsValid(caseNumber)) return Results.BadRequest(new { error = "Invalid case number." });
            var member = await repo.GetMemberNumberAsync(caseNumber, ct);
            return member is null ? Results.NotFound(new { error = "No record found!" }) : Results.Ok(await load(tabs, member, ct));
        });
}
