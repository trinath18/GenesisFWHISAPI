using System.Globalization;
using System.Text;

namespace GenesisFIS.Core.Cases;

/// <summary>
/// Building blocks of the legacy fixed-width "ASCII" interface files (Case menu: Claim ASCII, Case ASCII, MedGen, Kurnia, SPPA, Zurich).
/// They reproduce the VB6 output byte for byte: <c>l(value, width)</c> was VB's <c>LSet</c> (left-justified, space-padded, cut to the width),
/// dates were <c>Format(d, "ddmmyyyy")</c>, amounts <c>Format(x, "#,##0.00")</c>, and every file was written with <c>Print #</c> (ANSI, CRLF).
/// </summary>
public static class FixedWidth
{
    /// <summary>The legacy l(): left-justified, padded with spaces, cut to the width. Null is empty.</summary>
    public static string L(object? value, int width)
    {
        var s = value switch { null or DBNull => "", DateTime d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
        return s.Length >= width ? s[..width] : s.PadRight(width);
    }

    /// <summary>Format(d, "ddmmyyyy"); empty for no date (the legacy left these blank).</summary>
    public static string Dmy(DateTime? d) => d is { } v && v.Year > 1900 ? v.ToString("ddMMyyyy", CultureInfo.InvariantCulture) : "";

    /// <summary>Format(x, "#,##0.00"); empty when there is no amount.</summary>
    public static string Money(decimal? x) => x is { } v ? v.ToString("#,##0.00", CultureInfo.InvariantCulture) : "";

    /// <summary>VB Format(n, "00000") style zero padding.</summary>
    public static string Zero(int n, int digits) => n.ToString(new string('0', digits), CultureInfo.InvariantCulture);

    /// <summary>The files were written with VB's Print # in the Windows ANSI code page; Latin-1 gives the same bytes for every character the data uses.</summary>
    public static byte[] Bytes(string content) => Encoding.Latin1.GetBytes(content);
}

/// <summary>A row of a legacy procedure or view, read by column name (case-insensitive), as the VB recordsets were.</summary>
public sealed class AsciiRow(IReadOnlyDictionary<string, object?> values)
{
    private readonly Dictionary<string, object?> _v = new(values, StringComparer.OrdinalIgnoreCase);
    public object? this[string column] => _v.TryGetValue(column, out var v) && v is not DBNull ? v : null;
    /// <summary>The value as VB's CStr wrote it: trimmed text; numbers without trailing zeros (money 5000.0000 is "5000").</summary>
    public string S(string column) => this[column] switch
    {
        null => "",
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        var v => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim(),
    };
    public bool Has(string column) => S(column).Length > 0;
    public DateTime? D(string column) => this[column] switch { DateTime d => d, string s when DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var d) => d, _ => null };
    public decimal? M(string column) => this[column] switch { null => null, decimal m => m, var o => decimal.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : null };
}

/// <summary>A generated interface file, returned to the browser instead of being written to the legacy server share and C:\ drive.</summary>
public sealed record AsciiFile(string FileName, byte[] Content, int Records, string Batch);

/// <summary>
/// Record layouts of Case > Claim ASCII (frmNewClaimAscii) and Case > Case ASCII (frmCaseASCII), field by field as the VB built them.
/// Kept on purpose for byte-compatibility with the receiving systems (flag them to the payor before changing):
/// a blank patient IC is written 30 wide instead of 20, and every data line ends CR + CRLF (the code added Chr(13) and Print # added CRLF).
/// </summary>
public static class ClaimAsciiLayout
{
    private static string L(object? v, int w) => FixedWidth.L(v, w);
    private const string DataEnd = "\r\r\n", LineEnd = "\r\n";

