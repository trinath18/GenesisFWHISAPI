using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Dapper;
using GenesisFIS.Api.Features.Membership;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Reports.Membership;

namespace GenesisFIS.Tests;

public class LeafletRuleTests
{
    private static readonly LeafletCriteria Empty = new(null, null, null, null, null, null, null, null, null, null);
    private static LeafletRow Row(string n, bool printed) => new("XW", n, new DateOnly(2006, 4, 19), "P1", "N", 6, "NAME", printed);

    [Fact]
    public void Each_selection_needs_its_own_fields()
    {
        Assert.Equal("memberNo", LeafletRules.Validate(Empty with { Selection = LeafletLists.SingleMember })!.Value.Field);
        Assert.Null(LeafletRules.Validate(Empty with { Selection = LeafletLists.SingleMember, MemberNo = "XWGA00001*01" }));
        Assert.Equal("memberFrom", LeafletRules.Validate(Empty with { Selection = LeafletLists.MemberRange, MemberFrom = "A" })!.Value.Field);
        Assert.Equal("bordxDate", LeafletRules.Validate(Empty with { Selection = LeafletLists.BordxBatch, BordxDate = new(2006, 4, 19), BatchNo = 6 })!.Value.Field);
        Assert.Null(LeafletRules.Validate(Empty with { Selection = LeafletLists.BordxBatch, BordxDate = new(2006, 4, 19), BatchNo = 6, Payor = "XW" }));
        Assert.Equal("bordxFrom", LeafletRules.Validate(Empty with { Selection = LeafletLists.BordxRange, BordxFrom = new(2006, 1, 1), Payor = "XW" })!.Value.Field);
        Assert.Equal("selection", LeafletRules.Validate(Empty with { Selection = "Everything", MemberNo = "A" })!.Value.Field);
    }

    [Fact]
    public void Card_export_needs_a_narrowing_filter_and_rejects_bad_values()
    {
        Assert.Equal("memberNo", LeafletRules.Validate(Empty with { Payor = "XW" })!.Value.Field);
        Assert.Null(LeafletRules.Validate(Empty with { BordxFrom = new(2006, 1, 1), Payor = "XW" }));
        Assert.Equal("memberNo", LeafletRules.Validate(Empty with { MemberNo = "a'; --" })!.Value.Field);
        Assert.Equal("payor", LeafletRules.Validate(Empty with { MemberNo = "A1", Payor = "XWX" })!.Value.Field);
        Assert.Equal("sequence", LeafletRules.Validate(Empty with { MemberNo = "A1", Sequence = "Age" })!.Value.Field);
        Assert.Equal("bordxTo", LeafletRules.Validate(Empty with { BordxFrom = new(2006, 2, 1), BordxTo = new(2006, 1, 1) })!.Value.Field);
        Assert.Equal("memberTo", LeafletRules.Validate(Empty with { MemberFrom = "B", MemberTo = "A" })!.Value.Field);
    }

    [Fact]
    public void Print_options_pick_new_printed_or_all_rows_with_the_legacy_messages()
    {
        IReadOnlyList<LeafletRow> mixed = [Row("A", true), Row("B", false)], printed = [Row("A", true)], fresh = [Row("B", false)];
        Assert.Equal((false, (string?)null), LeafletRules.Filter(LeafletLists.PrintNew, mixed));
        Assert.Equal((true, (string?)null), LeafletRules.Filter(LeafletLists.Reprint, mixed));
        Assert.Equal((null, (string?)null), LeafletRules.Filter(LeafletLists.PrintAll, mixed));
        Assert.Equal("This report already printed, please select 'Reprint'", LeafletRules.Filter(LeafletLists.PrintNew, printed).Problem);
        Assert.Equal("Please select 'Print' option for this report", LeafletRules.Filter(LeafletLists.ReExport, fresh).Problem);
        Assert.NotNull(LeafletRules.Filter("Print Twice", mixed).Problem);
        Assert.NotNull(LeafletRules.ValidateVersion("Gold"));
        Assert.Null(LeafletRules.ValidateVersion(LeafletLists.ByDeptEmployee));
    }

    [Fact]
    public void Policy_number_carries_the_agent_code_and_export_file_name_is_payor_plus_yymmdd()
    {
        Assert.Equal("P1 (AG7)", LeafletReports.PolicyWithAgent("P1 ", " AG7"));
        Assert.Equal("P1", LeafletReports.PolicyWithAgent("P1", ""));
        Assert.Equal("XW060419", LeafletReports.FileName("XW", new DateOnly(2006, 4, 19)));
    }

