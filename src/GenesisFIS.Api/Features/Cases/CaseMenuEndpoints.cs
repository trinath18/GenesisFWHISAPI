using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Cases;

/// <summary>The remaining Case menu screens: Non-Disclousre Info, Send to Claims (Rep), CRU Send to Claims, Claim ASCII and Case ASCII.</summary>
public static class CaseMenuEndpoints
{
    /// <summary>Legacy USRAccess positions: Non-Disclousre Info shares 11 with the other case entry screens; the send-to screens use 154 and the ASCII screens 18.</summary>
    public const int NonDisclosureScreen = 11, SendToScreen = 154, AsciiScreen = 18;

    public static void MapCaseMenu(this IEndpointRouteBuilder app)
    {
        var nd = app.MapGroup("/api/case-non-disclosure").WithTags("Non-Disclosure Info").RequireScreen(NonDisclosureScreen);
        // The legacy Search button opened the member search (frmSearchMBM05); the same search, without needing the Enquiry permission.
        nd.MapGet("/search", async (string? q, IMembershipEnquiryRepository repo, CancellationToken ct) =>
            MemberNumber.IsValidSearch(q) ? Results.Ok(await repo.SearchAsync(q!, ct)) : Results.BadRequest(new { error = $"Enter at least {MemberNumber.MinSearchLength} characters (letters, digits, * and -)." }));
        nd.MapGet("/{memberNumber}", async (string memberNumber, ICaseNonDisclosureRepository repo, CancellationToken ct) =>
            await repo.GetAsync(memberNumber, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { error = "No record found!" }));
        nd.MapGet("/{memberNumber}/next", async (string memberNumber, ICaseNonDisclosureRepository repo, CancellationToken ct) =>
            await repo.NeighbourAsync(memberNumber, true, ct) is { } n ? Results.Ok(new { memberNumber = n }) : Results.NotFound(new { error = "Last Record Reached! " }));
        nd.MapGet("/{memberNumber}/previous", async (string memberNumber, ICaseNonDisclosureRepository repo, CancellationToken ct) =>
            await repo.NeighbourAsync(memberNumber, false, ct) is { } n ? Results.Ok(new { memberNumber = n }) : Results.NotFound(new { error = "First Record Reached! " }));
        nd.MapPut("/{memberNumber}/{coverId}", async (string memberNumber, string coverId, NonDisclosureRequest request, ClaimsPrincipal user, ICaseNonDisclosureRepository repo, CancellationToken ct) =>
        {
            await repo.UpdateAsync(memberNumber, coverId, request, user.UserCode(), ct);
            return Results.Ok(new { message = "Record Updated Successfully" });
        });

        var rep = app.MapGroup("/api/case-send-to-claims-rep").WithTags("Send to Claims (Rep)").RequireScreen(SendToScreen);
        rep.MapGet("/passed-by", async (ICaseSendToClaimsRepRepository repo, CancellationToken ct) => Results.Ok(await repo.PassedByAsync(ct)));
        rep.MapPost("/search", async (SendToClaimsSearch request, ICaseSendToClaimsRepRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(request, ct)));
        rep.MapPost("/pass", async (RepPassRequest request, ClaimsPrincipal user, ICaseSendToClaimsRepRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PassAsync(request.CaseNumbers ?? [], user.UserCode(), ct)));

        var cru = app.MapGroup("/api/case-cru-send-to-claims").WithTags("CRU Send to Claims").RequireScreen(SendToScreen);
        cru.MapGet("/passed-by", async (ICaseCruSendToClaimsRepository repo, CancellationToken ct) => Results.Ok(await repo.PassedByAsync(ct)));
        cru.MapPost("/search", async (SendToClaimsSearch request, ICaseCruSendToClaimsRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(request, ct)));
        cru.MapPost("/pass", async (CruPassRequest request, ClaimsPrincipal user, ICaseCruSendToClaimsRepository repo, CancellationToken ct) =>
            request.PassOutDate is { } d ? Results.Ok(await repo.PassAsync(request.Items ?? [], d, user.UserCode(), ct)) : Results.BadRequest(new { error = "Please Enter in date", field = "passOutDate" }));

        var ascii = app.MapGroup("/api/case-ascii").WithTags("Claim / Case ASCII").RequireScreen(AsciiScreen);
        ascii.MapGet("/payors", async (ICaseAsciiRepository repo, CancellationToken ct) => Results.Ok(await repo.PayorsAsync(ct)));
        ascii.MapPost("/claim", async (ClaimAsciiRequest request, HttpContext http, ClaimsPrincipal user, ICaseAsciiRepository repo, CancellationToken ct) => AsciiDownload(http, await repo.ClaimAsync(request, user.UserCode(), ct)));
        ascii.MapPost("/case", async (CaseAsciiRequest request, HttpContext http, ClaimsPrincipal user, ICaseAsciiRepository repo, CancellationToken ct) => AsciiDownload(http, await repo.CaseAsync(request, user.UserCode(), ct)));
    }

    /// <summary>The generated file as a download; record count, batch and server copy travel in headers (exposed to the browser in Program's CORS policy).</summary>
    private static IResult AsciiDownload(HttpContext http, AsciiExport export)
    {
        http.Response.Headers["X-Records"] = export.File.Records.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Batch"] = export.File.Batch;
        if (export.SavedTo is { } saved) http.Response.Headers["X-Saved-To"] = saved;
        return Results.File(export.File.Content, "application/octet-stream", export.File.FileName);   // no extension, like the legacy file
    }
}

public sealed record RepPassRequest(IReadOnlyList<string>? CaseNumbers);
public sealed record CruPassRequest(IReadOnlyList<SendToClaimsItem>? Items, DateOnly? PassOutDate);
