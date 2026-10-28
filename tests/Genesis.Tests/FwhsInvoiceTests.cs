using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class FwhsInvoiceRuleTests
{
    private static FwhsInvoiceFilter Ok() => new("WM", "new", new(2023, 1, 1), new(2023, 1, 31), null, "none");

    [Fact]
    public void Valid_filters_pass() { Assert.Null(FwhsInvoiceRules.Validate(Ok())); Assert.Null(FwhsInvoiceRules.Validate(new("WM", "reprint", null, null, "FWMA02553", "gst"))); }

    [Fact]
    public void Missing_criteria_use_the_legacy_messages()
    {
        Assert.Equal("payor", FwhsInvoiceRules.Validate(Ok() with { Payor = "" })!.Value.Field);
        Assert.Equal("from", FwhsInvoiceRules.Validate(Ok() with { From = null })!.Value.Field);
        Assert.Equal("to", FwhsInvoiceRules.Validate(Ok() with { To = new(2022, 1, 1) })!.Value.Field);
        Assert.Equal("invoiceNo", FwhsInvoiceRules.Validate(Ok() with { Mode = "reprint", InvoiceNo = "" })!.Value.Field);
        Assert.Equal("mode", FwhsInvoiceRules.Validate(Ok() with { Mode = null })!.Value.Field);
        Assert.Equal("tax", FwhsInvoiceRules.Validate(Ok() with { Tax = "vat" })!.Value.Field);
    }
}