    [Fact]
    public void Leaflets_render_one_page_per_member_and_card_export_lays_out_the_sheet()
    {
        LeafletDocument doc(string n, string ins) => new(n, "P1", "AG7", "ALI", "1 JALAN", null, null, "50000", "PAYOR BHD", ins, "GRP", "DEPT", "E1", [new("01", "SPOUSE", "S")]);
        var pdf = LeafletReports.Leaflets([doc("A*01", "G"), doc("B*01", "F")], LeafletLists.ByDeptEmployee, new DateOnly(2026, 11, 1));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page[^s]").Count);

        var xlsx = LeafletReports.CardExport(
            [new("A*01", "00", "ALI", "IC1", "P1", "FH11", new DateOnly(2027, 1, 31), "GRP", "DEPT", "E1", new DateOnly(2006, 4, 19), 6, "XW")],
            "PAYOR BHD", "XW", new DateOnly(2006, 4, 19), LeafletLists.ByDeptEmployee);
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);
        Assert.Equal(("PAYOR BHD", "19/04/2006", "XW060419"), (ws.Cell(1, 1).GetString(), ws.Cell(2, 1).GetString(), ws.Cell(3, 1).GetString()));
        Assert.Equal(("Name", "Employee No"), (ws.Cell(4, 2).GetString(), ws.Cell(4, 10).GetString()));
        Assert.Equal((1, "ALI", "A*01-00", "FH11"), (ws.Cell(5, 1).GetValue<int>(), ws.Cell(5, 2).GetString(), ws.Cell(5, 5).GetString(), ws.Cell(5, 6).GetString()));
        Assert.Equal(new DateTime(2027, 1, 31), ws.Cell(5, 7).GetDateTime());
    }
}

