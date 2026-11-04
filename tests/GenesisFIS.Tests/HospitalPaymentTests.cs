using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Tests;

public class HospitalPaymentRuleTests
{
    private static readonly HospitalPaymentHeader Header = new(new DateOnly(2030, 5, 3), null, "CHQ1", new DateOnly(2030, 5, 4), 1500, "HOSPITAL ONE", null);

    [Fact]
    public void Update_checks_follow_the_legacy_form()
    {
        HospitalPaymentUpdateRequest R(HospitalPaymentHeader h, HospitalPaymentClaim? c = null) => new(h, c ?? new HospitalPaymentClaim("ZZ1", 1, 100));
        Assert.Equal("payee", HospitalPaymentRules.Validate(R(Header with { Payee = " " }))?.Field);
        Assert.Equal("pvDate", HospitalPaymentRules.Validate(R(Header with { PvDate = null }))?.Field);
        Assert.Equal("chequeNo", HospitalPaymentRules.Validate(R(Header with { ChequeNo = null }))?.Field);
        Assert.Equal("chequeDate", HospitalPaymentRules.Validate(R(Header with { ChequeDate = null }))?.Field);
        Assert.Equal("chequeAmount", HospitalPaymentRules.Validate(R(Header with { ChequeAmount = null }))?.Field);
        Assert.Equal("pvStatus", HospitalPaymentRules.Validate(R(Header with { PvStatus = "X" }))?.Field);
        Assert.Equal("contraCase", HospitalPaymentRules.Validate(R(Header, new HospitalPaymentClaim("ZZ1", 1, 100, ContraCase: "ZZ2")))?.Field);
        Assert.Null(HospitalPaymentRules.Validate(R(Header with { PvStatus = "C" })));
    }

    [Fact]
    public void The_cheque_amount_is_written_in_words_as_the_legacy_voucher()
    {
        Assert.Equal(("One Thousand Five Hundred Only", ""), HospitalPaymentRules.Wording(1500));
        Assert.Equal(("One Hundred And Twenty Five And Cents Fifty Only", ""), HospitalPaymentRules.Wording(125.50m));
        Assert.Equal(("One Hundred Five Only", ""), HospitalPaymentRules.Wording(105));
        Assert.Equal(("One Dollar And Cent One Only", ""), HospitalPaymentRules.Wording(1.01m));
        Assert.Equal(("Cents Ten Only", ""), HospitalPaymentRules.Wording(0.10m));
        Assert.Equal(("Two Hundred And Thirty Four Thousand Five Hundred And Sixty Seven", "And Cents Eighty Nine Only"), HospitalPaymentRules.Wording(234567.89m));
    }

    [Fact]
    public void Status_filters_follow_the_legacy_combo()
    {
        Assert.True(HospitalPaymentStatus.Matches(HospitalPaymentStatus.Unpaid, null));
        Assert.False(HospitalPaymentStatus.Matches(HospitalPaymentStatus.Unpaid, "F"));
        Assert.True(HospitalPaymentStatus.Matches(HospitalPaymentStatus.UnpaidOrPartial, "P"));
        Assert.True(HospitalPaymentStatus.Matches(HospitalPaymentStatus.PaidOrPartial, "F"));
        Assert.True(HospitalPaymentStatus.Matches(null, "F"));
    }
}

