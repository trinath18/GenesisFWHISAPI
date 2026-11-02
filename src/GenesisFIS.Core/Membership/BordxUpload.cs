using System.Globalization;
using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Membership;

/// <summary>
/// The six pipe-delimited FWHS bordereaux files of Membership > Upload Bordx (FrmNewMBMEndt2012). The kind is the two letters at positions 6-7 of the
/// file name (ETFWMMP002812.TXT is an MP file). Master files: MP employer policies (GroupCompanyFW), MW foreign workers (MBM + MBMOthers).
/// MCO billing files (FWHSTPAFee): NP new policies, EP policy endorsements, EX cancellations; EW worker endorsements (FWHSEndorsementMBM).
/// </summary>
public static class BordxKind
{
    public const string Mp = "MP", Mw = "MW", Np = "NP", Ep = "EP", Ex = "EX", Ew = "EW";

    /// <summary>Upload order of the legacy form.</summary>
    public static readonly IReadOnlyList<string> All = [Mp, Mw, Np, Ep, Ex, Ew];

    /// <summary>Fields a line must have: the legacy form reads up to this index and failed the whole upload when a line was shorter.</summary>
    public static int FieldCount(string kind) => kind switch { Mp or Np or Ep => 33, Mw => 22, Ex => 15, Ew => 25, _ => 0 };

    public static string? Of(string? fileName) =>
        fileName is { Length: >= 7 } && All.Contains(fileName.Substring(5, 2).ToUpperInvariant()) ? fileName.Substring(5, 2).ToUpperInvariant() : null;
}

public sealed record BordxUploadFile(string? Name, string? Content);

public sealed record BordxUploadRequest(IReadOnlyList<BordxUploadFile>? Files);

/// <summary>One line of the error list (Line, Policy, Name, Message, File). Blocking issues stop the upload; the others are the legacy Verify messages, which never did.</summary>
public sealed record BordxIssue(int Line, string? PolicyNo, string? Name, string Message, string FileName, bool Blocking);

/// <summary>What the upload does with one file: records read, rows inserted and rows updated (existing employer / worker), rows skipped.</summary>
public sealed record BordxFileSummary(string Kind, string FileName, int Records, int Inserts, int Updates, int Skipped);

public sealed record BordxUploadPreview(IReadOnlyList<BordxFileSummary> Files, IReadOnlyList<BordxIssue> Issues, bool CanUpload)
{
    public int TotalErrors => Issues.Count;
}

public sealed record BordxUploadResult(IReadOnlyList<BordxFileSummary> Files, IReadOnlyList<string> NewMemberNumbers);

/// <summary>A policy line (MP, NP, EP, EX). Fields an EX line does not carry stay empty.</summary>
public sealed record BordxPolicyRow(
    int Line, string PayorCode, string InsurerRefNo, string PolicyNo, DateTime? PolicyDate, DateTime? EffDate, DateTime? ExpDate,
    string EmployerType, string EmployerCode, string Name, string Address1, string Address2, string Address3, string Address4, string City, string PostCode, string State,
    string BizNature, string AgentCode, string OfficeTel, string HomeTel, string MobileTel, string Email,
    decimal ClaimLimit, int Workers, decimal Premium, decimal McoFee, string PolicyStatus, decimal PremiumTax, decimal TpcaTax, decimal Discount,
    DateTime? Created, DateTime? Updated, string? EndorseNo, DateTime? EndorseDate, DateTime? EndorseEffDate, string? BatchNo, DateTime? Submission);

/// <summary>A foreign worker line (MW, EW). Endorsement fields are only on EW lines.</summary>
public sealed record BordxWorkerRow(
    int Line, string PayorCode, string InsurerUniqueNo, string FwUniqueNo, string PolicyNo, DateTime? EffDate, string Nationality, string PassportNo, string Occupation,
    string Name, string Gender, DateTime? Dob, decimal ClaimLimit, decimal Premium, decimal McoFee, string Renewal, string Status, string InsuredFor, string WorkPermitNo,
    DateTime? WorkPermitExpiry, DateTime? Created, DateTime? Updated, string? EndorseNo, DateTime? EndorseDate, string? EndorseType, string? BatchNo, DateTime? BatchDate);

public sealed record BordxParsedFile(string Kind, string FileName, IReadOnlyList<BordxPolicyRow> Policies, IReadOnlyList<BordxWorkerRow> Workers)
{
    public int Records => Policies.Count + Workers.Count;
}

