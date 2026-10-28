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

    /// <summary>Legacy USRAccess position of MCOFees > Receipt MCO/Others (and the manual variant).</summary>
    public const int ReceiptScreen = 19;

    /// <summary>Legacy USRAccess position of MCOFees > MCO Reinstatement.</summary>
    public const int ReinstatementScreen = 1;

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
        var receipt = app.MapGroup("/api/mco/receipts").WithTags("MCO Fees: Receipt MCO/Others").RequireScreen(ReceiptScreen);
        receipt.MapGet("/payors", async (IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.PayorsAsync(ct)));
        receipt.MapGet("/invoices", async (string? payor, string? numberFrom, string? numberTo, DateOnly? dateFrom, DateOnly? dateTo, bool? creditNotes, bool? onlyOpen, IMcoReceiptRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.InvoicesAsync(new McoReceiptInvoiceSearch(payor, numberFrom, numberTo, dateFrom, dateTo, creditNotes ?? false, onlyOpen ?? true), ct)));
        receipt.MapGet("/", async (string? type, string? receiptFrom, string? receiptTo, DateOnly? dateFrom, DateOnly? dateTo, string? invoiceNo, IMcoReceiptRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new McoReceiptSearch(type, receiptFrom, receiptTo, dateFrom, dateTo, invoiceNo), ct)));
        receipt.MapPost("/", async (McoReceiptRequest request, System.Security.Claims.ClaimsPrincipal user, IMcoReceiptRepository repo, CancellationToken ct) => Results.Created("/api/mco/receipts", await repo.CreateAsync(request, false, user.UserCode(), ct)));
        receipt.MapPost("/manual", async (McoReceiptRequest request, System.Security.Claims.ClaimsPrincipal user, IMcoReceiptRepository repo, CancellationToken ct) => Results.Created("/api/mco/receipts", await repo.CreateAsync(request, true, user.UserCode(), ct)));
        receipt.MapPut("/{kind}/{index:int}", async (string kind, int index, McoReceiptUpdate update, System.Security.Claims.ClaimsPrincipal user, IMcoReceiptRepository repo, CancellationToken ct) => { await repo.UpdateAsync(kind, index, update, user.UserCode(), ct); return Results.Ok(new { updated = true }); });
        receipt.MapDelete("/{kind}/{index:int}", async (string kind, int index, System.Security.Claims.ClaimsPrincipal user, IMcoReceiptRepository repo, CancellationToken ct) => { await repo.DeleteAsync(kind, index, user.UserCode(), ct); return Results.Ok(new { deleted = true }); });
        var reinstatement = app.MapGroup("/api/mco/reinstatement").WithTags("MCO Fees: MCO Reinstatement").RequireScreen(ReinstatementScreen);
        reinstatement.MapGet("/payors", async (IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.PayorsAsync(ct)));
        reinstatement.MapGet("/", async (string? mode, string? payor, DateOnly? bordxDate, int? batchNo, string? invoiceNo, IMcoReinstatementRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(new McoReinstatementFilter(mode, payor, bordxDate, batchNo, invoiceNo), ct)));
        reinstatement.MapPost("/invoice", async (McoReinstatementFilter filter, System.Security.Claims.ClaimsPrincipal user, IMcoReinstatementRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.InvoiceAsync(filter with { Mode = "batch" }, user.UserCode(), ct)));

        var dateCorrection = app.MapGroup("/api/mco/date-correction").WithTags("MCO Fees: Date Correction").RequireScreen(InvoiceScreen);
        dateCorrection.MapPost("/search", async (McoDateFilter filter, IMcoDateCorrectionRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(filter, ct)));
        dateCorrection.MapPost("/update", async (McoDateCorrectionRequest request, System.Security.Claims.ClaimsPrincipal user, IMcoDateCorrectionRepository repo, CancellationToken ct) => Results.Ok(await repo.UpdateAsync(request, user.UserCode(), ct)));
        refund.MapGet("/supplementary", async (string memberNumber, IMcoRefundRepository repo, CancellationToken ct) => Results.Ok(await repo.SupplementaryAsync(memberNumber, ct)));
    }
}





