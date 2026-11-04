namespace GenesisFIS.Core.Payments;

/// <summary>
/// Payment > To Hospital (legacy FrmPaymentHospital, USRAccess 55): lists the claims payable to a hospital with their payment vouchers (Payment2HOS) and
/// corrects a voucher. In FWHS every voucher (CF...) is written by Receipt > Payor when the payor's receipt pays the claim; the legacy form's own Save
/// called GeneratePaymentPVNo with arguments it does not take and could not run, so creating vouchers here is not migrated.
/// </summary>
public sealed record HospitalPaymentSearch(
    string? Hospital = null, string? Payor = null, string? HealthType = null, string? WorksheetStatus = null, string? Status = null, string? ClaimNoFrom = null,
    string? ClaimNoTo = null, string? PvNoFrom = null, string? PvNoTo = null, bool WithoutPv = false, DateOnly? PvDateFrom = null, DateOnly? PvDateTo = null,
    string? SunNoFrom = null, string? SunNoTo = null, DateOnly? SunDateFrom = null, DateOnly? SunDateTo = null, string? InvoiceNoFrom = null, string? InvoiceNoTo = null,
    DateOnly? InvoiceDateFrom = null, DateOnly? InvoiceDateTo = null, DateOnly? ClaimDateFrom = null, DateOnly? ClaimDateTo = null, DateOnly? BordxDateFrom = null,
    DateOnly? BordxDateTo = null, string? MrnFrom = null, string? MrnTo = null);

/// <summary>Payment status filter (legacy CboPayment).</summary>
public static class HospitalPaymentStatus
{
    public const string All = "all";
    public const string Paid = "paid";
    public const string Unpaid = "unpaid";
    public const string Partial = "partial";
    public const string PaidOrPartial = "paid-partial";
    public const string UnpaidOrPartial = "unpaid-partial";

    /// <summary>Whether a line with this Payment2HOS status (null when the claim has no voucher) passes the filter.</summary>
    public static bool Matches(string? filter, string? status) => filter?.Trim().ToLowerInvariant() switch
    {
        Paid => status == "F",
        Unpaid => string.IsNullOrEmpty(status),
        Partial => status == "P",
        PaidOrPartial => status is "F" or "P",
        UnpaidOrPartial => string.IsNullOrEmpty(status) || status == "P",
        _ => true,
    };
}

/// <summary>
/// One listing row: a claim payable to a hospital and one of its vouchers (none yet when <see cref="PvNo"/> is null). The claim's invoices are listed
/// together (the legacy view repeated the voucher once per invoice). <see cref="Outstanding"/> is the payable amount less all its vouchers' payments.
/// </summary>
public sealed record HospitalPaymentLine(
    string CaseNumber, int Version, string? Status, string? Hospital, string? InvoiceNos, DateOnly? InvoiceDate, decimal Payable, decimal Paid, decimal TotalPaid, decimal Outstanding,
    decimal PayableToMember, string? BordxNo, string? MemberNumber, string? MemberName, DateOnly? Admitted, DateOnly? Discharged, string? PvNo, DateOnly? PvDate, string? PvStatus,
    string? ChequeNo, DateOnly? ChequeDate, decimal? ChequeAmount, int? AccountMonth, int? AccountYear, string? Remark, string? ReceiptNo, decimal? ReceiptAmount, decimal? BankCharge,
    decimal? GainLoss, decimal? Discount, decimal? Misc, string? ContraCase, string? ContraVersion, decimal? ContraAmount, string? CaseRemark, decimal? Excess, decimal? MedixPayment,
    string? ApprovalStatus, string? SunNo, DateOnly? SunDate, string? WorksheetStatus, DateOnly? ClaimDate, DateOnly? BordxDate, string? OriginalInvoiceStatus,
    DateOnly? OriginalInvoiceDate, string? Payee, string? Wording, string? HospitalPayee, string? Mrn, string? ClaimId, string? Bank);

public sealed record HospitalPaymentListing(IReadOnlyList<HospitalPaymentLine> Lines, bool Truncated);

public sealed record HospitalPaymentOption(string Code, string Name);

public sealed record HospitalPaymentOptions(IReadOnlyList<HospitalPaymentOption> HealthTypes, IReadOnlyList<HospitalPaymentOption> Payors, IReadOnlyList<HospitalPaymentOption> Hospitals);

/// <summary>The voucher header (legacy "PV" frame); it is the same on every claim of the voucher. A null <see cref="PvStatus"/> keeps the current one.</summary>
public sealed record HospitalPaymentHeader(
    DateOnly? PvDate, string? PvStatus, string? ChequeNo, DateOnly? ChequeDate, decimal? ChequeAmount, string? Payee, string? Remark, int? AccountMonth = null, int? AccountYear = null);