/// <summary>Reads the files the way the legacy form did (Split on "|", Trim, dates as yyyymmdd) and reports what would have failed it as blocking issues.</summary>
public static partial class BordxParser
{
    /// <summary>The legacy form passed '' for an empty optional date, which SQL Server stores as 1900-01-01; reports and enquiries expect that value.</summary>
    public static readonly DateTime BlankDate = new(1900, 1, 1);

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex LineBreak();

    private sealed class Reader(string[] f, int line, string file, List<BordxIssue> issues)
    {
        public string T(int i) => i < f.Length ? f[i].Trim() : "";
        public string Raw(int i) => i < f.Length ? f[i] : "";
        public string First(int i, int n) => T(i).Length > n ? T(i)[..n] : T(i);
        private void Bad(string message) => issues.Add(new BordxIssue(line, T(PolicyIndex), Name, message, file, true));
        public int PolicyIndex { get; init; }
        public string? Name { get; init; }

        /// <summary>yyyymmdd (Mid 1-4, 5-2, 7-2 of the legacy code). Optional dates that are empty give the 1900-01-01 the legacy upload stored.</summary>
        public DateTime? Date(int i, string label, bool required)
        {
            var s = T(i);
            if (s.Length == 0) { if (required) { Bad($"{label} Is Blank"); return null; } return BlankDate; }
            if (s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            Bad($"{label} '{s}' is not a valid date (yyyymmdd)");
            return null;
        }

        /// <summary>Amounts went into the procedures as numbers; a blank or non-numeric one failed the upload. EP lines passed them quoted, so blank there is 0.</summary>
        public decimal Money(int i, string label, bool blankIsZero = false)
        {
            var s = T(i);
            if (s.Length == 0 && blankIsZero) return 0;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return v;
            Bad(s.Length == 0 ? $"{label} Is Blank" : $"{label} '{s}' is not a number");
            return 0;
        }

        public int Count(int i, string label, bool blankIsZero = false)
        {
            var s = T(i);
            if (s.Length == 0 && blankIsZero) return 0;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            Bad(s.Length == 0 ? $"{label} Is Blank" : $"{label} '{s}' is not a whole number");
            return 0;
        }
    }

    public static IEnumerable<(int Line, string[] Fields)> Lines(string content)
    {
        var lines = LineBreak().Split(content);
        for (var i = 0; i < lines.Length; i++)
            if (!string.IsNullOrWhiteSpace(lines[i])) yield return (i + 1, lines[i].Split('|'));
    }

    public static BordxParsedFile Parse(string kind, string fileName, string content, List<BordxIssue> issues)
    {
        var policies = new List<BordxPolicyRow>(); var workers = new List<BordxWorkerRow>();
        foreach (var (line, f) in Lines(content))
        {
            var need = BordxKind.FieldCount(kind);
            if (f.Length < need)
            {
                issues.Add(new BordxIssue(line, null, null, $"The line has {f.Length} fields; a {kind} file needs {need}", fileName, true));
                continue;
            }
            switch (kind)
            {
                case BordxKind.Mp: policies.Add(Mp(new Reader(f, line, fileName, issues) { PolicyIndex = 3, Name = f[9].Trim() }, line, fileName, issues)); break;
                case BordxKind.Np: policies.Add(Np(new Reader(f, line, fileName, issues) { PolicyIndex = 3, Name = f[9].Trim() }, line)); break;
                case BordxKind.Ep: policies.Add(Ep(new Reader(f, line, fileName, issues) { PolicyIndex = 6, Name = f[11].Trim() }, line)); break;
                case BordxKind.Ex: policies.Add(Ex(new Reader(f, line, fileName, issues) { PolicyIndex = 6 }, line)); break;
                case BordxKind.Mw: workers.Add(Mw(new Reader(f, line, fileName, issues) { PolicyIndex = 4, Name = f[9].Trim() }, line, fileName, issues)); break;
                case BordxKind.Ew: workers.Add(Ew(new Reader(f, line, fileName, issues) { PolicyIndex = 6, Name = f[12].Trim() }, line)); break;
            }
        }
        return new BordxParsedFile(kind, fileName, policies, workers);
    }

    /// <summary>The legacy Verify checks (VerifyNewMemberTwo). They only listed the line; Upload stayed enabled.</summary>
    private static void Check(List<BordxIssue> issues, int line, string? policy, string? name, string file, params (string Value, string Message)[] checks)
    {
        foreach (var (value, message) in checks)
            if (value.Length == 0) issues.Add(new BordxIssue(line, policy, name, message, file, false));
    }

