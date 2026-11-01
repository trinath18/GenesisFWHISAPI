using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class McoReceiptRuleTests
{
    private static McoReceiptRequest M() => new("M", null, new(2023, 5, 1), 100m, "CHQ1", new(2023, 4, 28), "note", null, ["SZZ001"]);

    [Fact]
    public void Valid_requests_pass()
    {
        Assert.Null(McoReceiptRules.Validate(M(), false));
        Assert.Null(McoReceiptRules.Validate(M() with { Type = "F", Invoices = null }, false));
        Assert.Null(McoReceiptRules.Validate(M() with { ReceiptNo = "MB-9" }, true));
    }

    [Fact]
    public void Each_problem_names_its_field()
    {
        Assert.Equal("type", McoReceiptRules.Validate(M() with { Type = "" }, false)!.Value.Field);
        Assert.Equal("receiptDate", McoReceiptRules.Validate(M() with { ReceiptDate = null }, false)!.Value.Field);
        Assert.Equal("amount", McoReceiptRules.Validate(M() with { Amount = null }, false)!.Value.Field);
        Assert.Equal("amount", McoReceiptRules.Validate(M() with { Amount = 0 }, false)!.Value.Field);
        Assert.Equal("receiptNo", McoReceiptRules.Validate(M() with { ReceiptNo = " " }, true)!.Value.Field);
        Assert.Equal("receiptNo", McoReceiptRules.Validate(M() with { ReceiptNo = "a'b" }, true)!.Value.Field);
        Assert.Equal("invoices", McoReceiptRules.Validate(M() with { Invoices = [] }, false)!.Value.Field);
        Assert.Equal("invoices", McoReceiptRules.Validate(M() with { Invoices = ["A", "a"] }, false)!.Value.Field);
        Assert.Equal("accountCode", McoReceiptRules.Validate(M() with { Type = "O", Invoices = null }, false)!.Value.Field);
        Assert.Equal("remarks", McoReceiptRules.Validate(M() with { Remarks = new string('x', 51) }, false)!.Value.Field);
        Assert.Equal("chequeNo", McoReceiptRules.Validate(M() with { ChequeNo = new string('x', 21) }, false)!.Value.Field);
    }
}

