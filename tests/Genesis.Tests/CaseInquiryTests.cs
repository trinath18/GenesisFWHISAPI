using System.Net;
using System.Net.Http.Json;
using Genesis.Core.Cases;
using Microsoft.Extensions.Configuration;

namespace Genesis.Tests;

public class CaseNumberTests
{
    [Theory]
    [InlineData("WM00435", true)]
    [InlineData(".AX00022", false)]
    [InlineData("AB-12", true)]
    [InlineData("", false)]
    [InlineData("a'--", false)]
    [InlineData("a/b", false)]
    [InlineData("a%", false)]
    public void IsValid_allows_only_case_number_characters(string value, bool expected) =>
        Assert.Equal(expected, CaseNumber.IsValid(value));

    [Theory]
    [InlineData(CaseSearchBy.MemberNo, "AB", false)]
    [InlineData(CaseSearchBy.MemberNo, "ABC", true)]
    [InlineData(CaseSearchBy.CaseNo, "WM", false)]
    [InlineData(CaseSearchBy.Name, "AS", false)]
    [InlineData(CaseSearchBy.Name, "ASLINA TANJUNG", true)]
    [InlineData(CaseSearchBy.Name, "ASL%", false)]
    [InlineData(CaseSearchBy.Name, "O'BRIEN", true)]
    [InlineData(CaseSearchBy.Ic, "B2158139", true)]
    [InlineData(CaseSearchBy.Ic, "x'; drop", false)]
    [InlineData(CaseSearchBy.PolicyNo, "S5012090", true)]
    [InlineData(CaseSearchBy.ClaimId, "", false)]
    public void IsValidSearch_depends_on_the_search_type(CaseSearchBy by, string value, bool expected) =>
        Assert.Equal(expected, CaseNumber.IsValidSearch(by, value));
}

public class CaseInquiryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Case = "WM00435";

    /// <summary>Access string with the given 1-based positions switched on (the real strings are 178+ characters long).</summary>
    private static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    private HttpClient Client(params int[] positions) => factory.ClientFor(Access(positions));

    [Fact]
    public async Task Requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/cases/{Case}")).StatusCode);

    [Fact]
    public async Task Requires_case_inquiry_permission_13() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync($"/api/cases/{Case}")).StatusCode);

    [Fact]
    public async Task Payment_print_needs_permission_178_on_top_of_13()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(13).GetAsync($"/api/cases/{Case}/print/payments.xlsx")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Client(13, 178).GetAsync($"/api/cases/{Case}/print/payments.xlsx")).StatusCode);
    }

    [Theory]
    [InlineData("/api/cases?by=Nope&q=abc")]
    [InlineData("/api/cases?by=CaseNo&q=ab")]
    [InlineData("/api/cases?by=Name&q=a%25b")]
    [InlineData("/api/cases?by=Ic&q=x'--")]
    [InlineData("/api/cases?by=CaseNo&q=WM0&plan=a'b")]
    [InlineData("/api/cases/a'--")]
    [InlineData("/api/cases/a'--/worksheets")]
    public async Task Rejects_invalid_input(string url) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(13).GetAsync(url)).StatusCode);

    [DatabaseFact]
    public async Task Search_works_for_each_criterion()
    {
        var c = Client(13);
        foreach (var url in new[]
        {
            "/api/cases?by=CaseNo&q=WM0043", "/api/cases?by=MemberNo&q=WMGA66398", "/api/cases?by=PolicyNo&q=S5012090",
            "/api/cases?by=Name&q=ASLINA", "/api/cases?by=Ic&q=B2158139", "/api/cases?by=EspRefNo&q=A000180307",
            "/api/cases?by=ClaimId&q=42063001IPUC21001230",
        })
        {
            var page = await c.GetFromJsonAsync<CaseSearchPage>(url);
            Assert.Contains(page!.Items, i => i.CaseNumber == Case);
        }
        var none = await c.GetFromJsonAsync<CaseSearchPage>("/api/cases?by=CaseNo&q=ZZZ9999");
        Assert.Empty(none!.Items);
    }

    [DatabaseFact]
    public async Task Search_caps_results_and_flags_truncation()
    {
        var page = await Client(13).GetFromJsonAsync<CaseSearchPage>("/api/cases?by=MemberNo&q=WMG");
        Assert.True(page!.Truncated);
        Assert.Equal(CaseNumber.SearchLimit, page.Items.Count);
    }

    [DatabaseFact]
    public async Task Detail_returns_case_member_and_diagnoses()
    {
        var c = Client(13);
        var d = await c.GetFromJsonAsync<CaseDetail>($"/api/cases/{Case}");
        Assert.Equal("ASLINA TANJUNG", d!.Header.PatientName);
        Assert.Equal("HOSPITAL SHAH ALAM", d.Header.HospitalName);
        Assert.Equal("S5012090", d.Member.PolicyNo);
        Assert.Equal("FH01", d.Member.PlanCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/cases/ZZ99999")).StatusCode);

        var withDiag = await c.GetFromJsonAsync<CaseDetail>("/api/cases/JE19473");
        Assert.Equal("OBSTRUCTIVE UROPATHY SECONDARY TO RIGHT VUJ STONE", withDiag!.Header.AdmissionReason);
        Assert.NotEmpty(withDiag.FinalDiagnoses);
    }

    [DatabaseFact]
    public async Task Worksheets_and_invoices_come_from_the_worksheet_database()
    {
        var c = Client(13);
        var ws = await c.GetFromJsonAsync<List<CaseWorksheet>>($"/api/cases/{Case}/worksheets");
        Assert.Contains(ws!, w => w.Version == 1 && w.TotalApproved == 380m && w.BordxRefNo == "NWM00299");
        var inv = await c.GetFromJsonAsync<List<CaseInvoice>>($"/api/cases/{Case}/invoices");
        Assert.Contains(inv!, i => i.InvoiceNo == "SABLIP/21018123" && i.Amount == 260m);
    }

    [DatabaseFact]
    public async Task Payments_and_receipts_come_from_the_payment_database()
    {
        var p = await Client(13).GetFromJsonAsync<CasePayments>("/api/cases/JE19473/payments");
        Assert.Contains(p!.ToHospital, x => x.Reference == "CF100000" && x.Amount == 2232m && x.ChequeNo == "097713");
        Assert.Contains(p.ReceiptsFromPayor, x => x.Reference == "RF100000" && x.Amount == 2232m);
    }

    [DatabaseFact]
    public async Task Guarantees_reminders_and_cartons_map_correctly()
    {
        var c = Client(13);
        var g = await c.GetFromJsonAsync<CaseGuarantees>("/api/cases/MA08525/guarantees");
        Assert.Equal(2, g!.LetterOfGuarantees.Count(x => x.Amount > 0));
        Assert.Contains(g.FollowUps, f => f.Number == "FMA08525-1" && f.Amount == 3342.34m);
        var r = await c.GetFromJsonAsync<List<CaseReminder>>("/api/cases/AG00533/reminders");
        Assert.Contains(r!, x => x.OriginalInvoice == "Y" && x.RegisteredBy == "AHMAD");
        var cartons = await c.GetFromJsonAsync<List<CaseCarton>>($"/api/cases/{Case}/cartons");
        Assert.Contains(cartons!, x => x.Kind == "Claim" && x.CartonNo == "3001133158");
    }

    [DatabaseFact]
    public async Task Benefits_use_the_cases_plan_and_the_latest_block_before_admission()
    {
        var b = await Client(13).GetFromJsonAsync<CaseBenefits>($"/api/cases/{Case}/benefits");
        Assert.Equal("FH01", b!.PlanCode);
        Assert.Contains(b.Items, i => i.Description == "Medical Report" && i.ClaimableAmount == "120.00");
        Assert.Single(b.Items, i => i.Code == "801");
    }

    [DatabaseFact]
    public async Task Every_tab_endpoint_responds_ok()
    {
        var c = Client(13);
        foreach (var tab in new[]
        {
            "guarantees", "worksheets", "invoices", "payments", "reminders", "cartons", "surgical-schedule", "account-summary", "repudiation",
            "benefits", "exclusions", "non-disclosure", "policy-terms", "lifetime-limit", "policy-wording",
            "documents/Scanned", "documents/Internal", "documents/Emails",
        })
        {
            var res = await c.GetAsync($"/api/cases/{Case}/{tab}");
            Assert.True(res.IsSuccessStatusCode, $"{tab} returned {(int)res.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/cases/{Case}/documents/Nope")).StatusCode);
    }

    [DatabaseFact]
    public async Task Print_outputs_are_real_documents()
    {
        var c = Client(13, 178);
        foreach (var (path, type) in new[]
        {
            ("enquiry.xlsx", "spreadsheetml"), ("payments.xlsx", "spreadsheetml"), ("notes.pdf", "application/pdf"), ("notes.pdf?kind=internal", "application/pdf"),
        })
        {
            var res = await c.GetAsync($"/api/cases/{Case}/print/{path}");
            Assert.True(res.IsSuccessStatusCode, path);
            Assert.Contains(type, res.Content.Headers.ContentType!.MediaType);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(type == "application/pdf" ? "%PDF" : "PK", System.Text.Encoding.ASCII.GetString(bytes[..(type == "application/pdf" ? 4 : 2)]));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/cases/ZZ99999/print/notes.pdf")).StatusCode);
    }
}

