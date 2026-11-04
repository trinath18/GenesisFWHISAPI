using System.Globalization;
using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Posting;

/// <summary>
/// A/C Export (legacy frmPosting* forms): items are listed as SUN journal lines (journal type, transaction reference, account code, amount, D/C) and
/// "Post" records them in SUNPosting (worksheet database) under a new batch number from SunPostBatch, so they are not exported twice. The legacy
/// forms read the HIS databases through views; here the FWHS tables are read directly.
/// </summary>
public static class PostingType
{
    /// <summary>A/C Export > Invoice > Hospital (frmPostingInvoiceHosp, USRAccess 169).</summary>
    public const string InvoiceHospital = "InvHosp";

    /// <summary>A/C Export > Invoice > Member (frmPostingInvoiceMBM, USRAccess 170).</summary>
    public const string InvoiceMember = "InvMBM";

    /// <summary>A/C Export > Payment > Hospital (frmPostingPay2Hosp, USRAccess 173).</summary>
    public const string PaymentHospital = "PayHosp";

    /// <summary>A/C Export > Receipt > Payor (frmPostingRecPayor, USRAccess 172).</summary>
    public const string ReceiptPayor = "RecPayor";

    /// <summary>A/C Export > Bordx > Payor (frmPostingBordxInv2Pay, USRAccess 176).</summary>
    public const string BordxPayor = "Bordx2PaySum";
}

/// <summary>When posting is open (legacy CheckTime on TimeMain "POSTING"): after the start time and after the end time, unless the user has special access.</summary>
public sealed record PostingWindow(bool Open, TimeOnly? Start, TimeOnly? End, bool SpecialAccess, string? Message)
{
    public static PostingWindow For(TimeOnly now, TimeOnly? start, TimeOnly? end, bool specialAccess)
    {
        if (specialAccess) return new(true, start, end, true, null);
        // Legacy mdiMain refuses the menu until the start time; the form's timer closes it while the time is not past the end time.
        if (start is { } s && now <= s) return new(false, start, end, false, $"Posting is available at {s:hh\\:mm tt}!");
        if (end is { } e && now <= e) return new(false, start, end, false, $"Posting is ended at {e:hh\\:mm tt}!");
        return new(true, start, end, false, null);
    }
}

public sealed record PostingOption(string Code, string Name);

public sealed record PostingOptions(IReadOnlyList<PostingOption> HealthTypes, IReadOnlyList<PostingOption> Payors, IReadOnlyList<string> AccountPeriods, PostingWindow Window);

/// <summary>One line of the SUN journal, in the legacy grid's columns (Chk, J/T, J/S, DB, Trans.Ref., AccPeriod, Trans.Date, Description, AccCode, Amt, D/C, Hospital, Grp Company, Payor, Case No, Product Cat., Bordx Ref, BatchNo).</summary>
public sealed record JournalLine(
    string JournalType, string JournalSource, string Database, string TransRef, string? AccountPeriod, DateOnly? TransDate, string Description, string AccountCode,
    decimal Amount, string DebitCredit, string? Hospital, string? GroupCompany, string? Payor, string? CaseNo, string? ProductCategory, string? BordxRef, string? BatchNo);

/// <summary>An item that is posted as a whole (its debit and credit lines). <see cref="Problem"/> says why it cannot be posted.</summary>
public sealed record PostingItem(string Key, bool Posted, IReadOnlyList<JournalLine> Lines, IReadOnlyList<string> References, string? Problem);

public sealed record PostingJournal(IReadOnlyList<PostingItem> Items, decimal Debit, decimal Credit, bool Truncated);

/// <summary>
/// The search of A/C Export > Invoice > Hospital (legacy SearchRecord). Bordxed defaults to Yes and Posted to No, as on the legacy form. Post date and
/// batch number only apply to posted items.
/// </summary>
public sealed record HospitalInvoicePostingSearch(
    string? HealthType = null, string? Payor = null, string? Hospital = null, DateOnly? BordxFrom = null, DateOnly? BordxTo = null,
    DateOnly? InvoiceDateFrom = null, DateOnly? InvoiceDateTo = null, string? InvoiceNoFrom = null, string? InvoiceNoTo = null,
    bool Bordxed = true, bool Posted = false, DateOnly? PostDateFrom = null, DateOnly? PostDateTo = null, string? BatchFrom = null, string? BatchTo = null);

