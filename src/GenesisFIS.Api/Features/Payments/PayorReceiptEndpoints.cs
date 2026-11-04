using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Api.Features.Payments;

public static class PayorReceiptEndpoints
{
    /// <summary>Legacy USRAccess position of Receipt > Payor (mnuReceiptPayor_Click: CheckAccess(61)).</summary>
    public const int Screen = 61;

    public static void MapPayorReceipts(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/receipts/payor").WithTags("Receipt: Payor").RequireScreen(Screen);
        g.MapGet("/options", async (ClaimsPrincipal user, IPayorReceiptRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        g.MapPost("/search", async (PayorReceiptSearch search, IPayorReceiptRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(search, ct)));
        g.MapPost("/", async (PayorReceiptSaveRequest request, ClaimsPrincipal user, IPayorReceiptRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SaveAsync(request, user.UserCode(), ct)));
        // Receipt > Payor (Manual) (FrmReceiptPayorManual, the same USRAccess 61): typed receipt number, claims or whole bordereaux.
        g.MapPost("/search-bordx", async (PayorReceiptSearch search, IPayorReceiptRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchBordereauxAsync(search, ct)));
        g.MapPost("/manual", async (ManualPayorReceiptRequest request, ClaimsPrincipal user, IPayorReceiptRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SaveManualAsync(request, user.UserCode(), ct)));
        g.MapPut("/{receiptNo}", async (string receiptNo, PayorReceiptUpdateRequest request, ClaimsPrincipal user, IPayorReceiptRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UpdateAsync(receiptNo, request, user.UserCode(), ct)));
        // Legacy Print PV (FWHS\PaymentVoucher\PVHospital.xlt): the voucher the receipt paid the hospital with.
        g.MapGet("/voucher/{pvNo}/voucher.pdf", (string pvNo, ClaimsPrincipal user, IPaymentVoucherRepository repo, CancellationToken ct) =>
            HospitalPaymentEndpoints.PrintAsync(pvNo, "voucher.pdf", user, repo, ct));
    }
}
