using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Posting;

namespace GenesisFIS.Api.Features.Posting;

public static class AccountPostingEndpoints
{
    /// <summary>Legacy USRAccess position of A/C Export > Invoice > Hospital (Hospital_Click: CheckAccess(169)).</summary>
    public const int InvoiceHospitalScreen = 169;

    /// <summary>Legacy USRAccess position of A/C Export > Invoice > Member (Member_Click: CheckAccess(170)).</summary>
    public const int InvoiceMemberScreen = 170;

    /// <summary>Legacy USRAccess position of A/C Export > Payment > Hospital (PostPay2Hosp_Click: CheckAccess(173)).</summary>
    public const int PaymentHospitalScreen = 173;

    /// <summary>Legacy USRAccess position of A/C Export > Receipt > Payor (PayorExp_Click: CheckAccess(172)).</summary>
    public const int ReceiptPayorScreen = 172;

    /// <summary>Legacy USRAccess position of A/C Export > Bordx > Payor (BordxPost2Pay_Click: CheckAccess(176)).</summary>
    public const int BordxPayorScreen = 176;

    public static void MapAccountPosting(this IEndpointRouteBuilder app)
    {
        var hosp = app.MapGroup("/api/posting/invoice-hospital").WithTags("A/C Export: Invoice Hospital").RequireScreen(InvoiceHospitalScreen);
        hosp.MapGet("/options", async (ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        hosp.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        hosp.MapPost("/search", async (HospitalInvoicePostingSearch search, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchHospitalInvoicesAsync(search, ct)));
        hosp.MapPost("/post", async (HospitalInvoicePostRequest request, ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PostHospitalInvoicesAsync(request, user.UserCode(), ct)));

        var mbm = app.MapGroup("/api/posting/invoice-member").WithTags("A/C Export: Invoice Member").RequireScreen(InvoiceMemberScreen);
        mbm.MapGet("/options", async (ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        mbm.MapPost("/search", async (MemberInvoicePostingSearch search, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchMemberInvoicesAsync(search, ct)));
        mbm.MapPost("/post", async (MemberInvoicePostRequest request, ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PostMemberInvoicesAsync(request, user.UserCode(), ct)));

        var payHosp = app.MapGroup("/api/posting/payment-hospital").WithTags("A/C Export: Payment Hospital").RequireScreen(PaymentHospitalScreen);
        payHosp.MapGet("/options", async (ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        payHosp.MapPost("/search", async (HospitalPaymentPostingSearch search, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchHospitalPaymentsAsync(search, ct)));
        payHosp.MapPost("/post", async (HospitalPaymentPostRequest request, ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PostHospitalPaymentsAsync(request, user.UserCode(), ct)));

        var recPay = app.MapGroup("/api/posting/receipt-payor").WithTags("A/C Export: Receipt Payor").RequireScreen(ReceiptPayorScreen);
        recPay.MapGet("/options", async (ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        recPay.MapPost("/search", async (PayorReceiptPostingSearch search, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchPayorReceiptsAsync(search, ct)));
        recPay.MapPost("/post", async (PayorReceiptPostRequest request, ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PostPayorReceiptsAsync(request, user.UserCode(), ct)));

        var bordxPay = app.MapGroup("/api/posting/bordx-payor").WithTags("A/C Export: Bordx Payor").RequireScreen(BordxPayorScreen);
        bordxPay.MapGet("/options", async (ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(user.UserCode(), ct)));
        bordxPay.MapPost("/search", async (BordxPayorPostingSearch search, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.SearchBordxPayorAsync(search, ct)));
        bordxPay.MapPost("/post", async (BordxPayorPostRequest request, ClaimsPrincipal user, IAccountPostingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.PostBordxPayorAsync(request, user.UserCode(), ct)));
    }
}
