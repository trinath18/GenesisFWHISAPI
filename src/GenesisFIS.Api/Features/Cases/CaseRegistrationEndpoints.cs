using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Membership;
using GenesisFIS.Reports.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class CaseRegistrationEndpoints
{
    /// <summary>Legacy USRAccess position of Case > Case Registration.</summary>
    public const int Screen = 11;

    private static IResult? Valid(string number) => MemberNumber.IsValid(number) ? null : Results.BadRequest(new { error = "Invalid membership number." });

    /// <summary>The read-only member tabs shared by Case Registration and Case Adjustment (each under its own screen permission).</summary>
    /// <summary>Doc Req Adm, Doc Req Discharge and T.Home Drugs, printed from the form (both the registration and the adjustment form had these buttons).</summary>
    public static void MapDraftLetters(RouteGroupBuilder g) =>
        g.MapPost("/letters/{kind}", async (string kind, CaseDraftLetterRequest request, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) =>
        {
            var d = await repo.DraftLetterAsync(kind, request, user.UserCode(), ct);
            return Results.File(CaseLetters.ToPdf(d), "application/pdf", $"{kind}-{(d.CaseNumber == "-" ? d.MemberNumber : d.CaseNumber)}.pdf");
        });

    public static void MapMemberTabs(RouteGroupBuilder g)
    {
        g.MapGet("/member/{number}/extras", async (string number, ICaseRegistrationRepository repo, CancellationToken ct) => await repo.ExtrasAsync(number, ct) is { } e ? Results.Ok(e) : Results.NotFound(new { message = "No record found!" }));
        g.MapGet("/member/{number}/benefits", async (string number, IMembershipTabsRepository tabs, CancellationToken ct) => Valid(number) ?? Results.Ok(await tabs.GetBenefitsAsync(number, ct)));
        g.MapGet("/member/{number}/history", async (string number, IMembershipTabsRepository tabs, CancellationToken ct) => Valid(number) ?? Results.Ok(await tabs.GetMemberHistoryAsync(number, ct)));
        g.MapGet("/member/{number}/non-disclosure", async (string number, IMembershipTabsRepository tabs, CancellationToken ct) => Valid(number) ?? Results.Ok(await tabs.GetNonDisclosureAsync(number, ct)));
        g.MapGet("/member/{number}/policy-terms", async (string number, IMembershipTabsRepository tabs, CancellationToken ct) => Valid(number) ?? Results.Ok(await tabs.GetPolicyTermsAsync(number, ct)));
        g.MapGet("/member/{number}/policy-wording", async (string number, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (Valid(number) is { } bad) return bad;
            var folder = await tabs.GetPolicyWordingFolderAsync(number, ct);
            return Results.Ok(new PolicyWordingListing(folder, store.IsConfigured, folder is null || !store.IsConfigured ? [] : store.List(folder)));
        });
        g.MapGet("/member/{number}/policy-wording/{file}", async (string number, string file, IMembershipTabsRepository tabs, IPolicyWordingStore store, CancellationToken ct) =>
        {
            if (Valid(number) is { } bad) return bad;
            var folder = await tabs.GetPolicyWordingFolderAsync(number, ct);
            var content = folder is null ? null : store.Open(folder, file);
            return content is null ? Results.NotFound(new { error = "The document was not found." }) : Results.File(content.Content, content.ContentType, content.FileName);
        });
    }

    public static void MapCaseRegistration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-registration").WithTags("Case Registration").RequireScreen(Screen);
        g.MapGet("/options", async (ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        g.MapGet("/member", async (string memberNumber, ICaseRegistrationRepository repo, CancellationToken ct) =>
            await repo.MemberAsync(memberNumber, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { message = "No record found!" }));
        MapMemberTabs(g);
        g.MapPost("/{caseNumber}/lg", async (string caseNumber, CaseLgRequest request, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Created($"/api/case-registration/{caseNumber}/letters/{CaseLetterKinds.Lg}", await repo.IssueLgAsync(caseNumber, request, user.UserCode(), ct)));
        MapDraftLetters(g);
        g.MapGet("/{caseNumber}/letters/{kind}", async (string caseNumber, string kind, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) =>
            await repo.LetterAsync(caseNumber, kind, user.UserCode(), ct) is { } d ? Results.File(CaseLetters.ToPdf(d), "application/pdf", $"{kind}-{d.CaseNumber}.pdf") : Results.NotFound(new { error = "The case does not exist." }));
        g.MapPost("/", async (CaseRegistrationRequest request, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) =>
        {
            var result = await repo.RegisterAsync(request, user.UserCode(), ct);
            return Results.Created("/api/cases/" + result.CaseNumber, result);
        });
    }
}




