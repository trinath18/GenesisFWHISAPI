using System.Globalization;
using System.Text;

namespace GenesisFIS.Core.Cases;

/// <summary>
/// Case > Claims ASCII (To SPPA) (legacy frmCLMASCIISPPA2012, USRAccess 18) and Claims ASCII Pymt (To SPPA) (frmCLMASCIISPPAPymt): the pipe-delimited
/// claim files sent to SPPA for a date range, one batch per run: TEFWM{kind}{batch}.txt for AH (claims), AD (benefit lines), AA (admissions), AN (notes),
/// RD (requests), PH (payment headers), PD (payment details), PS (payment slips) and AB (batch totals). The batch number is the next CLAIMASCIIMedGen
/// number (legacy GetCLMAsciiMedBatch, shared with the other claim exports). Lines reproduce the legacy form exactly, including its quirks (the AH line of a
/// claim with several invoices has a different number of fields), because SPPA reads these files.
/// </summary>
public sealed record SppaExportRequest(DateOnly? From, DateOnly? To, bool ByPaymentDate = false);

public sealed record SppaFile(string Name, string Content);

/// <summary>The AB totals: bills and their amount, benefit lines, then the line counts of PH, PD, AA, AN, PS and RD.</summary>
public sealed record SppaTotals(int Bills, decimal ClaimAmount, int BenefitLines, int PaymentHeaders, int PaymentDetails, int Admissions, int Notes, int PaymentSlips, int Requests);

public sealed record SppaExport(string Batch, IReadOnlyList<SppaFile> Files, SppaTotals Totals);

/// <summary>The view rows of one export (legacy procedure Case_ClaimAscii_FWHS cases 2-9) and the lookups the legacy form made per row.</summary>
public sealed class SppaSource
{
    public IReadOnlyList<IDictionary<string, object?>> Claims { get; init; } = [];          // case 2, View_Claims_ASCII_FWHSAH
    public IReadOnlyList<IDictionary<string, object?>> Details { get; init; } = [];         // case 3, View_Claims_ASCII_FWHSAD
    public IReadOnlyList<IDictionary<string, object?>> Admissions { get; init; } = [];      // case 6, View_Claims_ASCII_FWHSAA
    public IReadOnlyList<IDictionary<string, object?>> Notes { get; init; } = [];           // case 7, View_Claims_ASCII_FWHSAN
    public IReadOnlyList<IDictionary<string, object?>> Requests { get; init; } = [];        // case 9, View_Claims_ASCII_FWHSRD
    public IReadOnlyList<IDictionary<string, object?>> PaymentGroups { get; init; } = [];   // case 4, View_Claims_ASCII_FWHSPH_Group
    public IReadOnlyList<IDictionary<string, object?>> Payments { get; init; } = [];        // case 5, View_Claims_ASCII_FWHSPD
    public IReadOnlyList<IDictionary<string, object?>> PaymentSlips { get; init; } = [];    // case 8, View_Claims_ASCII_FWHSPS

    /// <summary>Rows of the claim (case, version) in the AH view, any date.</summary>
    public Func<string, int, int> ClaimRows { get; init; } = (_, _) => 1;
    /// <summary>Rows of the claim (case, version) in the AD view, any date.</summary>
    public Func<string, int, int> DetailRows { get; init; } = (_, _) => 1;
    /// <summary>The PD view rows of a voucher, ordered by voucher, case and invoice amount.</summary>
    public Func<string, IReadOnlyList<IDictionary<string, object?>>> VoucherPayments { get; init; } = _ => [];
    /// <summary>Rows of the case in the PD view, any voucher.</summary>
    public Func<string, int> CasePaymentRows { get; init; } = _ => 1;
    /// <summary>The invoice amount of the case's PD row with the invoice number (the last one), or null.</summary>
    public Func<string, string, decimal?> CaseInvoiceAmount { get; init; } = (_, _) => null;
}

