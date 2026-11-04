using System.Net;
using System.Net.Http.Json;
using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Tracking;
using Microsoft.Extensions.DependencyInjection;

namespace GenesisFIS.Tests;

public class TrackingPrintRuleTests
{
    [Fact]
    public void A_label_holds_the_name_and_the_address_without_NA_fillers()
    {
        Assert.Equal(["AKRAM HOSSAIN", "120, JALAN KANTHAN BARU 1", "31400 IPOH", "PERAK"],
            MemberLabels.Lines(" AKRAM HOSSAIN ", "120, JALAN KANTHAN BARU 1", "NA", " ", "31400", "IPOH", "PERAK"));
        Assert.Null(MemberLabels.Lines("ROSAN SAH", "NA", "NA", "NA", "NA", "NA", null));
    }

    [Fact]
    public void Print_bordx_pays_government_hospitals_to_the_director_and_splits_the_medical_fee()
    {
        Assert.Equal("PENGARAH HOSPITAL SELAYANG", BordxPrintRules.PayTo("C", "HOSPITAL SELAYANG ", "X"));
        Assert.Equal("HOSPITAL", BordxPrintRules.PayTo("P", "HOSPITAL SELAYANG", " HOSPITAL "));
        Assert.Equal((940m, 60m, 1000m), BordxPrintRules.Amounts(1000, 60));
    }

    [Fact]
    public void The_label_and_bordx_screens_offer_their_printouts()
    {
        var labels = TrackingCatalog.Find("member-labels")!;
        Assert.Equal((6, "member-labels"), (labels.ScreenPosition, Assert.Single(labels.Prints!).Key));
        Assert.Contains("INNER JOIN MBM m ON m.MBMNumber = s.MBMNumber", labels.From);
        Assert.Equal("bordx-details", Assert.Single(TrackingCatalog.Find("print-bordx")!.Prints!).Key);
    }
}

public class TrackingPrintTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    private static async Task AssertPdfAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));
    }

    [DatabaseFact]
    public async Task Print_label_lists_the_member_addresses_and_prints_a_label_sheet()
    {
        var client = factory.ClientFor(Access(6));
        var def = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/tracking/member-labels");
        Assert.Equal("member-labels", def.GetProperty("prints")[0].GetProperty("key").GetString());
        var rows = await client.GetFromJsonAsync<TrackingResult>("/api/tracking/member-labels/search?f.memberNo.from=LPGB4757533&f.memberNo.to=LPGB4757533*99");
        Assert.Contains(rows!.Rows, r => r[0]?.ToString() == "LPGB4757533*04" && r[3]?.ToString() == "120, JALAN KANTHAN BARU 1," && r[4]?.ToString() == "TAMAN KANTHAN JAYA");
        await AssertPdfAsync(await client.GetAsync("/api/tracking/member-labels/print/member-labels.pdf?f.memberNo.from=LPGB4757533&f.memberNo.to=LPGB4757533*99"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tracking/member-labels/print/bordx-details.pdf?f.memberNo.from=A&f.memberNo.to=B")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Access(3)).GetAsync("/api/tracking/member-labels/print/member-labels.pdf?f.memberNo.from=A")).StatusCode);
    }

    [DatabaseFact]
    public async Task Print_bordx_prints_the_bordereaux_found()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var b = Assert.Single(await scope.ServiceProvider.GetRequiredService<IBordxPrintRepository>().LoadAsync(["NLP02520"]));
            Assert.Equal(12, b.Claims.Count);
            Assert.Equal(b.Claims.Count, b.Claims.Select(c => (c.CaseNumber, c.Version)).Distinct().Count());
            Assert.NotNull(b.Payor.Name);
            Assert.All(b.Claims, c => Assert.Equal(c.Total, c.HospitalCharges + c.MedicalReport));
        }
        var client = factory.ClientFor(Access(32));
        await AssertPdfAsync(await client.GetAsync("/api/tracking/print-bordx/print/bordx-details.pdf?f.bordxRef=NLP0252"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tracking/print-bordx/print/bordx-details.pdf?f.bordxRef=ZZNOBORDX")).StatusCode);
    }
}

public class MemberEnquiryPrintTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [DatabaseFact]
    public async Task The_enquiry_report_reads_the_extra_fields_of_the_legacy_report_view()
    {
        using var scope = factory.Services.CreateScope();
        var extras = await scope.ServiceProvider.GetRequiredService<GenesisFIS.Core.Membership.IMembershipTabsRepository>().GetPrintExtrasAsync("LPGB4757533*04");
        Assert.NotNull(extras);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<GenesisFIS.Core.Membership.IMembershipTabsRepository>().GetPrintExtrasAsync("ZZZZ0000000"));
    }
}
