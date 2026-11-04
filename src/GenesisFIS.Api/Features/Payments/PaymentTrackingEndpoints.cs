using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Api.Features.Payments;

public static class PaymentTrackingEndpoints
{
    /// <summary>Legacy USRAccess position of Payment > Payment Tracking (mnuPaymentTracking_Click: CheckAccess(58)).</summary>
    public const int Screen = 58;

    public static void MapPaymentTracking(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/payments/tracking").WithTags("Payment: Tracking").RequireScreen(Screen);
        g.MapGet("/options", async (IPaymentTrackingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapPost("/search", async (PaymentTrackingSearch search, IPaymentTrackingRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(search, ct)));
    }
}