public static class SppaClaimExport
{
    public const string Tpa = "FWM";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Medical-report invoice amounts: the AH file treats 92 and 120 as one, the other files 80, 92 and 120 (legacy form).</summary>
    private static readonly decimal[] ReportFeesAh = [92m, 120m];
    private static readonly decimal[] ReportFees = [80m, 92m, 120m];

    /// <summary>The benefit lines of the AD file: code, description, actual and approved columns, and whether the amounts were Format(x, "###0.00").</summary>
    private static readonly (string Code, string Name, string Actual, string Approved, bool Fixed)[] Benefits =
    [
        ("H001", "Room & Board", "CLMBoardAmtAct", "CLMBoardAmt", false),
        ("H002", "Intensive Care Unit", "CLMICUAmtAct", "CLMICUAmt", false),
        ("H003", "Hospital Supplies and Services", "CLMServicesAct", "CLMServices", true),
        ("H004", "Operating Theatre", "CLMTheatreAct", "CLMTheatre", true),
        ("H005", "Surgical Fees", "CLMSurgicalAmtAct", "CLMSurgicalAmt", true),
        ("H006", "Anesthetist Fees", "CLMAnaAct", "CLMAna", true),
        ("H007", "In-Hospital Physician Visits", "CLMNSPreHosAct", "CLMNSPreHos", true),
        ("H008", "In-Hospital Specialist Consultation Visits", "CLMPhysicianAct", "CLMPhysician", true),
        ("H009", "Ambulance Fees", "CLMAmbulanceAct", "CLMAmbulance", true),
        ("H011", "Admin Fee", "CLMAdminAct", "CLMAdmin", false),
        ("H012", "Service Tax", "CLMBoardTaxAmtAct", "CLMBoardTaxAmt", false),
    ];
    private static readonly (string Code, string Name, string Actual, string Approved, bool Fixed) MedicalReport = ("H010", "Medical Report", "CLMMedicalAct", "CLMMedical", false);

    public static string FileName(string kind, string batch) => $"TE{Tpa}{kind}{batch}.txt";

    // --- VB6 value formatting -----------------------------------------------------------------------------------------------------------

    private static object? Get(IDictionary<string, object?> r, string c) =>
        r.TryGetValue(c, out var v) ? v is DBNull ? null : v : r.FirstOrDefault(kv => string.Equals(kv.Key, c, StringComparison.OrdinalIgnoreCase)).Value is var x && x is not DBNull ? x : null;

    /// <summary>VB IsNumeric + CDbl: a number, or null.</summary>
    public static decimal? Num(object? v) => v switch
    {
        null => null,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        string s when decimal.TryParse(s.Trim(), NumberStyles.Number, Inv, out var d) => d,
        _ => null,
    };

    /// <summary>A number as VB writes it when concatenated: no trailing zeros ("1335", "12.5", "-3").</summary>
    public static string VbNumber(decimal d) => d.ToString("0.############", Inv);

    /// <summary>A database value concatenated by VB: Null is empty, numbers lose trailing zeros, text is written as stored.</summary>
    public static string Vb(object? v) => v switch
    {
        null => "",
        string s => s,
        DateTime d => d.ToString("dd/MM/yyyy", Inv),
        _ when Num(v) is { } n => VbNumber(n),
        _ => Convert.ToString(v, Inv) ?? "",
    };

    private static string Fixed(decimal d) => d.ToString("0.00", Inv);
    private static string Ymd(object? v) => v is DateTime d ? d.ToString("yyyyMMdd", Inv) : "";
    private static string Mid50(object? v) { var s = Vb(v); return s.Length > 50 ? s[..50] : s; }
    private static bool In(decimal? v, decimal[] set) => v is { } d && set.Contains(d);

    private static string Line(params string[] fields) => string.Join("|", fields);