    /// <summary>SaveMBMPolicyCtrl: GroupCompanyFW, keyed by payor (first 2 characters) and insurer reference.</summary>
    private static BordxPolicyRow Mp(Reader r, int line, string file, List<BordxIssue> issues)
    {
        Check(issues, line, r.T(3), r.T(9), file, (r.T(0), "TPCA Code Is Blank"), (r.T(9), "Employer Name Is Blank"), (r.T(1), "Insurer Code Is Blank"),
            (r.T(2), "Insurer Number Is Blank"), (r.T(3), "Policy Number Is Blank"), (r.T(10), "Address Is Blank"));
        return new BordxPolicyRow(line, r.First(1, 2), r.T(2), r.First(3, 25), r.Date(4, "Policy Date", true), r.Date(5, "Effective Date", true), r.Date(6, "Expiry Date", true),
            r.T(7), r.T(8).Replace("'", ""), r.T(9), r.T(10), r.T(11), r.T(12), r.T(13), r.T(14), r.T(15), r.T(16), r.T(17), r.T(18), r.T(19), r.T(20), r.T(21), r.T(22),
            r.Money(23, "Max Claim Limit"), r.Count(24, "Total FW"), r.Money(25, "Total Premium"), r.Money(26, "MCO Fee"), r.T(27),
            r.Money(28, "Premium Tax"), r.Money(29, "TPCA Tax"), r.Money(30, "Discount"), r.Date(31, "Record Created Date", true), r.Date(32, "Record Update Date", false),
            null, null, null, null, null);
    }

    /// <summary>SaveFeeNewPolCtrl: one FWHSTPAFee row per line.</summary>
    private static BordxPolicyRow Np(Reader r, int line) =>
        new(line, r.T(1), r.T(2), r.First(3, 25), r.Date(4, "Policy Date", true), r.Date(5, "Effective Date", true), r.Date(6, "Expiry Date", true),
            r.T(7), r.T(8).Replace("'", ""), r.T(9), r.T(10), r.T(11), r.T(12), r.T(13), r.T(14), r.T(15), r.T(16), r.T(17), r.T(18), r.T(19), r.T(20), r.T(21), r.T(22),
            r.Money(23, "Max Claim Limit"), r.Count(24, "Total FW"), r.Money(25, "Total Premium"), r.Money(26, "MCO Fee"), "",
            r.Money(27, "Premium Tax"), r.Money(28, "TPCA Tax"), r.Money(29, "Discount"), null, r.Date(32, "Record Update Date", false),
            null, null, null, r.T(30), r.Date(31, "Submission Date", false));

    /// <summary>SaveFeeEndPolCtrl: endorsement fee row. Amounts went in quoted, so blanks are 0; agent code and claim amount were always blank / 0.</summary>
    private static BordxPolicyRow Ep(Reader r, int line) =>
        new(line, r.T(1), r.T(2), r.First(6, 25), null, r.Date(7, "Effective Date", true), r.Date(8, "Expiry Date", true),
            r.T(9), r.T(10), r.T(11), r.T(12), r.T(13), r.T(14), r.T(15), r.T(16), r.T(17), r.T(18), r.T(19), "", r.T(20), r.T(21), r.T(22), r.T(23),
            0, r.Count(24, "Total FW", true), r.Money(25, "Total Premium", true), r.Money(26, "MCO Fee", true), "",
            r.Money(27, "Premium Tax", true), r.Money(28, "TPCA Tax", true), r.Money(29, "Discount", true), null, r.Date(32, "Record Update Date", true),
            r.T(3), r.Date(4, "Endorsement Date", true), r.Date(5, "Endorsement Effective Date", true), r.T(30), r.Date(31, "Submission Date", true));

    /// <summary>SaveFeeCanPolCtrl: cancellation fee row.</summary>
    private static BordxPolicyRow Ex(Reader r, int line) =>
        new(line, r.T(1), r.T(2), r.First(6, 25), null, r.Date(7, "Effective Date", true), null,
            "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
            0, 0, r.Money(8, "Total Premium"), r.Money(9, "MCO Fee"), "", r.Money(10, "Premium Tax"), r.Money(11, "TPCA Tax"), 0, null, r.Date(14, "Record Update Date", true),
            r.T(3), r.Date(4, "Endorsement Date", true), r.Date(5, "Endorsement Effective Date", true), r.T(12), r.Date(13, "Submission Date", true));

