using System.Globalization;
using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Cases;

/// <summary>
/// Case > Claims ASCII (From SPPA) (frmCLMASCIIUpload2012): the pipe-delimited claims file SPPA sent (a "CH" file: letters 6-7 of the name). Every line is one
/// hospital bill of a foreign worker; the first bill of an admission registers the case and its worksheet (version 1), later bills of the same admission
/// become further invoices of that case. This is how almost every FWHS case was registered (2011 to 2021).
/// </summary>
public sealed record SppaUploadRequest(string? FileName, string? Content);

/// <summary>One bill line, field by field as the legacy form read it (fields 0, 7, 10, 11, 15, 21, 24 and 25 were not used).</summary>
public sealed record SppaUploadLine(
    int Line, string HospitalCode, string PayorCode, string PolicyUniqueNo, string WorkerUniqueNo, string PolicyNo, DateTime PolicyEffDate, string PassportNo, string Name,
    string EspRefNo, DateTime Admitted, DateTime Discharged, string BillNo, DateTime BillDate, decimal BilledAmount, DateTime? BillLodgedDate, string BillLodgedBy,
    string NatureCode, string MrnNo, DateTime? LastDocDate);

public static class SppaUploadAction
{
    public const string NewCase = "New case", Invoice = "Further invoice", Skipped = "Skipped";
}

/// <summary>What the upload does with one line: a new case, a further invoice of a case registered by an earlier line, or nothing (with the reason).</summary>
public sealed record SppaUploadRow(
    int Line, string PayorCode, string WorkerUniqueNo, string Name, string EspRefNo, DateTime Admitted, string BillNo, decimal BilledAmount, string Action,
    string? MemberNumber, string? CaseNumber, string? Message);

/// <summary>A problem with the file itself; any of these stops the upload (the legacy form failed and rolled back on the same things).</summary>
public sealed record SppaUploadIssue(int Line, string Message);

public sealed record SppaUploadPreview(string FileName, IReadOnlyList<SppaUploadRow> Rows, IReadOnlyList<SppaUploadIssue> Issues)
{
    public int NewCases => Rows.Count(r => r.Action == SppaUploadAction.NewCase);
    public int Invoices => Rows.Count(r => r.Action != SppaUploadAction.Skipped);
    public int Skipped => Rows.Count(r => r.Action == SppaUploadAction.Skipped);
    public bool CanUpload => Issues.Count == 0 && Invoices > 0;
}

/// <summary>The rows with the case numbers given to the new cases.</summary>
public sealed record SppaUploadResult(string FileName, IReadOnlyList<SppaUploadRow> Rows)
{
    public int NewCases => Rows.Count(r => r.Action == SppaUploadAction.NewCase);
    public int Invoices => Rows.Count(r => r.Action != SppaUploadAction.Skipped);
    public int Skipped => Rows.Count(r => r.Action == SppaUploadAction.Skipped);
}

/// <summary>A claim row of a member with the same policy keys and effective date, for the balance limit (in the order the legacy form read them).</summary>
public sealed record SppaMemberClaim(string CaseNumber, int Version, decimal BordxAmount);

public static partial class SppaClaimUpload
{
    /// <summary>The legacy form read up to field 26 (TPA last document date); a shorter line failed the upload.</summary>
    public const int FieldCount = 27;

    /// <summary>Room and board benefit per day the form put on every new worksheet.</summary>
    public const decimal RoomBoardPerDay = 76m;