    // --- the files ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Builds the batch's files from the view rows (legacy PrincipalASCII). Returns null when there is no claim (legacy "No record found!").</summary>
    public static SppaExport? Build(SppaSource s, string batch)
    {
        if (s.Claims.Count == 0) return null;
        var files = new List<SppaFile>();
        var bills = 0; var claimAmount = 0m;

        // AH: one line per claim invoice.
        var ah = new StringBuilder();
        foreach (var r in s.Claims)
        {
            var no = Vb(Get(r, "CASNumber")); var version = (int)(Num(Get(r, "ClaimVersion")) ?? 0);
            var head = Line(Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), Vb(Get(r, "InvNo"))) + "|";
            var invoice = Num(Get(r, "INVAmount")) ?? 0; var medical = Num(Get(r, "CLMMedical")) ?? 0;
            var tail = (Num(Get(r, "CLMPayableByMedix2M")) is { } m2 ? VbNumber(m2) : "0.00") + "|" + Ymd(Get(r, "CLMBordxDate")) + ":00:00|" + Vb(Get(r, "ClaimLastUpdateUser")) + "||";
            if (s.ClaimRows(no, version) > 1)
            {
                string amounts;
                if (In(Num(Get(r, "INVAmount")), ReportFeesAh))
                {
                    amounts = VbNumber(invoice) + "|";
                    claimAmount += invoice;
                }
                else
                {
                    // Legacy: the bill and approved amounts are the invoice less the medical report, and the same value is written a third time.
                    var net = VbNumber(invoice - medical) + "|";
                    amounts = (Num(Get(r, "CLMTotalActual")) is null ? "0.00|" : net) + (Num(Get(r, "CLMBordxAmt")) is null ? "0.00|" : net) + net;
                }
                bills++;
                ah.Append(head).Append(amounts).Append(tail).Append("0.00").Append("\r\n");
            }
            else
            {
                var actual = Num(Get(r, "CLMTotalActual")); var approved = Num(Get(r, "CLMBordxAmt"));
                ah.Append(head).Append(actual is { } a ? VbNumber(a) : "0.00").Append('|').Append(approved is { } b ? VbNumber(b) : "0.00").Append('|')
                    .Append(tail).Append(VbNumber((actual ?? 0) - (approved ?? 0))).Append("\r\n");
                bills++;
                claimAmount += approved ?? 0;
            }
        }
        files.Add(new SppaFile(FileName("AH", batch), ah.ToString()));

