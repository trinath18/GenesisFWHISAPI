using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace GenesisFIS.Tests;

public class PaymentVoucherRuleTests
{
    private static VoucherClaim Claim(string no, decimal bordx, decimal medical, decimal paid, params VoucherInvoice[] invoices) =>
        new(no, 1, "42" + no, "MRN" + no, "PATIENT " + no, "BX1", "RF1", "CHQ" + no, "CHQ" + no + "(1)", bordx, medical, paid, invoices);

    private static VoucherData Data(params VoucherClaim[] claims) =>
        new("CF1", new DateOnly(2021, 8, 6), new DateOnly(2021, 8, 6), "PUBLIC BANK", null, "HOSPITAL X", "ADDR 1", null, null, claims, new DateOnly(2026, 11, 3), "TEST");

    [Fact]
    public void A_claim_with_one_invoice_prints_charges_fee_and_total_on_one_line()
    {
        var l = Assert.Single(PaymentVoucherRules.Lines(Data(Claim("A", 1000, 60, 1000, new VoucherInvoice("INV1", new DateOnly(2021, 7, 1), 0)))));
        Assert.Equal((940m, 60m, 1000m), (l.HospitalCharges, l.MedicalReport, l.Total));
        Assert.Equal(("CHQA", "CHQA(1)"), (l.ChequeNo, l.ChequeNoMr));
    }

    [Fact]
    public void Several_invoices_print_each_amount_once_so_lines_add_up_to_the_bordereau_amount()
    {
        var lines = PaymentVoucherRules.Lines(Data(
            Claim("A", 1000, 60, 1000, new VoucherInvoice("INV1", null, 0), new VoucherInvoice("MR1", null, 60), new VoucherInvoice("INV2", null, 0)),
            Claim("B", 500, 0, 500, new VoucherInvoice("INV3", null, 0))));
        Assert.Equal([1, 2, 3, 4], lines.Select(l => l.No));
        Assert.Equal((940m, 0m, null), (lines[0].HospitalCharges, lines[0].MedicalReport, lines[0].ChequeNoMr));
        Assert.Equal((0m, 60m, null, "CHQA(1)"), (lines[1].HospitalCharges, lines[1].MedicalReport, lines[1].ChequeNo, lines[1].ChequeNoMr));
        Assert.Equal(0m, lines[2].Total);
        Assert.Equal(1500m, lines.Sum(l => l.Total));
    }

    [Fact]
    public void A_fee_on_no_invoice_goes_on_the_charges_line_and_a_claim_without_invoices_still_prints()
    {
        var lines = PaymentVoucherRules.Lines(Data(Claim("A", 800, 50, 800, new VoucherInvoice("INV1", null, 0), new VoucherInvoice("INV2", null, 0)), Claim("B", 300, 0, 300)));
        Assert.Equal((750m, 50m, 800m), (lines[0].HospitalCharges, lines[0].MedicalReport, lines[0].Total));
        Assert.Equal(0m, lines[1].Total);
        Assert.Equal((null, 300m), (lines[2].InvoiceNo, lines[2].Total));
    }

    [Fact]
    public void Letter_lists_one_line_per_claim_with_its_invoices_and_the_amount_paid()
    {
        var lines = PaymentVoucherRules.LetterLines(Data(Claim("A", 1000, 60, 990, new VoucherInvoice("INV1", new DateOnly(2021, 7, 1), 0), new VoucherInvoice("MR1", null, 60))));
        var l = Assert.Single(lines);
        Assert.Equal(("INV1, MR1", new DateOnly(2021, 7, 1), 990m), (l.InvoiceNos, l.InvoiceDate, l.Paid));
    }
}

public class ChequeRuleTests
{
    private static ChequeData Cheque(string? payee = "HOSPITAL X", string no = "100001", DateOnly? date = null, decimal amount = 10) =>
        new(no, date ?? new DateOnly(2021, 8, 6), amount, payee, "Ten Only", "", "CF", "CF1", null, false);