public class LeafletTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/leaflet";
    private const string Member = "XWGA00001*01";
    private static readonly LeafletCriteria Range = new(LeafletLists.MemberRange, null, "XWGA00001*01", "XWGA00001*03", null, null, null, null, null, "Membership No");
    private static readonly LeafletCriteria Batch = new(LeafletLists.BordxBatch, null, null, null, new DateOnly(2006, 4, 19), 6, "XW", null, null, "Name");
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(LeafletEndpoints.Screen));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Exec(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        await conn.ExecuteAsync(sql, args);
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    /// <summary>MBMPrinting is not in the restored databases; the tests create it (rolled back afterwards) with the columns UpdatePrintingFile writes.</summary>
    private Task CreatePrintingTable() => Exec("""
        IF OBJECT_ID('dbo.MBMPrinting', 'U') IS NULL
        CREATE TABLE dbo.MBMPrinting (MBMBordxDate datetime, MBMBatchNo int, PAYCode char(2), HLTCode nvarchar(4), MBMNumber nvarchar(30),
            MBMLeafletPrinted bit, MBMLeafletPrintedDate datetime, MBMMCOListPrinted bit, MBMMCOListDate datetime, MBMInvoiced bit, MBMInvoiceDate datetime,
            MBMInvoiceNo nvarchar(20), MBMExpListed bit, MBMExpListDate datetime)
        """);

    private static string Query(LeafletCriteria c) => string.Join("&", new (string, object?)[]
    {
        ("selection", c.Selection), ("memberNo", c.MemberNo), ("memberFrom", c.MemberFrom), ("memberTo", c.MemberTo), ("bordxDate", c.BordxDate?.ToString("yyyy-MM-dd")),
        ("batchNo", c.BatchNo), ("payor", c.Payor), ("bordxFrom", c.BordxFrom?.ToString("yyyy-MM-dd")), ("bordxTo", c.BordxTo?.ToString("yyyy-MM-dd")), ("sequence", c.Sequence),
    }.Where(p => p.Item2 is not null).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!.ToString()!)}"));

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/print", new LeafletPrintRequest(Range, LeafletLists.PrintAll, LeafletLists.BySihat))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(Range, LeafletLists.ExportAll, LeafletLists.BySihat))).StatusCode);
    }

    [DatabaseFact]
    public async Task Options_list_the_legacy_combos_and_the_payors()
    {
        var o = await Client().GetFromJsonAsync<LeafletOptions>($"{Url}/options");
        Assert.Equal(LeafletLists.Selections, o!.Selections);
        Assert.Contains(o.Payors, p => p.Code == "XW");
    }

    [DatabaseFact]
    public async Task Searches_a_member_range_and_a_bordereaux_batch_in_the_chosen_sequence()
    {
        var c = Client();
        var range = await c.GetFromJsonAsync<LeafletPage>($"{Url}?{Query(Range)}");
        Assert.Equal(["XWGA00001*01", "XWGA00001*02", "XWGA00001*03"], range!.Rows.Select(r => r.MemberNumber));
        Assert.All(range.Rows, r => Assert.False(r.Printed));
        var first = range.Rows[0];
        Assert.Equal(("XW", new DateOnly(2006, 4, 19), 6, "MOHAMAD SUSANTO"), (first.PayCode, first.BordxDate, first.BatchNo, first.Name));

        var batch = await c.GetFromJsonAsync<LeafletPage>($"{Url}?{Query(Batch)}");
        Assert.Equal(39, batch!.Rows.Count);
        Assert.Equal(batch.Rows.Select(r => r.Name).Order(StringComparer.Ordinal), batch.Rows.Select(r => r.Name), StringComparer.Ordinal);
        Assert.All(batch.Rows, r => Assert.Equal((6, "XW"), (r.BatchNo, r.PayCode)));

        // Fields of other selections are ignored: a single-member search does not apply the range.
        var single = await c.GetFromJsonAsync<LeafletPage>($"{Url}?selection={Uri.EscapeDataString(LeafletLists.SingleMember)}&memberNo={Uri.EscapeDataString(Member)}&memberFrom=ZZ");
        Assert.Single(single!.Rows);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{Url}?selection={Uri.EscapeDataString(LeafletLists.BordxBatch)}&batchNo=6")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{Url}?memberNo=a'b")).StatusCode);
    }

    [DatabaseFact]
    public async Task Printing_without_the_MBMPrinting_table_returns_the_pdf_and_records_nothing()
    {
        if (await Scalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.MBMPrinting', 'U') IS NULL THEN 0 ELSE 1 END") == 1) return;
        var res = await Client().PostAsJsonAsync($"{Url}/print", new LeafletPrintRequest(Range, LeafletLists.PrintNew, LeafletLists.BySihat));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("false", Assert.Single(res.Headers.GetValues(LeafletEndpoints.RecordedHeader)));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(await res.Content.ReadAsByteArrayAsync(), 0, 4));
        Assert.Equal(false, Assert.Single(factory.Audit.Entries).Details["recorded"]);
    }

    [DatabaseFact]
    public async Task Printing_new_leaflets_marks_them_printed_so_print_new_then_refuses_and_reprint_works()
    {
        await CreatePrintingTable();
        var c = Client();
        var res = await c.PostAsJsonAsync($"{Url}/print", new LeafletPrintRequest(Range, LeafletLists.PrintNew, LeafletLists.ByGroupCompany));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("true", Assert.Single(res.Headers.GetValues(LeafletEndpoints.RecordedHeader)));
        Assert.Equal(3, await Scalar<int>("SELECT COUNT(*) FROM MBMPrinting WHERE MBMLeafletPrinted = 1 AND MBMLeafletPrintedDate = CAST(GETDATE() AS date) AND PAYCode = 'XW' AND MBMNumber LIKE 'XWGA00001*%'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMPrinting WHERE MBMNumber = @m AND MBMBatchNo = 6 AND MBMBordxDate = '2006-04-19' AND HLTCode = 'N'", new { m = Member }));

        var page = await c.GetFromJsonAsync<LeafletPage>($"{Url}?{Query(Range)}");
        Assert.True(page!.PrintingTracked);
        Assert.All(page.Rows, r => Assert.True(r.Printed));

        var again = await c.PostAsJsonAsync($"{Url}/print", new LeafletPrintRequest(Range, LeafletLists.PrintNew, LeafletLists.BySihat));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("Reprint", await again.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"{Url}/print", new LeafletPrintRequest(Range, LeafletLists.Reprint, LeafletLists.BySihat))).StatusCode);
        Assert.Equal(3, await Scalar<int>("SELECT COUNT(*) FROM MBMPrinting"));

        // The card export flag is separate from the leaflet flag.
        var cards = await c.GetFromJsonAsync<LeafletPage>($"{Url}/cards?{Query(Range)}");
        Assert.All(cards!.Rows, r => Assert.False(r.Printed));
    }

    [DatabaseFact]
    public async Task Card_export_writes_the_sheet_for_the_members_and_marks_them_exported()
    {
        await CreatePrintingTable();
        var c = Client();
        var criteria = Batch with { Selection = null, MemberFrom = "XWGA00001*01", MemberTo = "XWGA00001*09", Sequence = null };
        var preview = await c.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(criteria, LeafletLists.ExportNew, LeafletLists.BySihat, Preview: true));
        Assert.Equal("false", Assert.Single(preview.Headers.GetValues(LeafletEndpoints.RecordedHeader)));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMPrinting"));
        var res = await c.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(criteria, LeafletLists.ExportNew, LeafletLists.BySihat));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("XW060419", ws.Cell(3, 1).GetString());
        Assert.Equal(("MOHAMAD SUSANTO", "XWGA00001*01-00", "FH11"), (ws.Cell(5, 2).GetString(), ws.Cell(5, 5).GetString(), ws.Cell(5, 6).GetString()));
        Assert.True(ws.Cell(6, 1).IsEmpty());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMPrinting WHERE MBMNumber = @m AND MBMExpListed = 1 AND MBMLeafletPrinted IS NULL", new { m = Member }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(criteria, LeafletLists.ExportNew, LeafletLists.BySihat))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(criteria, LeafletLists.PrintNew, LeafletLists.BySihat))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"{Url}/cards/export", new LeafletPrintRequest(criteria with { MemberFrom = "ZZ1", MemberTo = "ZZ2" }, LeafletLists.ExportAll, LeafletLists.BySihat))).StatusCode);
    }
}