        // AD: the claim's benefit lines (always written, even when empty).
        var ad = new StringBuilder(); var benefitLines = 0;
        foreach (var r in s.Details)
        {
            var no = Vb(Get(r, "CASNumber")); var version = (int)(Num(Get(r, "ClaimVersion")) ?? 0);
            var head = new[] { Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), Vb(Get(r, "InvNo")) };
            var n = 0;
            void Add((string Code, string Name, string Actual, string Approved, bool Fixed) b)
            {
                if (Num(Get(r, b.Actual)) is not { } act || act == 0) return;
                n++; benefitLines++;
                var app = Num(Get(r, b.Approved));
                ad.Append(Line([.. head, n.ToString(Inv), b.Code, b.Name, "1", "1", "LUMPSUM", b.Fixed ? Fixed(act) : Vb(Get(r, b.Actual)),
                    app is null ? "0.00" : b.Fixed ? Fixed(app.Value) : Vb(Get(r, b.Approved)), "0.00", "", "0.00"])).Append("\r\n");
            }
            if (s.DetailRows(no, version) > 1)
            {
                if (In(Num(Get(r, "INVAmount")), ReportFees)) Add(MedicalReport);
                else foreach (var b in Benefits) Add(b);
            }
            else
            {
                Add(MedicalReport);
                foreach (var b in Benefits) Add(b);
            }
        }
        files.Add(new SppaFile(FileName("AD", batch), ad.ToString()));

        // AA: diagnosis, modality and days (legacy: with a second ICD code the third is left out).
        var aa = new StringBuilder();
        foreach (var r in s.Admissions)
        {
            var icd1 = Vb(Get(r, "ICDCode1")); var icd2 = Vb(Get(r, "ICDCode2")); var icd3 = Vb(Get(r, "ICDCode3"));
            var icd = icd2.Length > 0 ? $"{icd1},{icd2}" : icd3.Length > 0 ? $"{icd1},{icd2},{icd3}" : icd1;
            aa.Append(Line(Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), icd, Vb(Get(r, "ModalityCode")).Trim(), Vb(Get(r, "CLMBoardDay")).Trim(), Vb(Get(r, "CLMICUDay")).Trim())).Append("\r\n");
        }
        if (s.Admissions.Count > 0) files.Add(new SppaFile(FileName("AA", batch), aa.ToString()));

        // AN: hospital charges and medical report per claim.
        var an = new StringBuilder();
        foreach (var r in s.Notes)
        {
            var report = Num(Get(r, "CLMMedical")) ?? 0;
            an.Append(Line(Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), Vb(Get(r, "BordxRefNo")).Trim(), Vb(Get(r, "CASNumber")).Trim() + Vb(Get(r, "ClaimVersion")).Trim(),
                Ymd(Get(r, "CLMBordxDate")), Vb(Get(r, "InvNo")), VbNumber((Num(Get(r, "CLMBordxAmt")) ?? 0) - report), VbNumber(report))).Append("\r\n");
        }
        if (s.Notes.Count > 0) files.Add(new SppaFile(FileName("AN", batch), an.ToString()));

        // RD: requests.
        var rd = new StringBuilder();
        foreach (var r in s.Requests)
            rd.Append(Line(Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), Vb(Get(r, "FWReqNumber")), Vb(Get(r, "FWReqReason")))).Append("\r\n");
        if (s.Requests.Count > 0) files.Add(new SppaFile(FileName("RD", batch), rd.ToString()));

        // PH: per voucher, the hospital charges (M) and the medical report fees (R) with their cheques.
        var ph = new StringBuilder(); var phCount = 0;
        foreach (var g in s.PaymentGroups)
        {
            decimal rptFee = 0, medFee = 0; string rptChq = "", medChq = "", hos = "", pv = "", inv = ""; object? pvDate = null;
            foreach (var p in s.VoucherPayments(Vb(Get(g, "PVHOSNo")).Trim()))
            {
                var medical = Num(Get(p, "CLMMedical")) ?? 0; var paid = Num(Get(p, "HOSPaidAmt")) ?? 0;
                if (In(Num(Get(p, "INVAmount")), ReportFees))
                {
                    rptFee += Num(Get(p, "INVAmount")) ?? 0; rptChq = Mid50(Get(p, "PVHOSChequeNoMR")); medChq = Mid50(Get(p, "PVHOSChequeNo")); inv = Vb(Get(p, "CASESPRefNo"));
                }
                else if (inv.Trim() != Vb(Get(p, "CASESPRefNo")).Trim())
                {
                    medFee += paid - medical; medChq = Mid50(Get(p, "PVHOSChequeNo")); rptFee += medical; rptChq = Mid50(Get(p, "PVHOSChequeNoMR"));
                }
                else { medFee += paid - medical; medChq = Mid50(Get(p, "PVHOSChequeNo")); }
                hos = Vb(Get(p, "HOSCode")); pv = Vb(Get(p, "PVHOSNo")); pvDate = Get(p, "PVHOSDate");
            }
            if (medFee != 0) { ph.Append(Line(Tpa, hos, pv, "C", medChq, Ymd(pvDate), VbNumber(medFee), "M", Ymd(pvDate))).Append("\r\n"); phCount++; }
            if (rptFee != 0) { ph.Append(Line(Tpa, hos, pv, "C", rptChq, Ymd(pvDate), VbNumber(rptFee), "R", Ymd(pvDate))).Append("\r\n"); phCount++; }
        }
        if (s.PaymentGroups.Count > 0) files.Add(new SppaFile(FileName("PH", batch), ph.ToString()));

        // PD: payment lines per claim invoice.
        var pd = new StringBuilder(); var pdCount = 0;
        foreach (var r in s.Payments)
        {
            var no = Vb(Get(r, "CASNumber")).Trim(); var invNo = Vb(Get(r, "InvNo")).Trim();
            var medical = Num(Get(r, "CLMMedical")) ?? 0; var paid = Num(Get(r, "HOSPaidAmt")) ?? 0; var bordx = Num(Get(r, "CLMBordxAmt")) ?? 0;
            var head = new[] { Tpa, Vb(Get(r, "HOSCode")), Vb(Get(r, "CASESPRefNo")), Vb(Get(r, "PVHOSNo")), Vb(Get(r, "InvNo")) };
            var chq = Vb(Get(r, "PVHOSChequeNo")); var chqMr = Vb(Get(r, "PVHOSChequeNoMR"));
            if (s.CasePaymentRows(no) > 1)
            {
                var invoiceAmount = s.CaseInvoiceAmount(no, invNo);
                var fee = In(Num(Get(r, "INVAmount")), ReportFees) ? medical : paid - medical;
                if (fee == 0) continue;
                pd.Append(In(invoiceAmount, ReportFees)
                    ? Line([.. head, VbNumber(invoiceAmount!.Value), VbNumber(invoiceAmount.Value), chqMr == "-" ? Mid50(chq) : Mid50(chqMr)])
                    : Line([.. head, VbNumber(bordx - medical), VbNumber(paid - medical), Mid50(chq)])).Append("\r\n");
                pdCount++;
            }
            else
            {
                // Legacy: a payment that is not itself a medical report fee also gets a second line for the medical report (with its MR cheque).
                var reportOnly = In(paid, ReportFees);
                pd.Append(Line([.. head, VbNumber(bordx), VbNumber(reportOnly ? paid : paid - medical), chq != "-" ? Mid50(chq) : Mid50(chqMr)])).Append("\r\n");
                pdCount++;
                if (!reportOnly) { pd.Append(Line([.. head, VbNumber(bordx), VbNumber(medical), Mid50(chqMr)])).Append("\r\n"); pdCount++; }
            }
        }
        if (s.Payments.Count > 0) files.Add(new SppaFile(FileName("PD", batch), pd.ToString()));

        // PS: payment slips.
        var ps = new StringBuilder();
        foreach (var r in s.PaymentSlips)
        {
            var courier = Vb(Get(r, "PAYCourierNo"));
            ps.Append(Line(Tpa, Vb(Get(r, "HOSCode")), courier.Length > 0 ? courier.Trim() : "N/A", Ymd(Get(r, "PVHOSDate")), Vb(Get(r, "PVHOSNo")).Trim())).Append("\r\n");
        }
        if (s.PaymentSlips.Count > 0) files.Add(new SppaFile(FileName("PS", batch), ps.ToString()));

        var totals = new SppaTotals(bills, claimAmount, benefitLines, phCount, pdCount, s.Admissions.Count, s.Notes.Count, s.PaymentSlips.Count, s.Requests.Count);
        files.Add(new SppaFile(FileName("AB", batch), Line(totals.Bills.ToString(Inv), VbNumber(totals.ClaimAmount), "0.00", totals.BenefitLines.ToString(Inv), totals.PaymentHeaders.ToString(Inv),
            totals.PaymentDetails.ToString(Inv), totals.Admissions.ToString(Inv), totals.Notes.ToString(Inv), totals.PaymentSlips.ToString(Inv), totals.Requests.ToString(Inv)) + "\r\n"));
        return new SppaExport(batch, files, totals);
    }
}

/// <summary>The batch as one zip of its text files (Latin-1, CRLF, like the other ASCII interface files).</summary>
public static class SppaZip
{
    public static byte[] Create(SppaExport export)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in export.Files)
            {
                using var w = zip.CreateEntry(f.Name).Open();
                var bytes = FixedWidth.Bytes(f.Content);
                w.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }
}

public interface ISppaClaimExportRepository
{
    /// <summary>Builds the batch for the range and takes its batch number; null when no claim falls in the range.</summary>
    Task<SppaExport?> ExportAsync(SppaExportRequest request, string user, CancellationToken ct = default);
}
