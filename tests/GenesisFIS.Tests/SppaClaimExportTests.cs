using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

/// <summary>The SPPA claim files line by line, worked out by hand from the legacy frmCLMASCIISPPA2012 code.</summary>
public class SppaClaimExportRuleTests
{
    private static readonly DateTime Bordx = new(2021, 8, 5), Pv = new(2021, 8, 6);

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> Claim(string no, int version, string inv, decimal invoice, decimal actual, decimal approved, string? medical = null, decimal member = 0) =>
        Row(("CASNumber", no), ("ClaimVersion", version), ("HOSCode", "H01"), ("CASESPRefNo", "ESP1"), ("InvNo", inv), ("INVAmount", invoice), ("CLMTotalActual", actual),
            ("CLMBordxAmt", approved), ("CLMMedical", medical), ("CLMPayableByMedix2M", member), ("CLMBordxDate", Bordx), ("ClaimLastUpdateUser", "AMALINA"));

    private static string File(SppaExport e, string kind) => e.Files.Single(f => f.Name == SppaClaimExport.FileName(kind, e.Batch)).Content;

    [Fact]
    public void Numbers_are_written_as_VB_wrote_them()
    {
        Assert.Equal(("1335", "12.5", "-3", "0"), (SppaClaimExport.VbNumber(1335.0000m), SppaClaimExport.VbNumber(12.50m), SppaClaimExport.VbNumber(-3m), SppaClaimExport.VbNumber(0m)));
        Assert.Equal(("", "AB ", "7"), (SppaClaimExport.Vb(null), SppaClaimExport.Vb("AB "), SppaClaimExport.Vb(7.00m)));
        Assert.Null(SppaClaimExport.Build(new SppaSource(), "000001"));
    }

    [Fact]
    public void Claim_lines_follow_the_one_and_several_invoice_rules()
    {
        var single = Claim("LP1", 1, "INV1", 1000, 1100, 1000, member: 0);
        var multiFee = Claim("LP2", 2, "INV2A", 120, 1500, 1400, medical: "120");
        var multiBill = Claim("LP2", 2, "INV2B", 1400, 1500, 1400, medical: "120");
        var e = SppaClaimExport.Build(new SppaSource { Claims = [single, multiFee, multiBill], ClaimRows = (no, _) => no == "LP2" ? 2 : 1 }, "005339")!;

        Assert.Equal(
            "FWM|H01|ESP1|INV1|1100|1000|0|20210805:00:00|AMALINA||100\r\n" +
            "FWM|H01|ESP1|INV2A|120|0|20210805:00:00|AMALINA||0.00\r\n" +
            "FWM|H01|ESP1|INV2B|1280|1280|1280|0|20210805:00:00|AMALINA||0.00\r\n", File(e, "AH"));
        // Bills: every line; amount: the single claim's approved + the medical report invoice (legacy did not add the third line).
        Assert.Equal((3, 1120m), (e.Totals.Bills, e.Totals.ClaimAmount));
        Assert.Equal("3|1120|0.00|0|0|0|0|0|0|0\r\n", File(e, "AB"));
        Assert.Equal(["TEFWMAH005339.txt", "TEFWMAD005339.txt", "TEFWMAB005339.txt"], e.Files.Select(f => f.Name));
    }