/// <summary>
/// The search of A/C Export > Invoice > Member (legacy SearchRecords). The claim date is the claim's close date (the legacy form labels it "Claim Reg
/// Date"), the claim number range is on the case number. Negative is the legacy Post Type "N": claims paying the member less than nothing (MJD journal).
/// </summary>
public sealed record MemberInvoicePostingSearch(
    string? HealthType = null, string? Payor = null, string? ClaimNoFrom = null, string? ClaimNoTo = null, DateOnly? ClaimDateFrom = null, DateOnly? ClaimDateTo = null,
    DateOnly? BordxFrom = null, DateOnly? BordxTo = null, bool Bordxed = true, bool Posted = false, DateOnly? PostDateFrom = null, DateOnly? PostDateTo = null,
    string? BatchFrom = null, string? BatchTo = null, bool Negative = false);

/// <summary>The search of A/C Export > Payment > Hospital (legacy SearchRecord): payment vouchers to hospitals (Payment2HOS) by date and number.</summary>
public sealed record HospitalPaymentPostingSearch(
    string? HealthType = null, string? Payor = null, DateOnly? VoucherDateFrom = null, DateOnly? VoucherDateTo = null, string? VoucherNoFrom = null, string? VoucherNoTo = null,
    bool Posted = false, DateOnly? PostDateFrom = null, DateOnly? PostDateTo = null, string? BatchFrom = null, string? BatchTo = null);

public sealed record HospitalPaymentPostRequest(HospitalPaymentPostingSearch? Search, IReadOnlyList<string>? Keys, string? AccountPeriod);

/// <summary>The search of A/C Export > Receipt > Payor (legacy SearchRecord): receipts from payors (ReceiptfrPAY) by payor, number and date.</summary>
public sealed record PayorReceiptPostingSearch(
    string? Payor = null, DateOnly? ReceiptDateFrom = null, DateOnly? ReceiptDateTo = null, string? ReceiptNoFrom = null, string? ReceiptNoTo = null,
    bool Posted = false, DateOnly? PostDateFrom = null, DateOnly? PostDateTo = null, string? BatchFrom = null, string? BatchTo = null);

public sealed record PayorReceiptPostRequest(PayorReceiptPostingSearch? Search, IReadOnlyList<string>? Keys, string? AccountPeriod);

/// <summary>The search of A/C Export > Bordx > Payor (legacy SearchRecord): bordereaux of non-Sihat (health type N) claims by payor, number and date.</summary>
public sealed record BordxPayorPostingSearch(
    string? Payor = null, string? BordxNoFrom = null, string? BordxNoTo = null, DateOnly? BordxDateFrom = null, DateOnly? BordxDateTo = null,
    bool Posted = false, DateOnly? PostDateFrom = null, DateOnly? PostDateTo = null, string? BatchFrom = null, string? BatchTo = null);

public sealed record BordxPayorPostRequest(BordxPayorPostingSearch? Search, IReadOnlyList<string>? Keys, string? AccountPeriod);

public sealed record MemberInvoicePostRequest(MemberInvoicePostingSearch? Search, IReadOnlyList<string>? Keys, string? AccountPeriod);

/// <summary>Post the chosen items (their keys) of the search into the accounting period ("MM/YYYY").</summary>
public sealed record HospitalInvoicePostRequest(HospitalInvoicePostingSearch? Search, IReadOnlyList<string>? Keys, string? AccountPeriod);

public sealed record PostingResult(string BatchNo, int Items, int Records, PostingJournal Journal);

public static partial class PostingRules
{
    /// <summary>The most items one search returns; the legacy form listed everything, which for FWHS can be well over 100,000 invoices.</summary>
    public const int MaxItems = 5000;