    /// <summary>The form only processed a file whose name has "CH" at positions 6-7 (Mid(name, 6, 2)); any other file did nothing.</summary>
    public static bool IsClaimsFile(string? fileName) => fileName is { Length: >= 7 } && fileName.Substring(5, 2).Equals("CH", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex LineBreak();

    public static List<SppaUploadIssue> CheckFile(SppaUploadRequest r)
    {
        var name = r.FileName?.Trim() ?? "";
        if (name.Length == 0) return [new(0, "Please Insert File")];
        var issues = new List<SppaUploadIssue>();
        if (!IsClaimsFile(name)) issues.Add(new(0, $"{name} is not an SPPA claims file (the name needs CH at positions 6-7)."));
        if (string.IsNullOrWhiteSpace(r.Content)) issues.Add(new(0, "The file is empty"));
        return issues;
    }

    /// <summary>Splits the lines on "|" and trims the fields; dates are yyyymmdd. Anything the legacy form would have failed on is an issue.</summary>
    public static List<SppaUploadLine> Parse(string content, List<SppaUploadIssue> issues)
    {
        var lines = new List<SppaUploadLine>();
        var raw = LineBreak().Split(content);
        for (var i = 0; i < raw.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(raw[i])) continue;
            var line = i + 1;
            var f = raw[i].Split('|');
            if (f.Length < FieldCount) { issues.Add(new(line, $"The line has {f.Length} fields; a claims line needs {FieldCount}")); continue; }
            string T(int n) => f[n].Trim();
            var ok = true;
            DateTime? Date(int n, string label, bool required)
            {
                var s = T(n);
                if (s.Length == 0) { if (required) { issues.Add(new(line, $"{label} Is Blank")); ok = false; } return null; }
                if (s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
                issues.Add(new(line, $"{label} '{s}' is not a valid date (yyyymmdd)")); ok = false; return null;
            }
            var eff = Date(6, "Policy Effective Date", true);
            var admitted = Date(13, "Date Admitted", true);
            var discharged = Date(14, "Date Discharged", true);
            var billDate = Date(17, "Bill Date", true);
            var lodged = Date(19, "Bill Lodged Date", false);
            var lastDoc = Date(26, "Last Document Date", false);
            if (!decimal.TryParse(T(18), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) { issues.Add(new(line, T(18).Length == 0 ? "Billed Amount Is Blank" : $"Billed Amount '{T(18)}' is not a number")); ok = false; }
            if (admitted is { } a && discharged is { } d && d < a) { issues.Add(new(line, "Date Discharged is before Date Admitted")); ok = false; }
            if (T(2).Length == 0 || T(3).Length == 0 || T(4).Length == 0) { issues.Add(new(line, "Insurer Code, Insurer Policy ID and Foreign Worker ID are needed to find the member")); ok = false; }
            if (T(16).Length == 0) { issues.Add(new(line, "Bill No Is Blank")); ok = false; }
            if (!ok) continue;
            lines.Add(new SppaUploadLine(line, T(1), T(2), T(3), T(4), T(5), eff!.Value, T(8), T(9), T(12), admitted!.Value, discharged!.Value, T(16), billDate!.Value, amount,
                lodged, T(20), T(22), T(23), lastDoc));
        }
        return lines;
    }

    /// <summary>Room and board days: discharge - admission + 1.</summary>
    public static int RoomBoardDays(SppaUploadLine l) => (l.Discharged - l.Admitted).Days + 1;

    public static decimal RoomBoardLimit(SppaUploadLine l) => RoomBoardPerDay * RoomBoardDays(l);

    /// <summary>Annual limit: RM 10,000 for policies effective before 1 August 2016, RM 20,000 after.</summary>
    public static decimal AnnualLimit(DateTime policyEffDate) => policyEffDate < new DateTime(2016, 8, 1) ? 10_000m : 20_000m;

    /// <summary>
    /// The balance limit put on the new worksheet: the annual limit less the bordereau amount of the LAST claim row the legacy form read for members with the
    /// same policy keys and effective date (it overwrote the amount row by row, it did not add them up), or the full limit when that amount is 0.
    /// </summary>
    public static decimal BalanceLimit(DateTime policyEffDate, IReadOnlyList<SppaMemberClaim> claims)
    {
        var used = claims.Count == 0 ? 0 : claims[^1].BordxAmount;
        return AnnualLimit(policyEffDate) - used;
    }

    private const string Symbols = "!@#$%^&*()?><~`+=|\\/.',{}[];:-% _";

    /// <summary>
    /// The SPPA claim ID: "42" + the hospital's KodPTJ + the bill number without symbols, right-aligned in 7 digits for insurers JE and KU and 12 for the
    /// others (zero-padded). Empty when the hospital has no KodPTJ.
    /// </summary>
    public static string ClaimId(string? kodPtj, string payorCode, string billNo)
    {
        if (string.IsNullOrEmpty(kodPtj)) return "";
        var bill = new string(billNo.Trim().Where(c => !Symbols.Contains(c)).ToArray());
        var width = payorCode is "JE" or "KU" ? 7 : 12;
        bill = bill.Length >= width ? bill[^width..] : bill.PadLeft(width, '0');
        return "42" + kodPtj + bill;
    }

    /// <summary>Lines of one admission: same admission date, insurer policy ID, worker ID and ESP reference (the legacy duplicate check on CAS).</summary>
    public static (DateTime, string, string, string) AdmissionKey(SppaUploadLine l) =>
        (l.Admitted, l.PolicyUniqueNo.ToUpperInvariant(), l.WorkerUniqueNo.ToUpperInvariant(), l.EspRefNo.ToUpperInvariant());

    /// <summary>The member keys (payor, insurer policy ID, worker ID).</summary>
    public static (string, string, string) MemberKey(SppaUploadLine l) => (l.PayorCode.ToUpperInvariant(), l.PolicyUniqueNo.ToUpperInvariant(), l.WorkerUniqueNo.ToUpperInvariant());

    /// <summary>
    /// Decides each line. The first line of an admission that is not registered yet is a new case; the following lines of that admission add invoices to it.
    /// Lines whose member is not found, whose admission is already registered by an earlier upload, or whose bill is already on that case are skipped.
    /// </summary>
    public static List<SppaUploadRow> Plan(
        IReadOnlyList<SppaUploadLine> lines, Func<SppaUploadLine, string?> member, Func<SppaUploadLine, string?> registeredCase, Func<SppaUploadLine, bool> billOnCase)
    {
        var rows = new List<SppaUploadRow>();
        var fileCases = new Dictionary<(DateTime, string, string, string), (int Line, HashSet<string> Bills)>();
        foreach (var l in lines)
        {
            SppaUploadRow Row(string action, string? mbm, string? message) =>
                new(l.Line, l.PayorCode, l.WorkerUniqueNo, l.Name, l.EspRefNo, l.Admitted, l.BillNo, l.BilledAmount, action, mbm, null, message);
            var no = member(l);
            if (no is null) { rows.Add(Row(SppaUploadAction.Skipped, null, "Member not found (insurer code, insurer policy ID and foreign worker ID)")); continue; }
            var key = AdmissionKey(l);
            if (fileCases.TryGetValue(key, out var first))
            {
                rows.Add(first.Bills.Add(l.BillNo.ToUpperInvariant())
                    ? Row(SppaUploadAction.Invoice, no, $"Case of line {first.Line}")
                    : Row(SppaUploadAction.Skipped, no, $"Bill {l.BillNo} is already in the file for this admission"));
                continue;
            }
            if (registeredCase(l) is { } existing)
            {
                rows.Add(Row(SppaUploadAction.Skipped, no, billOnCase(l)
                    ? $"Already uploaded: bill {l.BillNo} is on case {existing}"
                    : $"The admission is already registered as case {existing}; add the bill in Invoice Processing"));
                continue;
            }
            fileCases[key] = (l.Line, [l.BillNo.ToUpperInvariant()]);
            rows.Add(Row(SppaUploadAction.NewCase, no, null));
        }
        return rows;
    }
}

public interface ISppaClaimUploadRepository
{
    /// <summary>Reads the file and says what each line would do. Nothing is written.</summary>
    Task<SppaUploadPreview> PreviewAsync(SppaUploadRequest request, CancellationToken ct = default);

    /// <summary>Registers the cases, worksheets and invoices through the legacy procedures, in one transaction per database.</summary>
    Task<SppaUploadResult> UploadAsync(SppaUploadRequest request, string user, CancellationToken ct = default);
}