    [Fact]
    public void Benefit_lines_follow_the_legacy_codes_and_formats()
    {
        var details = Row(("CASNumber", "LP1"), ("ClaimVersion", 1), ("HOSCode", "H01"), ("CASESPRefNo", null), ("InvNo", "INV1"), ("INVAmount", 1000m),
            ("CLMMedicalAct", 80m), ("CLMMedical", 80m), ("CLMBoardAmtAct", 300m), ("CLMBoardAmt", 250m), ("CLMServicesAct", 99.5m), ("CLMServices", null), ("CLMAdminAct", 0m));
        var report = Row(("CASNumber", "LP2"), ("ClaimVersion", 1), ("HOSCode", "H01"), ("CASESPRefNo", "E2"), ("InvNo", "INV2"), ("INVAmount", 92m), ("CLMMedicalAct", 92m), ("CLMMedical", 92m),
            ("CLMBoardAmtAct", 500m));
        var e = SppaClaimExport.Build(new SppaSource { Claims = [Claim("LP1", 1, "INV1", 1000, 1000, 1000)], Details = [details, report], DetailRows = (no, _) => no == "LP2" ? 2 : 1 }, "1")!;

        Assert.Equal(
            "FWM|H01||INV1|1|H010|Medical Report|1|1|LUMPSUM|80|80|0.00||0.00\r\n" +
            "FWM|H01||INV1|2|H001|Room & Board|1|1|LUMPSUM|300|250|0.00||0.00\r\n" +
            "FWM|H01||INV1|3|H003|Hospital Supplies and Services|1|1|LUMPSUM|99.50|0.00|0.00||0.00\r\n" +
            // A medical report invoice of a claim with several invoices gives only its H010 line.
            "FWM|H01|E2|INV2|1|H010|Medical Report|1|1|LUMPSUM|92|92|0.00||0.00\r\n", File(e, "AD"));
        Assert.Equal(4, e.Totals.BenefitLines);
    }

    [Fact]
    public void Admission_note_and_request_lines()
    {
        var e = SppaClaimExport.Build(new SppaSource
        {
            Claims = [Claim("LP1", 1, "INV1", 1000, 1000, 1000)],
            Admissions = [Row(("HOSCode", "H01"), ("CASESPRefNo", "E1"), ("ICDCode1", "A01"), ("ICDCode2", "B02"), ("ICDCode3", "C03"), ("ModalityCode", "S "), ("CLMBoardDay", 3m), ("CLMICUDay", 0m)),
                          Row(("HOSCode", "H01"), ("CASESPRefNo", "E2"), ("ICDCode1", "A01"), ("ICDCode2", null), ("ICDCode3", "C03"), ("ModalityCode", "M"), ("CLMBoardDay", 1m), ("CLMICUDay", 1m))],
            Notes = [Row(("HOSCode", "H01"), ("CASESPRefNo", "E1"), ("BordxRefNo", "NLP02520 "), ("CASNumber", "LP1"), ("ClaimVersion", 1), ("CLMBordxDate", Bordx), ("InvNo", "INV1"),
                          ("CLMBordxAmt", 1000m), ("CLMMedical", 80m))],
            Requests = [Row(("HOSCode", "H01"), ("CASESPRefNo", "E1"), ("FWReqNumber", "R1"), ("FWReqReason", "Missing bill"))],
        }, "2")!;
        // Legacy: with a second ICD code the third is left out.
        Assert.Equal("FWM|H01|E1|A01,B02|S|3|0\r\nFWM|H01|E2|A01,,C03|M|1|1\r\n", File(e, "AA"));
        Assert.Equal("FWM|H01|E1|NLP02520|LP11|20210805|INV1|920|80\r\n", File(e, "AN"));
        Assert.Equal("FWM|H01|E1|R1|Missing bill\r\n", File(e, "RD"));
        Assert.Equal("1|1000|0.00|0|0|0|2|1|0|1\r\n", File(e, "AB"));
    }

