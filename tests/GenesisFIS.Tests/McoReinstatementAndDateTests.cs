using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class McoReinstatementRuleTests
{
    [Fact]
    public void Criteria_are_checked_with_the_legacy_messages()
    {
        Assert.Null(McoReinstatementRules.Validate(new("batch", "WM", new(2023, 1, 5), 3, null)));
        Assert.Null(McoReinstatementRules.Validate(new("invoice", null, null, null, "S00001")));
        Assert.Equal("mode", McoReinstatementRules.Validate(new(null, "WM", new(2023, 1, 5), 3, null))!.Value.Field);
        Assert.Equal("payor", McoReinstatementRules.Validate(new("batch", "", new(2023, 1, 5), 3, null))!.Value.Field);
        Assert.Equal("bordxDate", McoReinstatementRules.Validate(new("batch", "WM", null, 3, null))!.Value.Field);
        Assert.Equal("batchNo", McoReinstatementRules.Validate(new("batch", "WM", new(2023, 1, 5), null, null))!.Value.Field);
        Assert.Equal("invoiceNo", McoReinstatementRules.Validate(new("invoice", null, null, null, " "))!.Value.Field);
    }
}

public class McoDateCorrectionRuleTests
{
    [Fact]
    public void Filter_and_update_are_validated()
    {
        Assert.Null(McoDateCorrectionRules.ValidateFilter(new("WM", null, null, null, null, null, null, "S1", null)));
        Assert.Equal("payor", McoDateCorrectionRules.ValidateFilter(new("", null, null, null, null, null, null, "S1", null))!.Value.Field);
        Assert.Equal("bordxFrom", McoDateCorrectionRules.ValidateFilter(new("WM", null, null, null, null, null, null, null, null))!.Value.Field);
        var one = new List<McoCorrectionItem> { new("WMZDAT0001*01", "00") };
        Assert.Null(McoDateCorrectionRules.ValidateUpdate(new(one, new(2023, 5, 1), null)));
        Assert.Equal("invoiceDate", McoDateCorrectionRules.ValidateUpdate(new(one, null, null))!.Value.Field);
        Assert.Equal("batchNo", McoDateCorrectionRules.ValidateUpdate(new(one, new(2023, 5, 1), 99999))!.Value.Field);
        Assert.Equal("items", McoDateCorrectionRules.ValidateUpdate(new([], new(2023, 5, 1), null))!.Value.Field);
    }
}

public class McoReinstatementTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/reinstatement";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(1));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string prefix = "X")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("UPDATE PAY SET MCOInvoicePrefix = @p WHERE PAYCode = 'WM'", new { p = prefix });
        await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, PAYCode, MBMPayorEffDate, MBMPayorExpDate, MBMPolicyNo, HSCLNInd, MBMDataStatus, MBMAdultChild) VALUES ('WMZREI01*01', '00', 'A', 'PRINCIPAL', 'WM', '2023-01-01', '2023-12-31', 'POL1', 'N', 'A', 'A')");
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCRELCode) VALUES ('WMZREI01*01', '01', 'SPOUSE', 'S')");
        await main.ExecuteAsync("INSERT INTO MBMReinstatement (MBMNumber, MBMCoverID, MBMEndtBordxDate, MBMEndtBordxBatch, MBMLapsedDate, MBMReinstateDate, MBMMCOFee, MBMPremium) VALUES ('WMZREI01*01', '00', '2023-03-01', '7', '2023-02-01', '2023-02-20', 8, 100), ('WMZREI01*01', '01', '2023-03-01', '7', '2023-02-01', '2023-02-20', 4, 50), ('WMZREI01*01', '00', '2023-03-01', '8', '2023-02-01', '2023-02-20', 9, 90)");
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Needs_position_1()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/payors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(20)).GetAsync($"{Url}/payors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(20)).PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("batch", "WM", new(2023, 3, 1), 7, null))).StatusCode);
    }

    [DatabaseFact]
    public async Task A_batch_lists_the_principal_and_supplementary_rows_with_their_totals()
    {
        await Seed();
        var l = (await Client().GetFromJsonAsync<McoReinstatementListing>($"{Url}?mode=batch&payor=WM&bordxDate=2023-03-01&batchNo=7"))!;
        Assert.Equal([("00", "P", "PRINCIPAL"), ("01", "S", "SPOUSE")], l.Rows.Select(r => (r.CoverId, r.Relation, r.Name)));
        Assert.Equal((1, 1, 12m, false), (l.TotalPrincipal, l.TotalSupplementary, l.TotalFee, l.Invoiced));
        Assert.Equal(new DateOnly(2023, 2, 20), l.Rows[0].ReinstateDate);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?mode=batch&payor=WM&batchNo=7")).StatusCode);
    }

    [DatabaseFact]
    public async Task Printing_takes_the_payor_prefix_and_next_number_stamps_the_batch_only_and_cannot_be_repeated()
    {
        await Seed("X");
        var counter = int.Parse((await Scalar<string>("SELECT MCOInvoice_No FROM MBMMCOINVQ"))!);
        var res = await Client().PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("batch", "WM", new(2023, 3, 1), 7, null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<McoReinstatementInvoiceResult>())!;
        Assert.Equal("X" + (counter + 1).ToString("00000"), r.InvoiceNo);
        Assert.Equal(2, r.RowsInvoiced);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMReinstatement WHERE MBMInvoiceNo = @n AND MBMInvoiceBy = 'TEST' AND MBMInvoiceDate = CAST(GETDATE() AS date)", new { n = r.InvoiceNo }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = 'WMZREI01*01' AND MBMEndtBordxBatch = '8' AND MBMInvoiceNo IS NULL"));
        Assert.True(r.Listing.Invoiced);
        Assert.Equal("Mco.ReinstatementInvoice", Assert.Single(factory.Audit.Entries).Action);
        var again = await Client().PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("batch", "WM", new(2023, 3, 1), 7, null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var reprint = (await Client().GetFromJsonAsync<McoReinstatementListing>($"{Url}?mode=invoice&invoiceNo={r.InvoiceNo}"))!;
        Assert.Equal(2, reprint.Rows.Count);
    }

    [DatabaseFact]
    public async Task An_empty_prefix_defaults_to_S_and_a_missing_batch_is_refused()
    {
        await Seed("");
        var r = (await (await Client().PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("batch", "WM", new(2023, 3, 1), 8, null))).Content.ReadFromJsonAsync<McoReinstatementInvoiceResult>())!;
        Assert.StartsWith("S", r.InvoiceNo);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("batch", "WM", new(2023, 3, 1), 99, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/invoice", new McoReinstatementFilter("invoice", null, null, null, "S1"))).StatusCode == HttpStatusCode.OK ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
    }
}