public class HospitalPaymentTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/payments/hospital", Pv = "CFZZ00001", A = "ZZHOP00001", B = "ZZHOP00002", C = "ZZHOP00003";
    private HttpClient Client(int position = 55) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static readonly HospitalPaymentHeader Header = new(new DateOnly(2030, 5, 3), "A", "CHQ777", new DateOnly(2030, 5, 4), 1250.5m, "HOSPITAL PAYEE", "corrected", 5, 2030);
    private static HospitalPaymentSearch ByClaims => new(ClaimNoFrom: A, ClaimNoTo: C);

    /// <summary>Three claims payable to hospital 00001: A (RM 1,000) and B (RM 300) paid on voucher CFZZ00001, C (RM 200) not paid.</summary>
    private async Task SeedAsync()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO CAS (CASNumber, CASClaimAdmin, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASMRNNo)
                VALUES (@A, 'N', '00001', '2030-04-01', '2030-04-03', 'MRN1'), (@B, 'N', '00001', '2030-04-02', '2030-04-04', 'MRN2'), (@C, 'N', '00001', '2030-04-05', '2030-04-06', 'MRN3');
                """, new { A, B, C });
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("""
                INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMBordxAmt, CLMWSStatus, ClaimCloseDate)
                VALUES (@A, 1, 'C', 1, 0, 0, 0, 1000, 0, 1000, 'AA', '2030-04-10'), (@B, 1, 'C', 1, 0, 0, 0, 300, 0, 300, 'AA', '2030-04-11'), (@C, 1, 'C', 1, 0, 0, 0, 200, 0, 200, 'AA', '2030-04-12');
                INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, InvNo, CASNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVPayTo, INVAmount, InvPrintStatus, INVDCMBMStatus)
                VALUES ('1', 1, 1, 'ZZHINV-A1', @A, '2030-04-03', '2030-04-05', 'TEST', '00001', 'H', 600, '0', '0'), ('1', 1, 1, 'ZZHINV-A2', @A, '2030-04-03', '2030-04-05', 'TEST', '00001', 'H', 400, '0', '0'),
                       ('1', 1, 1, 'ZZHINV-B', @B, '2030-04-04', '2030-04-05', 'TEST', '00001', 'H', 300, '0', '0'), ('1', 1, 1, 'ZZHINV-C', @C, '2030-04-06', '2030-04-07', 'TEST', '00001', 'H', 200, '0', '0');
                """, new { A, B, C });
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        await pay.ExecuteAsync("""
            INSERT INTO Payment2HOS (PVHOSNo, CASNumber, ClaimVersion, PVHOSDate, PVPayHOSStatus, PVStatus, PVHOSChequeNo, PVHOSChequeAmt, HOSPaidAmt)
            VALUES (@Pv, @A, '1', '2030-05-01', 'F', 'F', 'CHQ1', 1300, 1000), (@Pv, @B, '1', '2030-05-01', 'F', 'F', 'CHQ1', 1300, 300);
            """, new { Pv, A, B });
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HospitalPaymentListing> SearchAsync(HospitalPaymentSearch s) => await Ok<HospitalPaymentListing>(await Client().PostAsJsonAsync($"{Url}/search", s));

    [DatabaseFact]
    public async Task Lists_the_claims_payable_to_hospitals_with_their_vouchers()
    {
        await SeedAsync();
        var listing = await SearchAsync(ByClaims);
        Assert.Equal([A, B, C], listing.Lines.Select(l => l.CaseNumber));
        var a = listing.Lines[0];
        Assert.Equal((Pv, "F", 1000m, 0m, "ZZHINV-A1, ZZHINV-A2", "MRN1"), (a.PvNo, a.Status, a.Paid, a.Outstanding, a.InvoiceNos, a.Mrn));
        Assert.NotNull(a.Hospital);
        var c = listing.Lines[2];
        Assert.Equal((null, null, 200m), (c.PvNo, c.Status, c.Outstanding));

        Assert.Equal([C], (await SearchAsync(ByClaims with { Status = HospitalPaymentStatus.Unpaid })).Lines.Select(l => l.CaseNumber));
        Assert.Equal([A, B], (await SearchAsync(new HospitalPaymentSearch(PvNoFrom: Pv, PvNoTo: Pv))).Lines.Select(l => l.CaseNumber));
        Assert.Equal([B], (await SearchAsync(new HospitalPaymentSearch(MrnFrom: "MRN2", MrnTo: "MRN2"))).Lines.Select(l => l.CaseNumber));
        Assert.Equal([A], (await SearchAsync(ByClaims with { InvoiceNoFrom = "ZZHINV-A2", InvoiceNoTo = "ZZHINV-A2" })).Lines.Select(l => l.CaseNumber));
        var options = await Ok<HospitalPaymentOptions>(await Client().GetAsync($"{Url}/options"));
        Assert.Contains(options.Hospitals, h => h.Code == "00001");
    }

    [DatabaseFact]
    public async Task Update_changes_the_voucher_header_on_all_its_claims_and_the_details_of_one()
    {
        await SeedAsync();
        var res = await Client().PutAsJsonAsync($"{Url}/{Pv}", new HospitalPaymentUpdateRequest(Header,
            new HospitalPaymentClaim(B, 1, 250, BankCharge: 2, Discount: 50, ContraCase: "ZZHOP00009", ContraVersion: "1", ContraAmount: 10, CaseRemark: "discounted")));
        var result = await Ok<HospitalPaymentResult>(res);
        Assert.Equal(2, result.Lines.Count);
        Assert.All(result.Lines, l => Assert.Equal(("CHQ777", 1250.5m, new DateOnly(2030, 5, 3), "A", 5, 2030, "HOSPITAL PAYEE", "corrected"),
            (l.ChequeNo, l.ChequeAmount!.Value, l.PvDate!.Value, l.PvStatus, l.AccountMonth!.Value, l.AccountYear!.Value, l.Payee, l.Remark)));
        Assert.Equal("One Thousand Two Hundred And Fifty And Cents Fifty Only", result.Lines[0].Wording);
        var a = result.Lines.Single(l => l.CaseNumber == A);
        var b = result.Lines.Single(l => l.CaseNumber == B);
        Assert.Equal((1000m, null), (a.Paid, a.Discount));
        Assert.Equal((250m, 50m, 2m, "ZZHOP00009", 10m, "discounted", 50m), (b.Paid, b.Discount!.Value, b.BankCharge!.Value, b.ContraCase, b.ContraAmount!.Value, b.CaseRemark, b.Outstanding));

        // A blank contra removes it.
        await Ok<HospitalPaymentResult>(await Client().PutAsJsonAsync($"{Url}/{Pv}", new HospitalPaymentUpdateRequest(Header, new HospitalPaymentClaim(B, 1, 250))));
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        Assert.Equal(0, await pay.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ContraAllocation WHERE RefNo = @Pv", new { Pv }));
        Assert.Equal(2, factory.Audit.Entries.Count(e => e.Action == "Payment.Hospital.Update"));
    }

    [DatabaseFact]
    public async Task The_enquiry_lists_the_same_lines_but_cannot_update()
    {
        await SeedAsync();
        var enquiry = factory.ClientFor(WriteApiFactory.Access(145));
        var res = await enquiry.PostAsJsonAsync("/api/payments/hospital-enquiry/search", ByClaims);
        var listing = await Ok<HospitalPaymentListing>(res);
        Assert.Equal((await SearchAsync(ByClaims)).Lines, listing.Lines);
        Assert.Equal(HttpStatusCode.OK, (await enquiry.GetAsync("/api/payments/hospital-enquiry/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await enquiry.PutAsJsonAsync($"{Url}/{Pv}", new HospitalPaymentUpdateRequest(Header, new HospitalPaymentClaim(A, 1, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsJsonAsync("/api/payments/hospital-enquiry/search", ByClaims)).StatusCode);
    }

    [DatabaseFact]
    public async Task Update_and_search_are_refused_when_invalid_or_without_the_screen()
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/{Pv}", new HospitalPaymentUpdateRequest(Header, new HospitalPaymentClaim(C, 1, 200)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync($"{Url}/CFZZ99999", new HospitalPaymentUpdateRequest(Header, new HospitalPaymentClaim(A, 1, 200)))).StatusCode);
        var noPayee = await Client().PutAsJsonAsync($"{Url}/{Pv}", new HospitalPaymentUpdateRequest(Header with { Payee = "" }, new HospitalPaymentClaim(A, 1, 200)));
        Assert.Equal(HttpStatusCode.BadRequest, noPayee.StatusCode);
        Assert.Contains("Payee", await noPayee.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new HospitalPaymentSearch(Status: HospitalPaymentStatus.Unpaid))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(61).GetAsync($"{Url}/options")).StatusCode);
    }
}
