using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class McoFeeEndpoints
{
    /// <summary>Legacy USRAccess position of MCOFees > Refund Listing.</summary>
    public const int RefundListingScreen = 21;

    public static void MapMcoFees(this IEndpointRouteBuilder app)
    {
        var refund = app.MapGroup("/api/mco/refund-listing").WithTags("MCO Fees: Refund Listing").RequireScreen(RefundListingScreen);
        refund.MapGet("/payors", async (IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.PayorsAsync(ct)));
        refund.MapGet("/", async (string? payor, string? dateBy, DateOnly? from, DateOnly? to, string? printStatus, IMcoRefundRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new McoRefundFilter(payor, dateBy, from, to, printStatus), ct)));
        refund.MapGet("/supplementary", async (string memberNumber, IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.SupplementaryAsync(memberNumber, ct)));
    }
}
