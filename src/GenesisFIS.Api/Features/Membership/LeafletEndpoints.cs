using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;
using GenesisFIS.Reports.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class LeafletEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Leaflet &amp; Card.</summary>
    public const int Screen = 4;
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    /// <summary>Response header saying whether MBMPrinting was updated ("false" when the table is not in the database).</summary>
    public const string RecordedHeader = "X-Printing-Recorded";

    public static void MapLeaflet(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/leaflet").WithTags("Membership: Leaflet & Card").RequireScreen(Screen);

        g.MapGet("/options", async (ILeafletRepository repo, CancellationToken ct) => Results.Ok(new LeafletOptions(
            LeafletLists.Selections, LeafletLists.Sequences, LeafletLists.PrintOptions, LeafletLists.ExportOptions, LeafletLists.Versions, await repo.PayorsAsync(ct))));

        g.MapGet("/", async ([AsParameters] CriteriaQuery q, ILeafletRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchAsync(q.ToCriteria() with { Selection = q.Selection ?? LeafletLists.SingleMember }, cardExport: false, ct)));
        g.MapGet("/cards", async ([AsParameters] CriteriaQuery q, ILeafletRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(q.ToCriteria() with { Selection = null }, cardExport: true, ct)));

        g.MapPost("/print", async (LeafletPrintRequest request, HttpContext http, ClaimsPrincipal user, ILeafletRepository repo, CancellationToken ct) =>
        {
            // The Leaflet tab always has a selection (Single Member when the form opens).
            if (request.Criteria is { Selection: null } c) request = request with { Criteria = c with { Selection = LeafletLists.SingleMember } };
            var output = await repo.PrintLeafletsAsync(request, user.UserCode(), ct);
            http.Response.Headers[RecordedHeader] = output.Recorded ? "true" : "false";
            return Results.File(LeafletReports.Leaflets(output.Items, request.Version!, DateOnly.FromDateTime(DateTime.Today)), "application/pdf", $"leaflets-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
        });

        g.MapPost("/cards/export", async (LeafletPrintRequest request, HttpContext http, ClaimsPrincipal user, ILeafletRepository repo, CancellationToken ct) =>
        {
            var c = request.Criteria;
            var output = await repo.ExportCardsAsync(request with { Criteria = c is null ? null : c with { Selection = null } }, user.UserCode(), ct);
            var codes = output.Items.Select(l => l.PayCode).Distinct().ToList();
            var payorCode = c?.Payor?.Trim().ToUpperInvariant() is { Length: > 0 } p ? p : codes.Count == 1 ? codes[0] : null;
            var payorName = payorCode is null ? "(several payors)" : (await repo.PayorsAsync(ct)).FirstOrDefault(x => x.Code == payorCode)?.Name ?? payorCode;
            var bordx = c?.BordxDate ?? c?.BordxFrom ?? output.Items.Select(l => l.BordxDate).FirstOrDefault(d => d is not null);
            http.Response.Headers[RecordedHeader] = output.Recorded ? "true" : "false";
            var name = LeafletReports.FileName(payorCode, bordx);
            return Results.File(LeafletReports.CardExport(output.Items, payorName, payorCode, bordx, request.Version!), Xlsx, $"card-export-{(name.Length > 0 ? name : DateTime.Now.ToString("yyyyMMdd"))}.xlsx");
        });
    }

    /// <summary>Query string of the two searches.</summary>
    public sealed record CriteriaQuery(string? Selection, string? MemberNo, string? MemberFrom, string? MemberTo, DateOnly? BordxDate, int? BatchNo, string? Payor,
        DateOnly? BordxFrom, DateOnly? BordxTo, string? Sequence)
    {
        public LeafletCriteria ToCriteria() => new(Selection, MemberNo, MemberFrom, MemberTo, BordxDate, BatchNo, Payor, BordxFrom, BordxTo, Sequence);
    }
}