public class FwhsInvoiceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/fwhs-invoice";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(20));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string billing, string medixTax = "N")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("UPDATE PAY SET PAYBillingMode = @b, PAYMedixTax = @t WHERE PAYCode = 'WM'", new { b = billing, t = medixTax });
        await main.ExecuteAsync("""
            INSERT INTO FWHSTPAFee (CoInsurerRefNo, CoGRPPaycode, CoPolicyNo, CoPolicyDate, CoPAYDateSubmission, CoPAYBatchNo, CoTotalFW, CoTotalMCOFee, CoTPCATax, CoEndorseNo, CoEndorseDate) VALUES
              ('ZZ1', 'WM', 'ZZPOL1', '2023-01-10', '2023-01-12', 'ZZB1', 2, 100, 6, NULL, NULL),
              ('ZZ2', 'WM', 'ZZPOL2', '2023-01-20', '2023-01-22', 'ZZB1', 3, 50, 3, NULL, NULL),
              ('ZZ3', 'WM', 'ZZPOL1', '2023-01-10', '2023-02-20', 'ZZB2', 1, 20, 1, 'ZZEND1', '2023-01-25'),
              ('ZZ4', 'WM', 'ZZPOL3', '2023-03-01', '2023-03-03', 'ZZB3', 4, 80, 5, NULL, NULL)
            """);
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Needs_the_invoice_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}?payor=WM&mode=new")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(21));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}?payor=WM&mode=new&from=2023-01-01&to=2023-01-31")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, new FwhsInvoiceFilter("WM", "new", new(2023, 1, 1), new(2023, 1, 31), null, "none"))).StatusCode);
    }

    [DatabaseFact]
    public async Task Policy_date_payors_list_new_business_by_policy_date_and_endorsements_by_endorsement_date()
    {
        await Seed("I", "Y");
        var l = (await Client().GetFromJsonAsync<FwhsInvoiceListing>($"{Url}?payor=WM&mode=new&from=2023-01-01&to=2023-01-31&tax=none"))!;
        Assert.Equal("I", l.BillingMode);
        Assert.Equal(3, l.Rows.Count);
        Assert.Equal((6, 170m, 10m, 180m), (l.TotalWorkers, l.TotalFee, l.TotalTax, l.Total));
        Assert.Equal(1, l.Rows.Count(r => r.Kind == "Endorsement"));
    }

    [DatabaseFact]
    public async Task Submission_date_payors_list_everything_submitted_in_the_range_and_tax_follows_the_payor_flag()
    {
        await Seed("T", "N");
        var l = (await Client().GetFromJsonAsync<FwhsInvoiceListing>($"{Url}?payor=WM&mode=new&from=2023-01-01&to=2023-01-31&tax=none"))!;
        Assert.Equal(2, l.Rows.Count);
        Assert.Equal((5, 150m, 0m, 150m), (l.TotalWorkers, l.TotalFee, l.TotalTax, l.Total));
        var gst = (await Client().GetFromJsonAsync<FwhsInvoiceListing>($"{Url}?payor=WM&mode=new&from=2023-01-01&to=2023-01-31&tax=gst"))!;
        Assert.Equal((9m, 159m), (gst.TotalTax, gst.Total));
        var sst = (await Client().GetFromJsonAsync<FwhsInvoiceListing>($"{Url}?payor=WM&mode=new&from=2023-01-01&to=2023-01-31&tax=sst"))!;
        Assert.Equal((0m, 150m), (sst.TotalTax, sst.Total));
    }

    [DatabaseFact]
    public async Task Invoicing_takes_the_next_number_stamps_exactly_the_listed_rows_and_cannot_be_repeated()
    {
        await Seed("T");
        var before = int.Parse((await Scalar<string>("SELECT MCOInvoice_No FROM MBMMCOINVQ"))!);
        var res = await Client().PostAsJsonAsync(Url, new FwhsInvoiceFilter("WM", "new", new(2023, 1, 1), new(2023, 1, 31), null, "gst"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<FwhsInvoiceResult>())!;
        Assert.Equal("FWMA" + (before + 1).ToString("00000"), r.InvoiceNo);
        Assert.Equal(2, r.RowsInvoiced);
        Assert.Equal((before + 1).ToString("00000"), await Scalar<string>("SELECT MCOInvoice_No FROM MBMMCOINVQ"));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoInvoiceNumber = @n AND CoInvoiceBy = 'TEST' AND CoInvoiceDate = CAST(GETDATE() AS date)", new { n = r.InvoiceNo }));
        Assert.Equal(9m, await Scalar<decimal>("SELECT SUM(GSTAmount) FROM FWHSTPAFee WHERE CoInvoiceNumber = @n", new { n = r.InvoiceNo }));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoGRPPaycode = 'WM' AND CoInsurerRefNo LIKE 'ZZ%' AND (CoInvoiceNumber IS NULL OR CoInvoiceNumber = '')"));
        Assert.Equal((150m, 9m, 159m), (r.Listing.TotalFee, r.Listing.TotalTax, r.Listing.Total));
        Assert.Equal("Mco.FwhsInvoice", Assert.Single(factory.Audit.Entries).Action);
        var again = await Client().PostAsJsonAsync(Url, new FwhsInvoiceFilter("WM", "new", new(2023, 1, 1), new(2023, 1, 31), null, "gst"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("no uninvoiced", await again.Content.ReadAsStringAsync());
        var reprint = (await Client().GetFromJsonAsync<FwhsInvoiceListing>($"{Url}?payor=WM&mode=reprint&invoiceNo={r.InvoiceNo}&tax=gst"))!;
        Assert.Equal((2, 150m, 9m), (reprint.Rows.Count, reprint.TotalFee, reprint.TotalTax));
    }

    [DatabaseFact]
    public async Task Bad_requests_change_nothing()
    {
        await Seed("T");
        var counter = await Scalar<string>("SELECT MCOInvoice_No FROM MBMMCOINVQ");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, new FwhsInvoiceFilter("WM", "new", null, null, null, "none"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, new FwhsInvoiceFilter("QQ", "new", new(2023, 1, 1), new(2023, 1, 31), null, "none"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?payor=WM&mode=reprint")).StatusCode);
        Assert.Equal(counter, await Scalar<string>("SELECT MCOInvoice_No FROM MBMMCOINVQ"));
        Assert.Empty(factory.Audit.Entries);
    }
}