    [GeneratedRegex(@"^(0[1-9]|1[0-2])/(\d{4})$")]
    private static partial Regex Period();

    public static (string Month, string Year)? ParsePeriod(string? s) =>
        s is not null && Period().Match(s.Trim()) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    /// <summary>Legacy: SUN case reference is characters 3-7 of the case number, the bordereau reference its last five characters.</summary>
    public static string? CaseRef(string? caseNo) => caseNo?.Trim() is { Length: > 2 } c ? c.Substring(2, Math.Min(5, c.Length - 2)) : null;

    public static string? Last(string? s, int n) => s?.Trim() is { Length: > 0 } t ? t[Math.Max(0, t.Length - n)..] : null;

    public static string Description(string? name) => (name ?? "").Trim() is { Length: > 25 } n ? n[..25].TrimEnd() : (name ?? "").Trim();

    public static string Period(string? month, string? year) =>
        string.IsNullOrWhiteSpace(month) && string.IsNullOrWhiteSpace(year) ? "" : $"{month?.Trim()}/{year?.Trim()}";

    /// <summary>The search must be narrowed by at least one range, so a search cannot read every invoice.</summary>
    public static string? Validate(HospitalInvoicePostingSearch s)
    {
        if (s.InvoiceDateTo is null && s.InvoiceDateFrom is null && s.BordxFrom is null && s.BordxTo is null && Blank(s.InvoiceNoFrom) && Blank(s.InvoiceNoTo)
            && Blank(s.BatchFrom) && Blank(s.BatchTo) && s.PostDateFrom is null && s.PostDateTo is null)
            return "Please enter an invoice date, bordx date, invoice number, post date or batch number range.";
        foreach (var v in new[] { s.HealthType, s.Payor, s.Hospital, s.InvoiceNoFrom, s.InvoiceNoTo, s.BatchFrom, s.BatchTo })
            if (v is { Length: > 40 }) return "A search value is too long.";
        return null;
    }

    public static string? Validate(MemberInvoicePostingSearch s)
    {
        if (s.ClaimDateTo is null && s.ClaimDateFrom is null && s.BordxFrom is null && s.BordxTo is null && Blank(s.ClaimNoFrom) && Blank(s.ClaimNoTo)
            && Blank(s.BatchFrom) && Blank(s.BatchTo) && s.PostDateFrom is null && s.PostDateTo is null)
            return "Please enter a claim date, bordx date, claim number, post date or batch number range.";
        foreach (var v in new[] { s.HealthType, s.Payor, s.ClaimNoFrom, s.ClaimNoTo, s.BatchFrom, s.BatchTo })
            if (v is { Length: > 40 }) return "A search value is too long.";
        return null;
    }

    public static string? Validate(HospitalPaymentPostingSearch s)
    {
        if (s.VoucherDateFrom is null && s.VoucherDateTo is null && Blank(s.VoucherNoFrom) && Blank(s.VoucherNoTo) && Blank(s.BatchFrom) && Blank(s.BatchTo)
            && s.PostDateFrom is null && s.PostDateTo is null)
            return "Please enter a PV date, PV number, export date or batch number range.";
        foreach (var v in new[] { s.HealthType, s.Payor, s.VoucherNoFrom, s.VoucherNoTo, s.BatchFrom, s.BatchTo })
            if (v is { Length: > 40 }) return "A search value is too long.";
        return null;
    }

    public static string? Validate(BordxPayorPostingSearch s)
    {
        if (s.BordxDateFrom is null && s.BordxDateTo is null && Blank(s.BordxNoFrom) && Blank(s.BordxNoTo) && Blank(s.BatchFrom) && Blank(s.BatchTo)
            && s.PostDateFrom is null && s.PostDateTo is null)
            return "Please enter a bordx date, bordx number, post date or batch number range.";
        foreach (var v in new[] { s.Payor, s.BordxNoFrom, s.BordxNoTo, s.BatchFrom, s.BatchTo })
            if (v is { Length: > 40 }) return "A search value is too long.";
        return null;
    }