    /// <summary>SaveMBMPolicyDetails: field 15 "N" is a new member (else renewal), field 16 "N" active (else cancelled).</summary>
    private static BordxWorkerRow Mw(Reader r, int line, string file, List<BordxIssue> issues)
    {
        Check(issues, line, r.T(4), r.T(9), file, (r.T(0), "TPCA Code Is Blank"), (r.T(1), "Insurer Code Is Blank"), (r.T(2), "Insurer Number Is Blank"),
            (r.T(3), "Insurer Foreign Worker Number Is Blank"), (r.T(4), "Policy Number Is Blank"), (r.T(7), "Passport No Is Blank"), (r.T(9), "Foreign Worker's Name Is Blank"));
        return new BordxWorkerRow(line, r.First(1, 2), r.T(2), r.T(3), r.First(4, 25), r.Date(5, "Effective Date", true), r.T(6), r.T(7), r.T(8), r.T(9), r.T(10),
            r.Date(11, "Foreign Worker's DOB", true), r.Money(12, "Max Claim Limit"), r.Money(13, "Premium"), r.Money(14, "MCO Fee"),
            r.T(15) == "N" ? "N" : "R", r.T(16) == "N" ? "A" : "C", r.T(17), r.T(18), r.Date(19, "Work Permit Expiry", false),
            r.Date(20, "Record Created Date", true), r.Date(21, "Record Update Date", false), null, null, null, null, null);
    }

    /// <summary>SaveFeeCanPolDetails: worker endorsement. An invalid effective date went in as '' (1900-01-01).</summary>
    private static BordxWorkerRow Ew(Reader r, int line)
    {
        var eff = DateTime.TryParseExact(r.T(7).Length >= 8 ? r.T(7)[..8] : "", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : BlankDate;
        return new BordxWorkerRow(line, r.T(1), r.T(2), r.T(3), r.First(6, 25), eff, r.T(9), r.T(10).Replace("'", ""), r.T(11), r.T(12), r.T(13),
            r.Date(14, "Foreign Worker's DOB", true), 0, r.Money(16, "Premium"), r.Money(17, "MCO Fee"), "", "", r.T(19), r.T(20), r.Date(21, "Work Permit Expiry", false),
            null, r.Date(24, "Record Update Date", true), r.T(4), r.Date(5, "Endorsement Date", true), r.T(8), r.T(22), r.Date(23, "Batch Date", true));
    }
}

public static class BordxUploadRules
{
    /// <summary>File-level checks: at least one file, a known kind from the name, one file per kind, a name that fits CoSPPAFilename (50).</summary>
    public static List<BordxIssue> CheckFiles(BordxUploadRequest request)
    {
        var issues = new List<BordxIssue>();
        var files = request.Files ?? [];
        if (files.Count == 0) { issues.Add(new BordxIssue(0, null, null, "Please Insert File", "", true)); return issues; }
        var seen = new HashSet<string>();
        foreach (var f in files)
        {
            var name = f.Name?.Trim() ?? "";
            var kind = BordxKind.Of(name);
            if (kind is null) issues.Add(new BordxIssue(0, null, null, $"Wrong File Name-{name}", name, true));
            else if (!seen.Add(kind)) issues.Add(new BordxIssue(0, null, null, $"Only one {kind} file can be uploaded at a time", name, true));
            if (name.Length > 50) issues.Add(new BordxIssue(0, null, null, "The file name is longer than 50 characters", name, true));
            if (string.IsNullOrWhiteSpace(f.Content)) issues.Add(new BordxIssue(0, null, null, "The file is empty", name, true));
        }
        return issues;
    }
}

public interface IBordxUploadRepository
{
    /// <summary>Verify: reads every file, lists the legacy messages and anything that would fail the upload, checks the file names were not uploaded before,
    /// and counts what would be inserted or updated. Nothing is written.</summary>
    Task<BordxUploadPreview> PreviewAsync(BordxUploadRequest request, CancellationToken ct = default);

    /// <summary>Upload: the same checks, then MP, MW, NP, EP, EX, EW through the legacy procedures in one transaction per database (main and maintenance).</summary>
    Task<BordxUploadResult> UploadAsync(BordxUploadRequest request, string user, CancellationToken ct = default);
}
