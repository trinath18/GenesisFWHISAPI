using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Tests;

public class PayorReceiptRuleTests
{
    private static readonly PayorReceiptHeader Header = new(new DateOnly(2030, 5, 2), 1100, "CHQ1", null, null, new DateOnly(2030, 5, 3), "AFFIN BANK", null);

    [Fact]
    public void A_claim_is_fully_paid_when_this_and_earlier_receipts_cover_it()
    {
        Assert.Equal("F", PayorReceiptRules.Status(1000, 0, 1000));
        Assert.Equal("F", PayorReceiptRules.Status(1000, 400, 600));
        Assert.Equal("P", PayorReceiptRules.Status(1000, 400, 500));
    }

    [Fact]
    public void Save_checks_follow_the_legacy_form()
    {
        PayorReceiptClaim Claim(decimal paying) => new("ZZ1", 1, paying);
        Assert.Equal("claims", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header, []))?.Field);
        Assert.Equal("amount", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header with { Amount = null }, [Claim(1100)]))?.Field);
        Assert.Equal("chequeNo", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header with { ChequeNo = " " }, [Claim(1100)]))?.Field);
        Assert.Equal("receiptDate", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header with { ReceiptDate = null }, [Claim(1100)]))?.Field);
        Assert.Equal("remark", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header, [Claim(1000)]))?.Field);
        Assert.Null(PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header with { Remark = "short paid" }, [Claim(1000)])));
        Assert.Equal("bank", PayorReceiptRules.Validate(new PayorReceiptSaveRequest(Header with { Bank = null }, [Claim(1100)]))?.Field);
        Assert.Equal("contraCase", PayorReceiptRules.Claim(new PayorReceiptClaim("ZZ1", 1, 10, ContraCase: "ZZ2"))?.Field);
        Assert.Equal("contraAmount", PayorReceiptRules.Claim(new PayorReceiptClaim("ZZ1", 1, 10, ContraCase: "ZZ2", ContraVersion: "1"))?.Field);
        Assert.Null(PayorReceiptRules.Claim(new PayorReceiptClaim("ZZ1", 1, 10, ContraCase: "ZZ2", ContraVersion: "1", ContraAmount: 5)));
    }

    [Fact]
    public void A_remark_line_goes_to_the_last_remark_column_in_use_or_the_next_one()
    {
        Assert.Equal((0, "~x"), PayorReceiptRules.AppendRemark([], "~x"));
        Assert.Equal((0, "a~x"), PayorReceiptRules.AppendRemark(["a", null, null], "~x"));
        Assert.Equal((2, "b~x"), PayorReceiptRules.AppendRemark(["a", "", "b"], "~x"));
        Assert.Equal((1, "~x"), PayorReceiptRules.AppendRemark([new string('a', 1999)], "~x"));
        Assert.Equal((6, new string('a', 3799) + "~"), PayorReceiptRules.AppendRemark([null, null, null, null, null, null, new string('a', 3799)], "~x"));
        Assert.StartsWith("~02/05/2030   Claim Receipt  -  RF1 on 01/05/2030 ", PayorReceiptRules.CaseRemark(new DateOnly(2030, 5, 2), "RF1", new DateOnly(2030, 5, 1), "TEST"));
    }
}

