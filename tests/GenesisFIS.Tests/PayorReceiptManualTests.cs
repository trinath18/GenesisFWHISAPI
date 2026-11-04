using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Tests;

public class PayorReceiptManualRuleTests
{
    private static readonly PayorReceiptHeader Header = new(new DateOnly(2030, 5, 2), 1300, "CHQ1", null, null, null, null, null);

    [Fact]
    public void Manual_save_checks_follow_the_legacy_form()
    {
        Assert.Equal("claims", PayorReceiptRules.Validate(new ManualPayorReceiptRequest("RM1", null, Header))?.Field);
        Assert.Equal("claims", PayorReceiptRules.Validate(new ManualPayorReceiptRequest("RM1", null, Header, [new("ZZ1", 1, 1)], ["B1"]))?.Field);
        Assert.Equal("receiptNo", PayorReceiptRules.Validate(new ManualPayorReceiptRequest(" ", null, Header, Bordereaux: ["B1"]))?.Field);
        Assert.Equal("receiptNo", PayorReceiptRules.Validate(new ManualPayorReceiptRequest("RM1234567890ABCD", null, Header, Bordereaux: ["B1"]))?.Field);
        Assert.Equal("chequeNo", PayorReceiptRules.Validate(new ManualPayorReceiptRequest("RM1", null, Header with { ChequeNo = null }, Bordereaux: ["B1"]))?.Field);
        Assert.Null(PayorReceiptRules.Validate(new ManualPayorReceiptRequest("RM1", null, Header, Bordereaux: ["B1"])));
        Assert.Equal("remark", PayorReceiptRules.Remark(Header, 1000)?.Field);
        Assert.Null(PayorReceiptRules.Remark(Header, 1300));
    }

    [Fact]
    public void A_bordereau_is_fully_received_when_all_its_claims_are()
    {
        Assert.Equal("F", PayorReceiptRules.BordxStatus(["F", "F"]));
        Assert.Equal("P", PayorReceiptRules.BordxStatus(["F", null]));
        Assert.Equal("P", PayorReceiptRules.BordxStatus(["P"]));
        Assert.Null(PayorReceiptRules.BordxStatus([null, null]));
    }
}

public class PayorReceiptManualTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/receipts/payor", Bordx = "NZZRM00001", Full = "ZZRCM00001", Part = "ZZRCM00002";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(61));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static readonly PayorReceiptHeader Header = new(new DateOnly(2030, 5, 2), 1300, "CHQM1", null, null, new DateOnly(2030, 5, 3), null, null);
    private static PayorReceiptSearch ByBordx => new(null, null, null, null, null, null, null, null, null, null, null, null, null, Bordx, Bordx);

    /// <summary>Two claims of payor ZZ on one bordereau: RM 1,000 and RM 300; the test user is not authorised.</summary>
    private async Task SeedAsync()
    {
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM USR WHERE USRCode = @user) UPDATE USR SET AuthorisedStatus = 'N' WHERE USRCode = @user
                ELSE INSERT INTO USR (USRCode, DBCode, AuthorisedStatus) SELECT TOP 1 @user, DBCode, 'N' FROM USR
                """, new { user = WriteApiFactory.TestUser });
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYCode, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
            VALUES (@bordx, '2030-04-20', 'ZZ', '0', '0', '0', '0', 0, 0, 0);
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMBordxAmt, BordxRefNo, ClaimCloseDate)
            VALUES (@full, 1, 'C', 1, 0, 0, 0, 1000, 0, 1000, @bordx, '2030-04-15'), (@part, 1, 'C', 1, 0, 0, 0, 300, 0, 300, @bordx, '2030-04-16');
            """, new { bordx = Bordx, full = Full, part = Part });
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(string? Status, DateTime? Date)> BordxStatusAsync()
    {
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        return await ws.QuerySingleAsync<(string?, DateTime?)>("SELECT BordxPAYFrStatus, BordxPAYRecDate FROM Bordx WHERE BordxRefNo = @Bordx", new { Bordx });
    }

    [DatabaseFact]
    public async Task A_whole_bordereau_is_receipted_under_the_typed_numbers()
    {
        await SeedAsync();
        var bordereaux = await Ok<PayorReceiptBordxListing>(await Client().PostAsJsonAsync($"{Url}/search-bordx", ByBordx));
        var b = Assert.Single(bordereaux.Bordereaux);
        Assert.Equal((Bordx, 2, 1300m, 1300m, (string?)null), (b.BordxNo, b.Claims, b.Amount, b.Outstanding, b.Status));

        var result = await Ok<PayorReceiptResult>(await Client().PostAsJsonAsync($"{Url}/manual", new ManualPayorReceiptRequest("RMZZ001", "PVZZ001", Header, Bordereaux: [Bordx])));
        Assert.Equal(("RMZZ001", "PVZZ001"), (result.ReceiptNo, result.PvNo));
        Assert.All(result.Lines, l => Assert.Equal(("F", 0m, "PVZZ001", "CHQM1"), (l.Status, l.Outstanding, l.PvNo, l.ChequeNo)));
        Assert.Equal(("F", new DateTime(2030, 5, 2)), await BordxStatusAsync());
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        Assert.Equal(0, await pay.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Payment2HOS WHERE CASNumber IN (@Full, @Part)", new { Full, Part }));

        var paid = await Ok<PayorReceiptBordxListing>(await Client().PostAsJsonAsync($"{Url}/search-bordx", ByBordx with { Status = PayorReceiptStatus.Paid }));
        Assert.Equal("RMZZ001", Assert.Single(paid.Bordereaux).ReceiptNos);
        var again = await Client().PostAsJsonAsync($"{Url}/manual", new ManualPayorReceiptRequest("RMZZ002", null, Header, Bordereaux: [Bordx]));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Record has been paid", await again.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Ticked_claims_set_the_bordereau_partly_received_and_a_used_number_is_refused()
    {
        await SeedAsync();
        var result = await Ok<PayorReceiptResult>(await Client().PostAsJsonAsync($"{Url}/manual",
            new ManualPayorReceiptRequest("RMZZ003", null, Header with { Amount = 100 }, [new PayorReceiptClaim(Part, 1, 100, ContraCase: "ZZRCM00009", ContraVersion: "1", ContraAmount: 5)])));
        var part = Assert.Single(result.Lines);
        Assert.Equal(("P", 200m, "ZZRCM00009", (string?)null), (part.Status, part.Outstanding, part.ContraCase, part.PvNo));
        Assert.Equal("P", (await BordxStatusAsync()).Status);

        var used = await Client().PostAsJsonAsync($"{Url}/manual", new ManualPayorReceiptRequest("RMZZ003", null, Header with { Amount = 1000 }, [new PayorReceiptClaim(Full, 1, 1000)]));
        Assert.Equal(HttpStatusCode.Conflict, used.StatusCode);
        var partly = await Client().PostAsJsonAsync($"{Url}/manual", new ManualPayorReceiptRequest("RMZZ004", null, Header, Bordereaux: [Bordx]));
        Assert.Equal(HttpStatusCode.Conflict, partly.StatusCode);
        Assert.Contains("Partial Paid", await partly.Content.ReadAsStringAsync());
        var noRemark = await Client().PostAsJsonAsync($"{Url}/manual", new ManualPayorReceiptRequest("RMZZ005", null, Header with { Amount = 999 }, [new PayorReceiptClaim(Full, 1, 1000)]));
        Assert.Equal(HttpStatusCode.BadRequest, noRemark.StatusCode);
    }
}