    [Fact]
    public void Print_cheque_checks_follow_the_legacy_order()
    {
        Assert.Null(ChequeRules.Problem(Cheque()));
        Assert.Equal("Please Enter Payee", ChequeRules.Problem(Cheque(payee: " ")));
        Assert.Equal("Please Enter Cheque No", ChequeRules.Problem(Cheque(no: "")));
        Assert.Equal("Please Enter Cheque Amount", ChequeRules.Problem(Cheque(amount: 0)));
    }

    [Fact]
    public void A_numeric_range_must_be_in_series_and_references_are_not_counted()
    {
        Assert.Equal(3, ChequeRules.SeriesCount("CQ000101", "CQ000103"));
        Assert.Equal(1, ChequeRules.SeriesCount("364271", "364271"));
        Assert.Null(ChequeRules.SeriesCount("CMS21218002294", "PV-ANH056750"));
        Assert.Null(ChequeRules.SeriesCount("X14515242A", "X14515250A"));
        Assert.Equal("Cheque range not in series", ChequeRules.ListingProblem(2, 3));
        Assert.Equal("No record found", ChequeRules.ListingProblem(0, null));
        Assert.Null(ChequeRules.ListingProblem(5, null));
        Assert.Equal("Please ensure cheque range entered correctly", ChequeRules.RangeProblem(new("CF", "CQ000105", "CQ000101")));
        Assert.Equal("Please select payment type", ChequeRules.RangeProblem(new(null, "1", "2")));
    }

    [Fact]
    public void The_wording_defaults_to_the_amount_in_words()
    {
        Assert.Equal(("One Thousand Two Hundred Five Only", ""), ChequeRules.Wording(null, null, 1205));
        Assert.Equal(("STORED", "LINE"), ChequeRules.Wording(" STORED ", "LINE", 1205));
    }
}

public class PaymentVoucherPrintTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    [Fact]
    public async Task Prints_need_the_screen_permission()
    {
        var none = factory.ClientFor(Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/payments/hospital/CF123474/print/voucher.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/receipts/payor/voucher/CF123474/voucher.pdf")).StatusCode);
    }

    [DatabaseFact]
    public async Task Voucher_and_letter_print_as_pdf_from_both_screens()
    {
        var hospital = factory.ClientFor(Access(55));
        foreach (var kind in new[] { "voucher.pdf", "letter.pdf" })
        {
            var res = await hospital.GetAsync($"/api/payments/hospital/CF35708/print/{kind}");
            Assert.True(res.IsSuccessStatusCode, $"{kind}: {(int)res.StatusCode}");
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));
        }
        var receipt = await factory.ClientFor(Access(61)).GetAsync("/api/receipts/payor/voucher/CF35708/voucher.pdf");
        Assert.True(receipt.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hospital.GetAsync("/api/payments/hospital/ZZ-NO-PV/print/voucher.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hospital.GetAsync("/api/payments/hospital/CF35708/print/receipt.pdf")).StatusCode);
    }

    [DatabaseFact]
    public async Task Voucher_lines_add_up_to_the_bordereau_amounts_of_its_claims()
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentVoucherRepository>();
        var d = (await repo.LoadAsync("CF35708", "TEST"))!;
        Assert.Equal(7, d.Claims.Count);
        Assert.NotNull(d.HospitalName);
        Assert.All(d.Claims, c => Assert.NotEmpty(c.Invoices));
        Assert.Equal(d.Claims.Sum(c => c.BordxAmount), PaymentVoucherRules.Lines(d).Sum(l => l.Total));
    }

    [DatabaseFact]
    public async Task Print_cheque_uses_the_hospital_as_payee_when_the_voucher_has_none()
    {
        using var scope = factory.Services.CreateScope();
        var c = Assert.Single(await scope.ServiceProvider.GetRequiredService<IPaymentVoucherRepository>().ChequesAsync("CF35708"));
        Assert.Equal(("364271", 7041m, "HOSPITAL TENGKU AMPUAN RAHIMAH", "CF"), (c.ChequeNo, c.Amount, c.Payee, c.AccountType));
        Assert.StartsWith("Seven Thousand Forty One", c.Wording1);
        var res = await factory.ClientFor(Access(55)).GetAsync("/api/payments/hospital/CF35708/print/cheque.pdf");
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        var accounts = await factory.ClientFor(Access(55)).GetFromJsonAsync<List<ChequeAccount>>("/api/payments/hospital/cheques/accounts");
        Assert.Contains(accounts!, a => a.AccountType == "CF" && a.Vouchers > 100_000);
    }
}