public class PayorReceiptTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/receipts/payor", Bordx = "NZZRC00001", Full = "ZZREC00001", Part = "ZZREC00002";
    private HttpClient Client(int position = 61) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static readonly PayorReceiptHeader Header = new(new DateOnly(2030, 5, 2), 1100, "CHQ123", "MR9", new DateOnly(2030, 5, 4), new DateOnly(2030, 5, 3), "AFFIN BANK", null);
    private static PayorReceiptSearch ByBordx => new(null, null, null, null, null, null, null, null, null, null, null, null, null, Bordx, Bordx);

    /// <summary>Two claims of payor ZZ on one bordereau: RM 1,000 and RM 300. The test user's authorisation is set by <paramref name="authorised"/>.</summary>
    private async Task SeedAsync(bool authorised = false)
    {
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM USR WHERE USRCode = @user) UPDATE USR SET AuthorisedStatus = @auth WHERE USRCode = @user
                ELSE INSERT INTO USR (USRCode, DBCode, AuthorisedStatus) SELECT TOP 1 @user, DBCode, @auth FROM USR
                """, new { user = WriteApiFactory.TestUser, auth = authorised ? "Y" : "N" });
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYCode, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
            VALUES (@bordx, '2030-04-20', 'ZZ', '0', '0', '0', '0', 0, 0, 0);
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMBordxAmt, BordxRefNo, ClaimCloseDate)
            VALUES (@full, 1, 'C', 1, 0, 0, 0, 1000, 0, 1000, @bordx, '2030-04-15'), (@part, 1, 'C', 1, 0, 0, 0, 300, 0, 300, @bordx, '2030-04-16');
            """, new { bordx = Bordx, full = Full, part = Part });
    }

    private async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<PayorReceiptResult> SaveAsync(PayorReceiptHeader header, params PayorReceiptClaim[] claims) =>
        await Ok<PayorReceiptResult>(await Client().PostAsJsonAsync(Url, new PayorReceiptSaveRequest(header, claims)));

    private async Task<string> SeqAsync(string sql)
    {
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        return (await pay.ExecuteScalarAsync<string>(sql))!.Trim();
    }

    [DatabaseFact]
    public async Task Lists_the_claims_of_a_bordereau_with_what_is_outstanding()
    {
        await SeedAsync();
        var listing = await Ok<PayorReceiptListing>(await Client().PostAsJsonAsync($"{Url}/search", ByBordx));
        Assert.Equal([Full, Part], listing.Lines.Select(l => l.CaseNumber));
        Assert.All(listing.Lines, l => { Assert.Null(l.ReceiptNo); Assert.Equal("ZZ", l.Payor); Assert.Equal(l.Amount, l.Outstanding); });
        var options = await Ok<PayorReceiptOptions>(await Client().GetAsync($"{Url}/options"));
        Assert.Contains("AFFIN BANK", options.Banks);
        Assert.False(options.Authorised);
    }

    [DatabaseFact]
    public async Task Saving_receipts_the_claims_under_new_receipt_and_voucher_numbers()
    {
        await SeedAsync();
        var rec = int.Parse(await SeqAsync("SELECT RecSeqNo FROM RecSeq WHERE RCInd = ''"));
        var pv = int.Parse(await SeqAsync("SELECT TOP 1 PVSeqNo FROM PVSeq"));
        var result = await SaveAsync(Header, new PayorReceiptClaim(Full, 1, 1000, BankCharge: 5), new PayorReceiptClaim(Part, 1, 100, ContraCase: "ZZREC00009", ContraVersion: "1", ContraAmount: 20, CaseRemark: "short"));

        Assert.Equal($"RF{rec + 1:00000}", result.ReceiptNo);
        Assert.Equal($"CF{pv + 1:00000}", result.PvNo);
        var full = result.Lines.Single(l => l.CaseNumber == Full);
        var part = result.Lines.Single(l => l.CaseNumber == Part);
        Assert.Equal(("F", 1000m, 0m, 1100m, 5, 2030), (full.Status, full.Received, full.Outstanding, full.ReceiptAmount!.Value, full.AccountMonth!.Value, full.AccountYear!.Value));
        Assert.Equal(("P", 100m, 200m, "ZZREC00009", 20m, "short"), (part.Status, part.Received, part.Outstanding, part.ContraCase, part.ContraAmount!.Value, part.CaseRemark));
        Assert.Equal(("MR9", "AFFIN BANK", new DateOnly(2030, 5, 4)), (full.ChequeNoMr, full.Bank, full.PayChequeDate!.Value));

        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        var hosp = (await pay.QueryAsync<(string Pv, decimal Paid, string Status)>("SELECT PVHOSNo, HOSPaidAmt, PVPayHOSStatus FROM Payment2HOS WHERE CASNumber IN (@Full, @Part)", new { Full, Part })).ToList();
        Assert.Equal([(result.PvNo!, 1000m, "F")], hosp.Select(h => (h.Pv, h.Paid, h.Status.Trim())));
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Contains($"Claim Receipt  -  {result.ReceiptNo} on 02/05/2030", await main.ExecuteScalarAsync<string>("SELECT CASORemarks FROM CASRemarks WHERE CASNumber = @Full", new { Full }));
        Assert.Single(factory.Audit.Entries, e => e.Action == "Receipt.Payor.Save");
    }

    [DatabaseFact]
    public async Task A_paid_claim_needs_an_authorised_user_and_a_second_receipt_completes_a_partial_one()
    {
        await SeedAsync();
        await SaveAsync(Header with { Amount = 1100 }, new PayorReceiptClaim(Full, 1, 1000), new PayorReceiptClaim(Part, 1, 100));
        var again = await Client().PostAsJsonAsync(Url, new PayorReceiptSaveRequest(Header with { Amount = 1000 }, [new PayorReceiptClaim(Full, 1, 1000)]));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Record has been paid", await again.Content.ReadAsStringAsync());

        var second = await SaveAsync(Header with { Amount = 200, ChequeNo = "CHQ2" }, new PayorReceiptClaim(Part, 1, 200));
        var listing = await Ok<PayorReceiptListing>(await Client().PostAsJsonAsync($"{Url}/search", ByBordx));
        var part = listing.Lines.Where(l => l.CaseNumber == Part).ToList();
        Assert.Equal(2, part.Count);
        Assert.All(part, l => Assert.Equal(("F", 300m, 0m), (l.Status, l.TotalReceived, l.Outstanding)));
        Assert.Contains(part, l => l.ReceiptNo == second.ReceiptNo && l.ChequeNo == "CHQ2");
        var paid = await Ok<PayorReceiptListing>(await Client().PostAsJsonAsync($"{Url}/search", ByBordx with { Status = PayorReceiptStatus.Unpaid }));
        Assert.Empty(paid.Lines);
    }

    [DatabaseFact]
    public async Task Update_changes_the_receipt_header_and_the_claim_details()
    {
        await SeedAsync();
        var saved = await SaveAsync(Header, new PayorReceiptClaim(Full, 1, 1000), new PayorReceiptClaim(Part, 1, 100));
        var res = await Client().PutAsJsonAsync($"{Url}/{saved.ReceiptNo}", new PayorReceiptUpdateRequest(Header with { ChequeNo = "CHQ999", Amount = 1300, ReceiptDate = new DateOnly(2030, 6, 1) },
            new PayorReceiptClaim(Part, 1, 300, GainLoss: 1.5m)));
        var updated = await Ok<PayorReceiptResult>(res);
        Assert.All(updated.Lines, l => Assert.Equal(("CHQ999", 1300m, new DateOnly(2030, 6, 1), 6), (l.ChequeNo, l.ReceiptAmount!.Value, l.ReceiptDate!.Value, l.AccountMonth!.Value)));
        var part = updated.Lines.Single(l => l.CaseNumber == Part);
        Assert.Equal(("F", 300m, 1.5m), (part.Status, part.Received, part.GainLoss!.Value));
        Assert.Equal(1000m, updated.Lines.Single(l => l.CaseNumber == Full).Received);

        var wrong = await Client().PutAsJsonAsync($"{Url}/{saved.ReceiptNo}", new PayorReceiptUpdateRequest(Header, new PayorReceiptClaim("ZZREC00099", 1, 1)));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [DatabaseFact]
    public async Task Save_is_refused_without_a_remark_when_the_amounts_differ_and_without_the_screen()
    {
        await SeedAsync();
        var res = await Client().PostAsJsonAsync(Url, new PayorReceiptSaveRequest(Header with { Amount = 999 }, [new PayorReceiptClaim(Full, 1, 1000)]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Remark", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(59).GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new PayorReceiptSearch(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null))).StatusCode);
    }
}