    /// <summary>Header line: F + payor + batch + run date ddmmyyyy (the legacy Trim() removed the padding).</summary>
    public static string Header(string payor, string batch, DateTime runDate) => "F" + payor + batch + runDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture) + LineEnd;

    /// <summary>Trailer line: T + record count (10 wide) + 164 spaces.</summary>
    public static string Trailer(int count) => "T" + L(count, 10) + L("", 164) + LineEnd;

    /// <summary>One claim version of an accepted case (Accepted Cases option), or one rejected case (Rejected Cases option: no bordx, worksheet or amount fields).</summary>
    public static string ClaimLine(AsciiRow r, bool rejected)
    {
        var b = new StringBuilder(5000);
        if (rejected) b.Append(L("N", 1)).Append(L("", 15)).Append(L("", 8));
        else b.Append(L(r.S("CLMBordxWSStatus"), 1)).Append(L(r.S("BordxRefNo"), 15)).Append(L(FixedWidth.Dmy(r.D("BordxDate")), 8));
        b.Append(L(r.S("PAYCode"), 2)).Append(L(r.S("CasNumber"), 15));
        b.Append(rejected ? L("", 15) : L(r.S("CasNumber") + "-" + r.S("ClaimVersion"), 15));
        b.Append(L(r.S("MBMNumber"), 15)).Append(L(r.S("MBMPatientCovID"), 2)).Append(L(r.S("MBMPolicyNo"), 25)).Append(L(r.S("MBMPolSubNo1"), 25)).Append(L(r.S("MBMPOLSubNo2"), 25));
        b.Append(L(FixedWidth.Dmy(r.D("MBMPayorEffDate")), 8)).Append(L(FixedWidth.Dmy(r.D("MBMPayorExpDate")), 8)).Append(L(r.S("MBMAgentCode"), 15));
        b.Append(L(r.S("MBMName"), 60)).Append(L(r.S("MBMIcBcPp"), 20)).Append(L(r.S("MBMSex"), 1)).Append(L(r.S("RACCode"), 1)).Append(L(FixedWidth.Dmy(r.D("MBMDOB")), 8));
        b.Append(L("", 1)).Append(L(r.S("MBMRenewFlag"), 1)).Append(L(r.S("MBMTakeover"), 1)).Append(L(r.S("INSCode"), 2)).Append(L(r.S("PLNCode"), 4));
        b.Append(L(r.S("MBMGroupCompany"), 50)).Append(L(r.S("BRNCode"), 50)).Append(L(r.S("AGECode"), 2)).Append(L(r.S("PatientName"), 60));
        b.Append(r.Has("ICNo") ? L(r.S("ICNo"), 20) : L("", 30));   // legacy quirk kept: 30 when blank
        b.Append(L(r.S("Sex"), 1)).Append(L(r.S("RACCode"), 1)).Append(L(r.S("AgeBand"), 2)).Append(L(FixedWidth.Dmy(r.D("DOB")), 8)).Append(L(r.S("HOSName"), 80));
        b.Append(L(r.S("FinalDiag"), 5)).Append(L(r.S("FinalDiag2"), 5)).Append(L(r.S("FinalDiag3"), 5));
        b.Append(L(FixedWidth.Dmy(r.D("CASDateAdmitted")), 8)).Append(L(FixedWidth.Dmy(r.D("CASDischargeDate")), 8));
        if (rejected) b.Append(L("", 12)).Append(L("", 12));
        else b.Append(L(FixedWidth.Money(r.M("CLMTotalActual")), 12)).Append(L(FixedWidth.Money(r.M("CLMTotalApproved")), 12));
        b.Append(L(r.S("CASClaimability"), 1)).Append(L(r.S("CASAdmissionReason"), 200)).Append(L(r.S("AttendDoctor"), 100)).Append(L(r.S("ClaimNatureCode"), 5)).Append(L(r.S("ModalityCode"), 1));
        b.Append(L(r.S("CASStayType"), 1)).Append(L(r.S("CASPAYType"), 1)).Append(L(r.S("CASUDReasons1"), 100)).Append(L("", 3000));
        if (rejected) b.Append(L(r.S("CASREPCode1"), 10)).Append(L(r.S("CASREPCode2"), 10)).Append(L(r.S("CASREPCode3"), 10)).Append(L(FixedWidth.Money(r.M("CASReserveAmount")), 12)).Append(L("", 942));
        else b.Append(L("", 984));
        return b.Append(DataEnd).ToString();
    }

    /// <summary>One case of Case > Case ASCII (registered cases by registration date). No header or trailer.</summary>
    public static string CaseLine(AsciiRow r, DateTime runDate, string batch)
    {
        var b = new StringBuilder(5000);
        b.Append(L(r.Has("CASASCIIAmendStatus") ? r.S("CASASCIIAmendStatus") : "N", 1)).Append(L(r.S("PAYCode"), 2)).Append(L(r.S("CasNumber"), 15)).Append(L(r.S("MBMNumber"), 15)).Append(L(r.S("MBMPatientCovID"), 2));
        b.Append(L(r.S("MBMPolicyNo"), 25)).Append(L(r.S("MBMPolSubNo1"), 25)).Append(L(r.S("MBMPOLSubNo2"), 25)).Append(L(FixedWidth.Dmy(r.D("MBMPayorEffDate")), 8)).Append(L(FixedWidth.Dmy(r.D("MBMPayorExpDate")), 8));
        b.Append(L(r.S("MBMName"), 60)).Append(L(r.S("MBMIcBcPp"), 20)).Append(L(FixedWidth.Dmy(r.D("MBMDOB")), 8)).Append(L(r.S("PLNCode"), 4)).Append(L(r.S("MBMGroupCompany"), 50)).Append(L(r.S("PatientName"), 60));
        b.Append(r.Has("ICNo") ? L(r.S("ICNo"), 20) : L("", 30));   // legacy quirk kept
        b.Append(L(FixedWidth.Dmy(r.D("DOB")), 8)).Append(L(FixedWidth.Dmy(r.D("CASDateAdmitted")), 8)).Append(L(FixedWidth.Dmy(r.D("CASDischargeDate")), 8));
        b.Append(L(r.S("CASClaimability"), 1)).Append(L(r.S("CASAdmissionReason"), 200)).Append(L(r.S("CASStayType"), 1)).Append(L(r.S("CASPAYType"), 1));
        // Reserve and LG amounts were written as the raw values (no number format).
        b.Append(L(r.S("CASReserveAmount"), 8)).Append(L(r.S("CASLGAmt"), 8));
        b.Append(L(runDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture), 8)).Append(L(batch, 5)).Append(L("", 4370));
        return b.Append(DataEnd).ToString();
    }

    /// <summary>The legacy file name: payor + yyMMdd + (batch).</summary>
    public static string FileName(string payor, DateTime runDate, string batch) => $"{payor}{runDate:yyMMdd}({batch})";
}
