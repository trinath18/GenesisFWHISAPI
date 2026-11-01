using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Maintenance;

namespace GenesisFIS.Api.Features.Maintenance;

public static class BenefitEndpoints
{
    /// <summary>Legacy USRAccess positions of Maintenance > Benefits: Benefit Entry tab 88, Benefits Listing tab 89.</summary>
    public const int EntryScreen = 88, ListingScreen = 89;

    public static void MapBenefits(this IEndpointRouteBuilder app)
    {
        var entry = app.MapGroup("/api/maintenance/benefits").WithTags("Maintenance: Benefits").RequireScreen(EntryScreen);
        entry.MapGet("/options", async (IBenefitRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        entry.MapGet("/entry", async (string health, string insured, string plan, DateOnly effectiveDate, IBenefitRepository repo, CancellationToken ct) => Results.Ok(await repo.GetEntryAsync(health, insured, plan, effectiveDate, ct)));
        entry.MapPost("/entry", async (BenefitSaveRequest request, ClaimsPrincipal user, IBenefitRepository repo, CancellationToken ct) => Results.Created("/api/maintenance/benefits/entry", await repo.AddAsync(request, user.UserCode(), ct)));
        entry.MapPut("/entry", async (BenefitSaveRequest request, ClaimsPrincipal user, IBenefitRepository repo, CancellationToken ct) => Results.Ok(await repo.UpdateAsync(request, user.UserCode(), ct)));
        entry.MapDelete("/entry", async (string health, string insured, string plan, DateOnly effectiveDate, ClaimsPrincipal user, IBenefitRepository repo, CancellationToken ct) =>
            Results.Ok(new { deleted = await repo.DeleteAsync(health, insured, plan, effectiveDate, user.UserCode(), ct) }));

        var listing = app.MapGroup("/api/maintenance/benefits").WithTags("Maintenance: Benefits").RequireScreen(ListingScreen);
        listing.MapGet("/listing", async (string? health, string? insured, string? plan, DateOnly? effectiveDate, string? code, string? payor, string? group, IBenefitRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new BenefitListingFilter(health, insured, plan, effectiveDate, code, payor, group), ct)));
    }
}
