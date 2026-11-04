using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Payments;
using GenesisFIS.Reports.Payments;

namespace GenesisFIS.Api.Features.Payments;

public static class HospitalPaymentEndpoints
{
    /// <summary>Legacy USRAccess position of Payment > To Hospital (mnuPaymentHos_Click: CheckAccess(55)).</summary>
    public const int Screen = 55;

    /// <summary>Legacy USRAccess position of Payment > Enquiry > Payment To Hospital (PaymentToHospital_Click: CheckAccess(145)), the read-only form.</summary>
    public const int EnquiryScreen = 145;

    public static void MapHospitalPayments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/payments/hospital").WithTags("Payment: To Hospital").RequireScreen(Screen);
        g.MapGet("/options", async (IHospitalPaymentRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapPost("/search", async (HospitalPaymentSearch search, IHospitalPaymentRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(search, ct)));
        g.MapPut("/{pvNo}", async (string pvNo, HospitalPaymentUpdateRequest request, ClaimsPrincipal user, IHospitalPaymentRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UpdateAsync(pvNo, request, user.UserCode(), ct)));
        // Legacy Print PV (PV-Hospital.xlt) and Print CV (PaymentHOSLetter.xlt, the covering letter to the hospital).
        g.MapGet("/{pvNo}/print/{kind}", (string pvNo, string kind, ClaimsPrincipal user, IPaymentVoucherRepository repo, CancellationToken ct) =>
            PrintAsync(pvNo, kind, user, repo, ct));
        // Legacy Print Cheque Range (frmPrintChequeRange, mode MedixPayHOS): list the unprinted cheques of a voucher series and print them, marking them printed.
        g.MapGet("/cheques/accounts", async (IPaymentVoucherRepository repo, CancellationToken ct) => Results.Ok(await repo.ChequeAccountsAsync(ct)));
        g.MapPost("/cheques/search", async (ChequeRangeRequest request, IPaymentVoucherRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ChequeRangeAsync(request, ct)));
        g.MapPost("/cheques/print", async (ChequeRangeRequest request, ClaimsPrincipal user, IPaymentVoucherRepository repo, CancellationToken ct) =>
        {
            var cheques = await repo.PrintChequeRangeAsync(request, user.UserCode(), ct);
            return Results.File(PaymentVoucherDocuments.Cheques(cheques), "application/pdf", $"Cheques-{request.AccountType?.Trim()}-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
        });

        // Payment > Enquiry > Payment To Hospital (FrmEnquiryPaymentHospital): the same listing, read only.
        var enquiry = app.MapGroup("/api/payments/hospital-enquiry").WithTags("Payment: Enquiry To Hospital").RequireScreen(EnquiryScreen);
        enquiry.MapGet("/options", async (IHospitalPaymentRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        enquiry.MapPost("/search", async (HospitalPaymentSearch search, IHospitalPaymentRepository repo, CancellationToken ct) => Results.Ok(await repo.SearchAsync(search, ct)));
    }

    /// <summary>The payment voucher ("voucher.pdf"), covering letter ("letter.pdf") or cheque ("cheque.pdf", legacy Print Cheque) of one PV number.</summary>
    internal static async Task<IResult> PrintAsync(string pvNo, string kind, ClaimsPrincipal user, IPaymentVoucherRepository repo, CancellationToken ct)
    {
        if (kind.Equals("cheque.pdf", StringComparison.OrdinalIgnoreCase))
        {
            var cheques = await repo.ChequesAsync(pvNo, ct);
            if (cheques.Count == 0) return Results.NotFound(new { error = $"Payment voucher {pvNo} not found." });
            if (cheques.Select(ChequeRules.Problem).FirstOrDefault(p => p is not null) is { } problem) return Results.BadRequest(new { error = problem });
            return Results.File(PaymentVoucherDocuments.Cheques(cheques), "application/pdf", $"Cheque-{pvNo.Trim()}.pdf");
        }
        var doc = kind.ToLowerInvariant() switch { "voucher.pdf" => PaymentVoucherKinds.Voucher, "letter.pdf" => PaymentVoucherKinds.Letter, _ => null };
        if (doc is null) return Results.NotFound();
        var data = await repo.LoadAsync(pvNo, user.UserCode(), ct);
        if (data is null) return Results.NotFound(new { message = $"Payment voucher {pvNo} not found." });
        return Results.File(PaymentVoucherDocuments.ToPdf(doc, data), "application/pdf", $"{(doc == PaymentVoucherKinds.Letter ? "Letter" : "PV")}-{data.PvNo}.pdf");
    }
}