public class ChequeRangePrintTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/payments/hospital/cheques";
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => factory.ResetAsync();
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(55));

    private async Task SeedAsync()
    {
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        await pay.ExecuteAsync("""
            INSERT INTO Payment2HOS (PVHOSNo, CASNumber, ClaimVersion, PVHOSDate, PVPayHOSStatus, PVStatus, PVHOSChequeNo, PVHOSChequeDate, PVHOSChequeAmt, HOSPaidAmt, PVPayee)
            VALUES ('ZQ0000001', 'ZZQ001', '1', '2030-05-01', 'F', 'F', 'ZQ000101', '2030-05-02', 300, 100, 'HOSPITAL ONE'),
                   ('ZQ0000001', 'ZZQ002', '1', '2030-05-01', 'F', 'F', 'ZQ000101', '2030-05-02', 300, 200, 'HOSPITAL ONE'),
                   ('ZQ0000002', 'ZZQ003', '1', '2030-05-01', 'F', 'F', 'ZQ000102', '2030-05-02', 1205.5, 1205.5, 'HOSPITAL TWO'),
                   ('ZQ0000003', 'ZZQ004', '1', '2030-05-01', 'F', 'F', 'ZQ000103', '2030-05-02', 50, 50, 'HOSPITAL THREE')
            """);
    }

    [DatabaseFact]
    public async Task Range_lists_one_cheque_per_number_and_print_marks_them_printed()
    {
        await SeedAsync();
        var listing = (await (await Client().PostAsJsonAsync($"{Url}/search", new ChequeRangeRequest("ZQ", "ZQ000101", "ZQ000103"))).Content.ReadFromJsonAsync<ChequeRangeListing>())!;
        Assert.Equal((3, 3, null), (listing.Cheques.Count, listing.Expected, listing.Problem));
        Assert.Equal([300m, 1205.5m, 50m], listing.Cheques.Select(c => c.Amount));
        Assert.Equal("One Thousand Two Hundred Five And Cents Fifty Only", listing.Cheques[1].Wording1);

        var gap = (await (await Client().PostAsJsonAsync($"{Url}/search", new ChequeRangeRequest("ZQ", "ZQ000101", "ZQ000104"))).Content.ReadFromJsonAsync<ChequeRangeListing>())!;
        Assert.Equal("Cheque range not in series", gap.Problem);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/print", new ChequeRangeRequest("ZQ", "ZQ000101", "ZQ000104"))).StatusCode);

        var print = await Client().PostAsJsonAsync($"{Url}/print", new ChequeRangeRequest("ZQ", "ZQ000101", "ZQ000103"));
        Assert.True(print.IsSuccessStatusCode, await print.Content.ReadAsStringAsync());
        Assert.Equal("application/pdf", print.Content.Headers.ContentType?.MediaType);
        await using (var pay = await factory.Db.OpenAsync(GenesisDb.Payment))
            Assert.Equal(4, await pay.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Payment2HOS WHERE PVHOSNo LIKE 'ZQ%' AND PVChequeStatus = 'Y'"));
        Assert.Single(factory.Audit.Entries, e => e.Action == "Payment.Hospital.PrintChequeRange");
        var again = (await (await Client().PostAsJsonAsync($"{Url}/search", new ChequeRangeRequest("ZQ", "ZQ000101", "ZQ000103"))).Content.ReadFromJsonAsync<ChequeRangeListing>())!;
        Assert.Equal("No record found", again.Problem);
    }

    [DatabaseFact]
    public async Task Range_needs_the_screen_and_a_valid_range()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(3)).PostAsJsonAsync($"{Url}/search", new ChequeRangeRequest("ZQ", "1", "2"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new ChequeRangeRequest("ZQ", "", "2"))).StatusCode);
    }
}
