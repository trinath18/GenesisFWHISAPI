using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class McoManualRuleTests
{
    private static McoManualFilter Ok() => new("WM", "WMZMAN0001*01", null, null, null, null, null);

    [Fact]
    public void Needs_the_payor_and_one_more_criterion()
    {
        Assert.Null(McoManualRules.ValidateFilter(Ok()));
        Assert.Null(McoManualRules.ValidateFilter(new("WM", null, null, new(2023, 1, 1), null, null, null)));
        Assert.Equal("payor", McoManualRules.ValidateFilter(Ok() with { Payor = "" })!.Value.Field);
        Assert.Equal("bordxFrom", McoManualRules.ValidateFilter(new("WM", null, null, null, null, null, null))!.Value.Field);
        Assert.Equal("bordxTo", McoManualRules.ValidateFilter(new("WM", null, null, new(2023, 2, 1), new(2023, 1, 1), null, null))!.Value.Field);
        Assert.Equal("batchTo", McoManualRules.ValidateFilter(new("WM", null, null, null, null, 5, 2))!.Value.Field);
    }

    [Fact]
    public void Update_needs_invoice_number_and_date()
    {
        Assert.Null(McoManualRules.ValidateUpdate(new(Ok(), "INV1", new(2023, 4, 1))));
        Assert.Equal("invoiceNo", McoManualRules.ValidateUpdate(new(Ok(), " ", new(2023, 4, 1)))!.Value.Field);
        Assert.Equal("invoiceDate", McoManualRules.ValidateUpdate(new(Ok(), "INV1", null))!.Value.Field);
        Assert.Equal("filter", McoManualRules.ValidateUpdate(new(null, "INV1", new(2023, 4, 1)))!.Value.Field);
    }
}

public class McoManualInvoiceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/manual-invoice";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(148));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();
    private static McoManualFilter Filter() => new("WM", "WMZMAN0001*01", "WMZMAN0009*01", null, null, null, null);

    private async Task Seed(decimal coverMco = 20)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, PAYCode, MBMBordxDate, MBMBatchNo) VALUES ('WMZMAN0001*01', '00', 'A', 'M1', 'WM', '2023-02-01', 3), ('WMZMAN0002*01', '00', 'A', 'M2', 'WM', '2023-02-01', 3)");
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMMCOFee, MBMInvoiceNo) VALUES ('WMZMAN0001*01', 100, NULL), ('WMZMAN0002*01', 50, 'OLD1')");
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCMCO, MBMCBordxDate, MBMCBatchNo) VALUES ('WMZMAN0001*01', '01', 'SPOUSE', @mco, '2023-02-01', 3)", new { mco = coverMco });
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Needs_position_148()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(20)).PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(20)).PostAsJsonAsync($"{Url}/update", new McoManualUpdateRequest(Filter(), "INV", new(2023, 4, 1)))).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_lists_principals_and_covered_persons_without_an_invoice_and_totals_the_fees()
    {
        await Seed();
        var l = (await (await Client().PostAsJsonAsync($"{Url}/search", Filter())).Content.ReadFromJsonAsync<McoManualListing>())!;
        Assert.Equal([("WMZMAN0001*01", "00", 100m), ("WMZMAN0001*01", "01", 20m)], l.Rows.Select(r => (r.MemberNumber, r.CoverId, r.Mco)));
        Assert.Equal((120m, false, false), (l.TotalMco, l.HasZeroMco, l.Truncated));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new McoManualFilter("WM", null, null, null, null, null, null))).StatusCode);
    }

    [DatabaseFact]
    public async Task Update_stamps_every_found_row_and_leaves_invoiced_ones_alone()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync($"{Url}/update", new McoManualUpdateRequest(Filter(), " INV-77 ", new(2023, 4, 1)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<McoManualUpdateResult>())!.Updated);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = 'WMZMAN0001*01' AND MBMInvoiceNo = 'INV-77' AND MBMInvoiceDate = '2023-04-01' AND MBMInvoiceBy = 'TEST'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = 'WMZMAN0001*01' AND MBMInvoiceNo = 'INV-77' AND MBMInvoiceBy = 'TEST'"));
        Assert.Equal("OLD1", await Scalar<string>("SELECT MBMInvoiceNo FROM MBMOthers WHERE MBMNumber = 'WMZMAN0002*01'"));
        Assert.Equal("Mco.ManualInvoice", Assert.Single(factory.Audit.Entries).Action);
        var again = await Client().PostAsJsonAsync($"{Url}/update", new McoManualUpdateRequest(Filter(), "INV-78", new(2023, 4, 2)));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("No record found", await again.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task A_zero_fee_blocks_the_whole_update()
    {
        await Seed(coverMco: 0);
        var res = await Client().PostAsJsonAsync($"{Url}/update", new McoManualUpdateRequest(Filter(), "INV-79", new(2023, 4, 1)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Zero MCO", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMOthers WHERE MBMInvoiceNo = 'INV-79'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/update", new McoManualUpdateRequest(Filter(), "", new(2023, 4, 1)))).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }
}