    [Fact]
    public void Payment_lines_split_hospital_charges_and_medical_report_fees()
    {
        Dictionary<string, object?> Pay(string no, string inv, decimal invoice, decimal paid, decimal medical, decimal bordx, string esp) =>
            Row(("PVHOSNo", "CF1"), ("PVHOSDate", Pv), ("HOSCode", "H01"), ("CASNumber", no), ("InvNo", inv), ("INVAmount", invoice), ("HOSPaidAmt", paid), ("CLMMedical", medical),
                ("CLMBordxAmt", bordx), ("CASESPRefNo", esp), ("PVHOSChequeNo", "CHQ1"), ("PVHOSChequeNoMR", "MR1"));
        var single = Pay("LP1", "INV1", 1000, 1000, 80, 1000, "E1");
        var feeInvoice = Pay("LP2", "INV2A", 120, 120, 120, 1120, "E2");
        var billInvoice = Pay("LP2", "INV2B", 1000, 1120, 120, 1120, "E2");
        var e = SppaClaimExport.Build(new SppaSource
        {
            Claims = [Claim("LP1", 1, "INV1", 1000, 1000, 1000)],
            PaymentGroups = [Row(("PVHOSNo", "CF1"))],
            VoucherPayments = _ => [single, feeInvoice, billInvoice],
            Payments = [single, feeInvoice, billInvoice],
            CasePaymentRows = no => no == "LP2" ? 2 : 1,
            CaseInvoiceAmount = (_, inv) => inv == "INV2A" ? 120m : 1000m,
            PaymentSlips = [Row(("HOSCode", "H01"), ("PAYCourierNo", null), ("PVHOSDate", Pv), ("PVHOSNo", "CF1 "))],
        }, "3")!;
        // PH: hospital charges (paid less medical report) on the claim cheque, medical report fees on the MR cheque.
        Assert.Equal("FWM|H01|CF1|C|CHQ1|20210806|1920|M|20210806\r\nFWM|H01|CF1|C|MR1|20210806|200|R|20210806\r\n", File(e, "PH"));
        Assert.Equal(
            "FWM|H01|E1|CF1|INV1|1000|920|CHQ1\r\n" +       // single invoice: the payment less the medical report...
            "FWM|H01|E1|CF1|INV1|1000|80|MR1\r\n" +         // ...and the medical report on its own line
            "FWM|H01|E2|CF1|INV2A|120|120|MR1\r\n" +        // several invoices: the medical report invoice
            "FWM|H01|E2|CF1|INV2B|1000|1000|CHQ1\r\n", File(e, "PD"));
        Assert.Equal("FWM|H01|N/A|20210806|CF1\r\n", File(e, "PS"));
        Assert.Equal((2, 4, 1), (e.Totals.PaymentHeaders, e.Totals.PaymentDetails, e.Totals.PaymentSlips));
    }

    [Fact]
    public void The_zip_holds_every_file_in_latin1()
    {
        var e = SppaClaimExport.Build(new SppaSource { Claims = [Claim("LP1", 1, "INV1", 1000, 1000, 1000)] }, "7")!;
        using var zip = new ZipArchive(new MemoryStream(SppaZip.Create(e)));
        Assert.Equal(e.Files.Select(f => f.Name), zip.Entries.Select(x => x.Name));
    }
}

public class SppaClaimExportTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/case-ascii/sppa";
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    [DatabaseFact]
    public async Task Exports_a_real_FWHS_week_and_takes_the_next_batch_number()
    {
        string before;
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet)) before = (await ws.ExecuteScalarAsync<string>("SELECT TOP 1 SEQBatchNo FROM CLMASCIIMedGen"))!.Trim();
        var c = factory.ClientFor(WriteApiFactory.Access(18));
        var res = await c.PostAsJsonAsync(Url, new SppaExportRequest(new DateOnly(2021, 8, 1), new DateOnly(2021, 8, 6)));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var batch = res.Headers.GetValues("X-Batch").Single();
        Assert.Equal((int.Parse(before) + 1).ToString("000000"), batch);
        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        var names = zip.Entries.Select(x => x.Name).ToList();
        Assert.Contains($"TEFWMAH{batch}.txt", names);
        Assert.Contains($"TEFWMAB{batch}.txt", names);
        var ah = new StreamReader(zip.GetEntry($"TEFWMAH{batch}.txt")!.Open()).ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(ah, l => Assert.StartsWith("FWM|", l));
        Assert.Equal(ah.Length.ToString(), res.Headers.GetValues("X-Records").Single());
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet)) Assert.Equal(batch, (await ws.ExecuteScalarAsync<string>("SELECT TOP 1 SEQBatchNo FROM CLMASCIIMedGen"))!.Trim());
        Assert.Contains(factory.Audit.Entries, e => e.Action == "Claim.SppaExport" && e.Key == batch);
    }

    [DatabaseFact]
    public async Task An_empty_range_takes_no_batch_and_dates_are_required()
    {
        var c = factory.ClientFor(WriteApiFactory.Access(18));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Url, new SppaExportRequest(new DateOnly(1990, 1, 1), new DateOnly(1990, 1, 2)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Url, new SppaExportRequest(null, new DateOnly(2021, 1, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(17)).PostAsJsonAsync(Url, new SppaExportRequest(new DateOnly(2021, 8, 1), new DateOnly(2021, 8, 6)))).StatusCode);
    }
}