    public static string? Validate(PayorReceiptPostingSearch s)
    {
        if (s.ReceiptDateFrom is null && s.ReceiptDateTo is null && Blank(s.ReceiptNoFrom) && Blank(s.ReceiptNoTo) && Blank(s.BatchFrom) && Blank(s.BatchTo)
            && s.PostDateFrom is null && s.PostDateTo is null)
            return "Please enter a receipt date, receipt number, post date or batch number range.";
        foreach (var v in new[] { s.Payor, s.ReceiptNoFrom, s.ReceiptNoTo, s.BatchFrom, s.BatchTo })
            if (v is { Length: > 40 }) return "A search value is too long.";
        return null;
    }

    /// <summary>
    /// Legacy PayListing of the payor receipt form: the bank account debited for a receipt, by its first two letters (RA 7400, RB 7410, RC 7420). FWHS
    /// receipts start with RF, which the legacy form had no account for.
    /// </summary>
    public static string? ReceiptBankAccount(string? receiptNo) => receiptNo?.Trim() is { Length: >= 2 } v ? v[..2].ToUpperInvariant() switch
    {
        "RA" => "7400", "RB" => "7410", "RC" => "7420", _ => null,
    } : null;

    /// <summary>
    /// Legacy PayHospListing: the bank account credited for a hospital payment voucher, by its first two letters (CA 7400, CB 7410, CC 7420). FWHS
    /// vouchers start with CF, which the legacy form had no account for.
    /// </summary>
    public static string? BankAccount(string? voucherNo) => voucherNo?.Trim() is { Length: >= 2 } v ? v[..2].ToUpperInvariant() switch
    {
        "CA" => "7400", "CB" => "7410", "CC" => "7420", _ => null,
    } : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    public static string Money(decimal d) => d.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

public interface IAccountPostingRepository
{
    /// <summary>Health types, payors, accounting periods (AccPeriod) and whether posting is open for the user now.</summary>
    Task<PostingOptions> OptionsAsync(string user, CancellationToken ct = default);

    Task<PostingJournal> SearchHospitalInvoicesAsync(HospitalInvoicePostingSearch search, CancellationToken ct = default);

    /// <summary>Legacy CmdPost: a new batch number and one SUNPosting row per invoice of each chosen item, in one transaction.</summary>
    Task<PostingResult> PostHospitalInvoicesAsync(HospitalInvoicePostRequest request, string user, CancellationToken ct = default);

    Task<PostingJournal> SearchMemberInvoicesAsync(MemberInvoicePostingSearch search, CancellationToken ct = default);

    /// <summary>Legacy CmdPost of the member form: a new batch number and one SUNPosting row per chosen claim (case number and version).</summary>
    Task<PostingResult> PostMemberInvoicesAsync(MemberInvoicePostRequest request, string user, CancellationToken ct = default);

    Task<PostingJournal> SearchHospitalPaymentsAsync(HospitalPaymentPostingSearch search, CancellationToken ct = default);

    /// <summary>Legacy CmdPost of the hospital payment form: a new batch number and one SUNPosting row per chosen voucher (TransRef = PV number).</summary>
    Task<PostingResult> PostHospitalPaymentsAsync(HospitalPaymentPostRequest request, string user, CancellationToken ct = default);

    Task<PostingJournal> SearchPayorReceiptsAsync(PayorReceiptPostingSearch search, CancellationToken ct = default);

    /// <summary>Legacy CmdExp of the payor receipt form: a new batch number and one SUNPosting row per chosen receipt (TransRef = receipt number).</summary>
    Task<PostingResult> PostPayorReceiptsAsync(PayorReceiptPostRequest request, string user, CancellationToken ct = default);

    Task<PostingJournal> SearchBordxPayorAsync(BordxPayorPostingSearch search, CancellationToken ct = default);

    /// <summary>Legacy CmdPost of the bordereau-to-payor form: a new batch number and one SUNPosting row per chosen bordereau (TransRef = bordereau number).</summary>
    Task<PostingResult> PostBordxPayorAsync(BordxPayorPostRequest request, string user, CancellationToken ct = default);
}