public class CaseDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
    private readonly ICaseDocumentStore _store;

    public CaseDocumentStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scanned", "WM00435"));
        File.WriteAllText(Path.Combine(_root, "scanned", "WM00435", "report.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_root, "scanned", "WM00435", "run.exe"), "bad");
        File.WriteAllText(Path.Combine(_root, "scanned", "secret.pdf"), "secret");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStore:ScannedDocsRoot"] = Path.Combine(_root, "scanned") }).Build();
        _store = new Genesis.Infrastructure.Cases.FileSystemCaseDocumentStore(cfg);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Lists_documents_only_for_configured_areas()
    {
        Assert.True(_store.IsConfigured(CaseDocumentArea.Scanned));
        Assert.False(_store.IsConfigured(CaseDocumentArea.Emails));
        Assert.Equal(["report.pdf"], _store.List(CaseDocumentArea.Scanned, "WM00435").Select(f => f.Name));
        Assert.Empty(_store.List(CaseDocumentArea.Emails, "WM00435"));
    }

    [Fact]
    public void Opens_documents_with_the_right_content_type()
    {
        var c = _store.Open(CaseDocumentArea.Scanned, "WM00435", "report.pdf");
        Assert.NotNull(c);
        using (c.Content) Assert.Equal("application/pdf", c.ContentType);
    }

    [Theory]
    [InlineData("WM00435", "run.exe")]
    [InlineData("WM00435", "..\\secret.pdf")]
    [InlineData("WM00435", "../secret.pdf")]
    [InlineData("..", "secret.pdf")]
    [InlineData("WM00435\\..", "secret.pdf")]
    [InlineData("", "report.pdf")]
    [InlineData("UNKNOWN", "report.pdf")]
    public void Rejects_traversal_and_unsafe_types(string caseNumber, string file) =>
        Assert.Null(_store.Open(CaseDocumentArea.Scanned, caseNumber, file));
}