/// <summary>One claim's details on the voucher (legacy "Case" frame).</summary>
public sealed record HospitalPaymentClaim(
    string CaseNumber, int Version, decimal Paid, decimal? BankCharge = null, decimal? GainLoss = null, decimal? Discount = null, decimal? Misc = null, decimal? Excess = null,
    decimal? MedixPayment = null, string? ContraCase = null, string? ContraVersion = null, decimal? ContraAmount = null, string? CaseRemark = null);

public sealed record HospitalPaymentUpdateRequest(HospitalPaymentHeader Header, HospitalPaymentClaim Claim);

public sealed record HospitalPaymentResult(string PvNo, IReadOnlyList<HospitalPaymentLine> Lines);

public static class HospitalPaymentRules
{
    public const int MaxLines = 5000;

    /// <summary>The legacy cmdUpdate_Click checks, in its order (the amount in words is worked out from the cheque amount, as legacy did on update).</summary>
    public static (string Field, string Message)? Validate(HospitalPaymentUpdateRequest r)
    {
        var h = r.Header;
        if (string.IsNullOrWhiteSpace(h.Payee)) return ("payee", "Please Enter Payee!");
        if (h.PvDate is null) return ("pvDate", "Please Enter PV Date! ");
        if (string.IsNullOrWhiteSpace(h.ChequeNo)) return ("chequeNo", "Please Enter Cheque No!");
        if (h.ChequeDate is null) return ("chequeDate", "Please Enter Cheque Date! ");
        if (h.ChequeAmount is null) return ("chequeAmount", "Please Enter Cheque Amt!");
        if (h.ChequeAmount < 0 || h.ChequeAmount >= 1_000_000_000_000m) return ("chequeAmount", "Invalid Cheque Amt!");
        if (h.PvStatus is { } s && s.Trim() is not ("A" or "C" or "F" or "")) return ("pvStatus", "PV status must be A (Active) or C (Cancelled).");
        if (h.AccountMonth is { } m && m is < 1 or > 12) return ("accountMonth", "Invalid account period month.");
        var c = r.Claim;
        if (string.IsNullOrWhiteSpace(c.ContraCase) != string.IsNullOrWhiteSpace(c.ContraVersion))
            return ("contraCase", "Invalid Contra Case..., Please Keyin Contra Case No. or Contra Version ");
        if (c.Paid < 0) return ("paid", "Invalid Amount Paid ! ");
        return null;
    }

    private static readonly string[] Ones = ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen",
        "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
    private static readonly string[] Places = ["", "Thousand", "Million", "Billion"];

    private static string Below100(int n) => n < 20 ? Ones[n] : string.Join(' ', new[] { Tens[n / 10], Ones[n % 10] }.Where(x => x.Length > 0));

    /// <summary>Legacy ConvertHundreds: "One Hundred And Twenty Five", "One Hundred Five" (no "And" when the tens digit is 0).</summary>
    private static string Below1000(int n)
    {
        var hundreds = n / 100; var rest = n % 100;
        var head = hundreds > 0 ? $"{Ones[hundreds]} Hundred" : "";
        if (rest == 0) return head;
        if (head.Length == 0) return Below100(rest);
        return rest >= 10 ? $"{head} And {Below100(rest)}" : $"{head} {Ones[rest]}";
    }

    /// <summary>
    /// The cheque amount in words as the legacy PV printed it (ConvertCurrencyToEnglish): "One Thousand Two Hundred And Five And Cents Fifty Only", split
    /// into a second line (the cents) when longer than 55 characters.
    /// </summary>
    public static (string Line1, string Line2) Wording(decimal amount)
    {
        amount = Math.Round(Math.Abs(amount), 2);
        var whole = (long)Math.Floor(amount);
        var cents = (int)((amount - whole) * 100);
        var groups = new List<string>();
        for (var place = 0; whole > 0; place++, whole /= 1000)
            if (whole % 1000 is var g && g > 0) groups.Insert(0, Below1000((int)g) + (Places[place].Length > 0 ? " " + Places[place] : ""));
        var dollars = string.Join(' ', groups);
        if (dollars == "One") dollars = "One Dollar";
        var centText = cents == 0 ? "" : cents == 1 ? "And Cent One" : $"And Cents {Below100(cents)}";
        if (dollars.Length == 0 && centText.StartsWith("And ", StringComparison.Ordinal)) centText = centText[4..];
        var full = string.Join(' ', new[] { dollars, centText, "Only" }.Where(x => x.Length > 0));
        if (full.Length <= 55 || centText.Length == 0 || dollars.Length == 0) return (full, "");
        return (dollars, centText + " Only");
    }
}

public interface IHospitalPaymentRepository
{
    Task<HospitalPaymentOptions> OptionsAsync(CancellationToken ct = default);
    Task<HospitalPaymentListing> SearchAsync(HospitalPaymentSearch search, CancellationToken ct = default);
    Task<HospitalPaymentResult> UpdateAsync(string pvNo, HospitalPaymentUpdateRequest request, string user, CancellationToken ct = default);
}
