using Genesis.Api.Infrastructure;
using Genesis.Core.Membership;

namespace Genesis.Api.Features.Membership;

public static class McoFeeEndpoints
{
    /// <summary>Legacy USRAccess position of MCOFees > Refund Listing.</summary>
    public const int RefundListingScreen = 21;

    /// <summary>Legacy USRAccess position of MCOFees > MCO Inv/Listing (plain, GST and SST variants).</summary>
    public const int InvoiceScreen = 20;

    /// <summary>Legacy USRAccess position of MCOFees > Invoice Manual Upd.</summary>
    public const int ManualInvoiceScreen = 148;

    public static void MapMcoFees(this IEndpointRouteBuilder app)
    {
        var refund = app.MapGroup("/api/mco/refund-listing").WithTags("MCO Fees: Refund Listing").RequireScreen(RefundListingScreen);
        refund.MapGet("/payors", async (IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.PayorsAsync(ct)));
        refund.MapGet("/", async (string? payor, string? dateBy, DateOnly? from, DateOnly? to, string? printStatus, IMcoRefundRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new McoRefundFilter(payor, dateBy, from, to, printStatus), ct)));
        var invoice = app.MapGroup("/api/mco/fwhs-invoice").WithTags("MCO Fees: Inv/Listing").RequireScreen(InvoiceScreen);
        invoice.MapGet("/", async (string? payor, string? mode, DateOnly? from, DateOnly? to, string? invoiceNo, string? tax, IFwhsInvoiceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new FwhsInvoiceFilter(payor, mode, from, to, invoiceNo, tax ?? FwhsTax.None), ct)));
        invoice.MapPost("/", async (FwhsInvoiceFilter filter, System.Security.Claims.ClaimsPrincipal user, IFwhsInvoiceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.InvoiceAsync(filter with { Mode = "new", Tax = filter.Tax ?? FwhsTax.None }, user.UserCode(), ct)));
        var manual = app.MapGroup("/api/mco/manual-invoice").WithTags("MCO Fees: Invoice Manual Upd").RequireScreen(ManualInvoiceScreen);
        manual.MapPost("/search", async (McoManualFilter filter, IMcoManualInvoiceRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(filter, ct)));
        manual.MapPost("/update", async (McoManualUpdateRequest request, System.Security.Claims.ClaimsPrincipal user, IMcoManualInvoiceRepository repo, CancellationToken ct) => Results.Ok(await repo.UpdateAsync(request, user.UserCode(), ct)));
        var correction = app.MapGroup("/api/mco/correction").WithTags("MCO Fees: MCO Correction").RequireScreen(InvoiceScreen);
        correction.MapPost("/search", async (McoManualFilter filter, IMcoCorrectionRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(filter, ct)));
        correction.MapPost("/update", async (McoCorrectionRequest request, System.Security.Claims.ClaimsPrincipal user, IMcoCorrectionRepository repo, CancellationToken ct) => Results.Ok(await repo.UpdateAsync(request, user.UserCode(), ct)));
        refund.MapGet("/supplementary", async (string memberNumber, IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.SupplementaryAsync(memberNumber, ct)));
    }
}