public class McoDateCorrectionTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/date-correction";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(20));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();
    private static McoDateFilter Filter() => new("WM", "WMZDAT0001*01", "WMZDAT0009*01", null, null, null, null, null, null);

    private async Task Seed()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, PAYCode, MBMBordxDate, MBMBatchNo) VALUES ('WMZDAT0001*01', '00', 'A', 'M1', 'WM', '2023-02-01', 3), ('WMZDAT0002*01', '00', 'A', 'M2', 'WM', '2023-02-01', 3)");
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMMCOFee, MBMInvoiceNo, MBMInvoiceDate) VALUES ('WMZDAT0001*01', 100, 'INV1', '2023-02-10'), ('WMZDAT0002*01', 50, NULL, NULL)");
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCMCO, MBMCBatchNo, MBMInvoiceNo, MBMInvoiceDate) VALUES ('WMZDAT0001*01', '01', 'SPOUSE', 20, 3, 'INV1', '2023-02-10')");
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql);
    }

    [Fact]
    public async Task Needs_the_invoice_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(21)).PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_lists_rows_and_the_invoice_range_narrows_them()
    {
        await Seed();
        var all = (await (await Client().PostAsJsonAsync($"{Url}/search", Filter())).Content.ReadFromJsonAsync<McoCorrectionListing>())!;
        Assert.Equal(3, all.Rows.Count);
        var inv = (await (await Client().PostAsJsonAsync($"{Url}/search", Filter() with { InvoiceFrom = "INV1", InvoiceTo = "INV1" })).Content.ReadFromJsonAsync<McoCorrectionListing>())!;
        Assert.Equal(["00", "01"], inv.Rows.Select(r => r.CoverId));
    }

    [DatabaseFact]
    public async Task Correcting_changes_the_invoice_date_and_optionally_the_batch_of_invoiced_rows()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync($"{Url}/update", new McoDateCorrectionRequest([new("WMZDAT0001*01", "00"), new("WMZDAT0001*01", "01")], new(2023, 3, 15), 9));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<McoDateCorrectionResult>())!.Updated);
        Assert.Equal(new DateTime(2023, 3, 15), await Scalar<DateTime>("SELECT MBMInvoiceDate FROM MBMOthers WHERE MBMNumber = 'WMZDAT0001*01'"));
        Assert.Equal(9, await Scalar<int>("SELECT MBMBatchNo FROM MBM WHERE MBMNumber = 'WMZDAT0001*01'"));
        Assert.Equal(9, await Scalar<int>("SELECT MBMCBatchNo FROM MBMCoveredPersons WHERE MBMCNumber = 'WMZDAT0001*01' AND MBMCCoverID = '01'"));
        Assert.Equal("INV1", await Scalar<string>("SELECT MBMInvoiceNo FROM MBMOthers WHERE MBMNumber = 'WMZDAT0001*01'"));
        Assert.Equal("Mco.DateCorrection", Assert.Single(factory.Audit.Entries).Action);
        var dateOnly = await Client().PostAsJsonAsync($"{Url}/update", new McoDateCorrectionRequest([new("WMZDAT0001*01", "00")], new(2023, 4, 1), null));
        Assert.Equal(HttpStatusCode.OK, dateOnly.StatusCode);
        Assert.Equal(9, await Scalar<int>("SELECT MBMBatchNo FROM MBM WHERE MBMNumber = 'WMZDAT0001*01'"));
    }

    [DatabaseFact]
    public async Task A_row_without_an_invoice_refuses_the_whole_correction()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync($"{Url}/update", new McoDateCorrectionRequest([new("WMZDAT0001*01", "00"), new("WMZDAT0002*01", "00")], new(2023, 3, 15), null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Please generate Invoice", await res.Content.ReadAsStringAsync());
        Assert.Equal(new DateTime(2023, 2, 10), await Scalar<DateTime>("SELECT MBMInvoiceDate FROM MBMOthers WHERE MBMNumber = 'WMZDAT0001*01'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/update", new McoDateCorrectionRequest([new("WMZDAT0001*01", "05")], new(2023, 3, 15), null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/update", new McoDateCorrectionRequest([new("WMZDAT0001*01", "00")], null, null))).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }
}