public class McoReceiptTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/receipts";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(19));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task SeedInvoices()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO MBMMCOInvoice (MBMNumber, MBMCovID, MCOInvNo, MCOInvDate, BillMCOAmt) VALUES ('WMZREC01*01', '00', 'SZZ001', '2023-04-01', 100), ('WMZREC02*01', '00', 'SZZ001', '2023-04-01', 50), ('WMZREC03*01', '00', 'SZZ002', '2023-04-02', 70)");
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMCRNote, MBMCRNoteDate) VALUES ('WMZREC03*01', 'CZZ001', '2023-04-05')");
        await main.ExecuteAsync("INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMMCORefund, MBMMCORefListPrinted, MBMRefInvoiced) VALUES ('WMZREC03*01', '00', 20, 0, 'N')");
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Needs_position_19()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/payors")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(20));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/payors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, new McoReceiptRequest("F", null, new(2023, 5, 1), 10m, null, null, null, null, null))).StatusCode);
    }

    [DatabaseFact]
    public async Task Invoice_search_totals_the_invoice_lines_lists_credit_notes_and_shows_what_was_received()
    {
        await SeedInvoices();
        var l = (await Client().GetFromJsonAsync<McoReceiptInvoiceListing>($"{Url}/invoices?payor=WM&dateFrom=2023-04-01&dateTo=2023-04-30&creditNotes=true&onlyOpen=false"))!;
        Assert.Equal([("SZZ001", 150m, "Invoice"), ("SZZ002", 70m, "Invoice"), ("CZZ001", 20m, "CreditNote")], l.Rows.Select(r => (r.Number, r.Total, r.Kind)).ToList().OrderBy(x => x.Number == "CZZ001" ? 2 : 0).ThenBy(x => x.Number));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}/invoices?payor=WM")).StatusCode);
    }

    [DatabaseFact]
    public async Task An_mco_receipt_gets_the_next_number_one_line_per_invoice_and_credit_notes_are_negative()
    {
        await SeedInvoices();
        var seq = int.Parse((await Scalar<string>(GenesisDb.Payment, "SELECT RecOthersSeqNo FROM RECOthersSeq"))!);
        var res = await Client().PostAsJsonAsync(Url, new McoReceiptRequest("M", null, new(2023, 5, 1), 200m, "CHQ77", new(2023, 4, 28), "test", null, ["SZZ001", "SZZ002", "CZZ001"]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<McoReceiptResult>())!;
        Assert.Equal("MB" + (seq + 1).ToString("00000"), r.ReceiptNo);
        Assert.Equal((3, 200m, 0m), (r.Lines, r.Allocated, r.Difference));
        Assert.Equal(3, await Scalar<int>(GenesisDb.Payment, "SELECT COUNT(*) FROM RECMCO WHERE ReceiptNo = @n AND ReceiptAmt = 200 AND ReceiptType = 'M' AND RECChqNo = 'CHQ77' AND RECChqDate = '28/04/2023' AND RecMCOLastUpdateUser = 'TEST'", new { n = r.ReceiptNo }));
        Assert.Equal(-20m, await Scalar<decimal>(GenesisDb.Payment, "SELECT INVRecAmt FROM RECMCO WHERE InvoiceNo = 'CZZ001'"));
        Assert.Equal(150m, await Scalar<decimal>(GenesisDb.Payment, "SELECT INVRecAmt FROM RECMCO WHERE InvoiceNo = 'SZZ001'"));
        Assert.Equal((seq + 1).ToString("00000"), await Scalar<string>(GenesisDb.Payment, "SELECT RecOthersSeqNo FROM RECOthersSeq"));
        var again = await Client().PostAsJsonAsync(Url, new McoReceiptRequest("M", null, new(2023, 5, 2), 150m, null, null, null, null, ["SZZ001"]));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already receipted", await again.Content.ReadAsStringAsync());
        Assert.Equal("McoReceipt.Create", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Other_receipts_use_the_default_account_codes_and_the_manual_variant_takes_a_unique_typed_number()
    {
        var f = (await (await Client().PostAsJsonAsync(Url, new McoReceiptRequest("F", null, new(2023, 5, 1), 164.38m, "MBB1", new(2023, 4, 1), "fine", null, null))).Content.ReadFromJsonAsync<McoReceiptResult>())!;
        Assert.Equal("6720", await Scalar<string>(GenesisDb.Payment, "SELECT RECAccCode FROM RECOthers WHERE ReceiptNo = @n AND ReceiptAmt = 164.38 AND RECChqNo = 'MBB1'", new { n = f.ReceiptNo }));
        var c = (await (await Client().PostAsJsonAsync(Url, new McoReceiptRequest("C", null, new(2023, 5, 1), 10m, null, null, null, null, null))).Content.ReadFromJsonAsync<McoReceiptResult>())!;
        Assert.Equal("2040", await Scalar<string>(GenesisDb.Payment, "SELECT RECAccCode FROM RECOthers WHERE ReceiptNo = @n AND ReceiptType = 'C'", new { n = c.ReceiptNo }));
        var manual = new McoReceiptRequest("O", "MAN-1", new(2023, 5, 1), 5m, null, null, null, "7001", null);
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync($"{Url}/manual", manual)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync($"{Url}/manual", manual)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/manual", manual with { ReceiptNo = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, manual with { ReceiptNo = null, AccountCode = null })).StatusCode);
    }

    [DatabaseFact]
    public async Task Editing_changes_the_header_of_every_line_and_deleting_removes_one_line_and_bad_requests_change_nothing()
    {
        await SeedInvoices();
        var r = (await (await Client().PostAsJsonAsync(Url, new McoReceiptRequest("M", null, new(2023, 5, 1), 220m, null, null, null, null, ["SZZ001", "SZZ002"]))).Content.ReadFromJsonAsync<McoReceiptResult>())!;
        var list = (await Client().GetFromJsonAsync<McoReceiptListing>($"{Url}?type=M&receiptFrom={r.ReceiptNo}&receiptTo={r.ReceiptNo}"))!;
        Assert.Equal(2, list.Rows.Count); Assert.Equal(220m, list.TotalAmount);
        var line = list.Rows.First(x => x.InvoiceNo == "SZZ001");
        var edit = await Client().PutAsJsonAsync($"{Url}/M/{line.Index}", new McoReceiptUpdate(new(2023, 6, 1), 225m, "NEWCHQ", new(2023, 5, 30), "edited", 140m));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(2, await Scalar<int>(GenesisDb.Payment, "SELECT COUNT(*) FROM RECMCO WHERE ReceiptNo = @n AND ReceiptAmt = 225 AND RECChqNo = 'NEWCHQ' AND RecMCORemarks = 'edited' AND ReceiptDate = '2023-06-01'", new { n = r.ReceiptNo }));
        Assert.Equal(140m, await Scalar<decimal>(GenesisDb.Payment, "SELECT INVRecAmt FROM RECMCO WHERE RecMCOIndex = @i", new { i = line.Index }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/M/{line.Index}", new McoReceiptUpdate(null, 1m, null, null, null, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync($"{Url}/M/99999999", new McoReceiptUpdate(new(2023, 6, 1), 1m, null, null, null, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().DeleteAsync($"{Url}/M/{line.Index}")).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Payment, "SELECT COUNT(*) FROM RECMCO WHERE ReceiptNo = @n", new { n = r.ReceiptNo }));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().DeleteAsync($"{Url}/M/{line.Index}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, new McoReceiptRequest("M", null, new(2023, 5, 1), 5m, null, null, null, null, ["NOSUCH"]))).StatusCode);
    }
}

