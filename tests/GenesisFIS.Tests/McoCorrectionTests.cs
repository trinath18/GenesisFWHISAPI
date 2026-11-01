using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class McoCorrectionRuleTests
{
    [Fact]
    public void Selection_and_fee_are_validated()
    {
        var one = new List<McoCorrectionItem> { new("WMZCOR0001*01", "00") };
        Assert.Null(McoCorrectionRules.Validate(new(one, 12.5m)));
        Assert.Null(McoCorrectionRules.Validate(new(one, 0m)));
        Assert.Equal("items", McoCorrectionRules.Validate(new([], 5m))!.Value.Field);
        Assert.Equal("items", McoCorrectionRules.Validate(new([new("a'b", "")], 5m))!.Value.Field);
        Assert.Equal("items", McoCorrectionRules.Validate(new([one[0], one[0]], 5m))!.Value.Field);
        Assert.Equal("fee", McoCorrectionRules.Validate(new(one, null))!.Value.Field);
        Assert.Equal("fee", McoCorrectionRules.Validate(new(one, -1m))!.Value.Field);
    }
}

public class McoCorrectionTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/correction";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(20));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();
    private static McoManualFilter Filter() => new("WM", "WMZCOR0001*01", "WMZCOR0009*01", null, null, null, null);

    private async Task Seed()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, PAYCode, MBMBordxDate, MBMBatchNo) VALUES ('WMZCOR0001*01', '00', 'A', 'M1', 'WM', '2023-02-01', 3), ('WMZCOR0002*01', '00', 'A', 'M2', 'WM', '2023-02-01', 3)");
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMMCOFee, MBMInvoiceNo) VALUES ('WMZCOR0001*01', 100, NULL), ('WMZCOR0002*01', 50, 'INV9')");
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCMCO) VALUES ('WMZCOR0001*01', '01', 'SPOUSE', 20)");
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql);
    }

    [Fact]
    public async Task Needs_the_mco_fee_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(21)).PostAsJsonAsync($"{Url}/search", Filter())).StatusCode);
    }

    [DatabaseFact]
    public async Task Search_lists_principals_and_covered_persons_with_their_invoice_numbers()
    {
        await Seed();
        var l = (await (await Client().PostAsJsonAsync($"{Url}/search", Filter())).Content.ReadFromJsonAsync<McoCorrectionListing>())!;
        Assert.Equal([("WMZCOR0001*01", "00", 100m, (string?)null), ("WMZCOR0001*01", "01", 20m, null), ("WMZCOR0002*01", "00", 50m, "INV9")], l.Rows.Select(r => (r.MemberNumber, r.CoverId, r.Mco ?? 0, r.InvoiceNo)));
    }

    [DatabaseFact]
    public async Task Correcting_sets_the_fee_on_the_selected_principal_and_covered_person_and_audits_before_and_after()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync($"{Url}/update", new McoCorrectionRequest([new("WMZCOR0001*01", "00"), new("WMZCOR0001*01", "01")], 12.5m));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<McoCorrectionResult>())!.Updated);
        Assert.Equal(12.5m, await Scalar<decimal>("SELECT MBMMCOFee FROM MBMOthers WHERE MBMNumber = 'WMZCOR0001*01'"));
        Assert.Equal(12.5m, await Scalar<decimal>("SELECT MBMCMCO FROM MBMCoveredPersons WHERE MBMCNumber = 'WMZCOR0001*01' AND MBMCCoverID = '01'"));
        Assert.Equal(50m, await Scalar<decimal>("SELECT MBMMCOFee FROM MBMOthers WHERE MBMNumber = 'WMZCOR0002*01'"));
        Assert.Equal("Mco.Correction", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task An_invoiced_or_missing_row_refuses_the_whole_correction()
    {
        await Seed();
        var invoiced = await Client().PostAsJsonAsync($"{Url}/update", new McoCorrectionRequest([new("WMZCOR0001*01", "00"), new("WMZCOR0002*01", "00")], 1m));
        Assert.Equal(HttpStatusCode.Conflict, invoiced.StatusCode);
        Assert.Contains("Invoice has been generated", await invoiced.Content.ReadAsStringAsync());
        Assert.Equal(100m, await Scalar<decimal>("SELECT MBMMCOFee FROM MBMOthers WHERE MBMNumber = 'WMZCOR0001*01'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/update", new McoCorrectionRequest([new("WMZCOR0001*01", "05")], 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/update", new McoCorrectionRequest([new("WMZCOR0001*01", "00")], null))).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }
}
