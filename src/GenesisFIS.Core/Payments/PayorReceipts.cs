namespace GenesisFIS.Core.Payments;

/// <summary>
/// Receipt > Payor (legacy FrmReceiptPayor, USRAccess 61): a payor's cheque is receipted against the claims of its bordereaux. Saving writes one
/// ReceiptfrPAY row per ticked claim under a new receipt number (RecSeq) and payment voucher number (PVSeq), sets the claim's PVRecPayorStatus, and
/// for a fully paid claim records the hospital payment (Payment2HOS). Only the legacy worksheet mode is migrated; the bordereau mode was hidden.
/// </summary>
public sealed record PayorReceiptSearch(
    string? Payor, string? ClaimNoFrom, string? ClaimNoTo, string? ClaimId, string? Status, string? ReceiptNoFrom, string? ReceiptNoTo,
    DateOnly? ReceiptDateFrom, DateOnly? ReceiptDateTo, DateOnly? ClaimDateFrom, DateOnly? ClaimDateTo, DateOnly? BordxDateFrom, DateOnly? BordxDateTo,
    string? BordxNoFrom, string? BordxNoTo);

/// <summary>Payment status filter (legacy CboBordxStatus): all, paid (has a receipt), unpaid (no receipt) or partially paid.</summary>
public static class PayorReceiptStatus
{
    public const string All = "all";
    public const string Paid = "paid";
    public const string Unpaid = "unpaid";
    public const string Partial = "partial";
}

/// <summary>
/// One listing row: a claim and one of its receipts (no receipt yet when <see cref="ReceiptNo"/> is null). <see cref="Received"/> is this receipt's
/// amount for the claim, <see cref="TotalReceived"/> what all the claim's receipts paid, <see cref="Outstanding"/> the claim amount less that.
/// </summary>
public sealed record PayorReceiptLine(
    string CaseNumber, int Version, string? BordxNo, string? Payor, string? ClaimId, decimal Amount, decimal Received, decimal TotalReceived, decimal Outstanding,
    string? Status, string? ReceiptNo, DateOnly? ReceiptDate, decimal? ReceiptAmount, int? AccountMonth, int? AccountYear, string? ReceiptRemark, string? PvNo,
    DateOnly? PvDate, string? ChequeNo, string? ChequeNoMr, DateOnly? PayChequeDate, string? Bank, decimal? BankCharge, string? ContraCase, string? ContraVersion,
    decimal? ContraAmount, decimal? GainLoss, decimal? Misc, decimal? Discount, decimal? WriteOff, decimal? MedixReceipt, string? CaseRemark, DateOnly? ClaimDate,
    DateOnly? BordxDate, DateOnly? ChequeSentDate, string? CourierNo);

public sealed record PayorReceiptListing(IReadOnlyList<PayorReceiptLine> Lines, bool Truncated);

public sealed record PayorReceiptOption(string Code, string Name);

/// <summary>The form's lists; <see cref="Authorised"/> (USR.AuthorisedStatus 'Y') lets the user receipt a claim that is already fully paid again.</summary>
public sealed record PayorReceiptOptions(IReadOnlyList<PayorReceiptOption> Payors, IReadOnlyList<string> Banks, bool Authorised);

/// <summary>The receipt header (legacy "Receipt / PV" frame). The account period defaults to the receipt date's month and year.</summary>
public sealed record PayorReceiptHeader(
    DateOnly? ReceiptDate, decimal? Amount, string? ChequeNo, string? ChequeNoMr, DateOnly? PayChequeDate, DateOnly? PvDate, string? Bank, string? Remark,
    int? AccountMonth = null, int? AccountYear = null);

/// <summary>A ticked claim and what this receipt pays on it (legacy "Update Case" frame).</summary>
public sealed record PayorReceiptClaim(
    string CaseNumber, int Version, decimal Paying, decimal? BankCharge = null, string? ContraCase = null, string? ContraVersion = null, decimal? ContraAmount = null,
    decimal? GainLoss = null, decimal? Misc = null, decimal? Discount = null, decimal? WriteOff = null, decimal? MedixReceipt = null, string? CaseRemark = null);

public sealed record PayorReceiptSaveRequest(PayorReceiptHeader Header, IReadOnlyList<PayorReceiptClaim> Claims);

/// <summary>Legacy Edit / Update: changes the receipt header on all its rows and the details of one of its claims.</summary>
public sealed record PayorReceiptUpdateRequest(PayorReceiptHeader Header, PayorReceiptClaim Claim);

public sealed record PayorReceiptResult(string ReceiptNo, string? PvNo, IReadOnlyList<PayorReceiptLine> Lines);

/// <summary>
/// Receipt > Payor (Manual) (legacy FrmReceiptPayorManual, USRAccess 61): the receipt number (and optionally the voucher number) is typed, and either
/// ticked claims (worksheet mode) or whole bordereaux (bordereau mode, each claim paid what it still owes) are receipted. Unlike the normal form it
/// writes no hospital payment and no case remark, and it records the bordereau's receipt status (Bordx.BordxPAYFrStatus).
/// </summary>
public sealed record ManualPayorReceiptRequest(
    string? ReceiptNo, string? PvNo, PayorReceiptHeader Header, IReadOnlyList<PayorReceiptClaim>? Claims = null, IReadOnlyList<string>? Bordereaux = null);

/// <summary>
/// A bordereau in the manual form's bordereau mode: its claims' amounts and receipts. <see cref="Status"/> is F when every claim is fully received,
/// P when some are received, null when none.
/// </summary>
public sealed record PayorReceiptBordx(
    string BordxNo, string? Payor, DateOnly? BordxDate, int Claims, decimal Amount, decimal Received, decimal Outstanding, string? Status, string? ReceiptNos,
    DateOnly? ReceiptDate, string? ChequeNo, string? PvNo);

public sealed record PayorReceiptBordxListing(IReadOnlyList<PayorReceiptBordx> Bordereaux, bool Truncated);

public static class PayorReceiptRules
{
    public const int MaxLines = 5000;

    /// <summary>The claim status after a receipt: F when what it now pays and what was received before cover the claim amount, else P.</summary>
    public static string Status(decimal amount, decimal receivedBefore, decimal paying) => paying + receivedBefore >= amount ? "F" : "P";

    /// <summary>The legacy cmdSave_Click checks, in its order.</summary>
    public static (string Field, string Message)? Validate(PayorReceiptSaveRequest r)
    {
        if (r.Claims is not { Count: > 0 }) return ("claims", "Please select at least one record to be updated! ");
        if (Header(r.Header) is { } h) return h;
        var total = r.Claims.Sum(c => c.Paying);
        if (r.Header.Amount != total && string.IsNullOrWhiteSpace(r.Header.Remark)) return ("remark", "Please Enter Remark!");
        if (string.IsNullOrWhiteSpace(r.Header.Bank)) return ("bank", "Please Enter Bank Name ");
        if (r.Claims.GroupBy(c => (c.CaseNumber.Trim().ToUpperInvariant(), c.Version)).Any(g => g.Count() > 1)) return ("claims", "A claim is ticked twice.");
        foreach (var c in r.Claims)
            if (Claim(c) is { } p) return p;
        return null;
    }

    /// <summary>The legacy FrmReceiptPayorManual cmdSave_Click checks (the remark check runs once the bordereaux are expanded to their claims).</summary>
    public static (string Field, string Message)? Validate(ManualPayorReceiptRequest r)
    {
        var claims = r.Claims?.Count ?? 0; var bordx = r.Bordereaux?.Count ?? 0;
        if (claims + bordx == 0) return ("claims", "Please select at least one record to be updated! ");
        if (claims > 0 && bordx > 0) return ("claims", "Receipt either claims or bordereaux, not both.");
        if (string.IsNullOrWhiteSpace(r.ReceiptNo)) return ("receiptNo", "Please Enter Receipt No");
        if (r.ReceiptNo.Trim().Length > 15) return ("receiptNo", "Receipt No is at most 15 characters.");
        if (r.PvNo?.Trim().Length > 20) return ("pvNo", "PV No is at most 20 characters.");
        if (Header(r.Header) is { } h) return h;
        if (r.Claims is { Count: > 0 } list)
        {
            if (list.GroupBy(c => (c.CaseNumber.Trim().ToUpperInvariant(), c.Version)).Any(g => g.Count() > 1)) return ("claims", "A claim is ticked twice.");
            foreach (var c in list)
                if (Claim(c) is { } p) return p;
        }
        return null;
    }

    /// <summary>The legacy save's remark check: a remark is needed when the receipt amount is not what the claims are paid.</summary>
    public static (string Field, string Message)? Remark(PayorReceiptHeader h, decimal paying) =>
        h.Amount != paying && string.IsNullOrWhiteSpace(h.Remark) ? ("remark", "Please Enter Remark!") : null;

    /// <summary>A bordereau's receipt status from its claims' statuses (legacy ProcessWS: F when every claim is F, else P).</summary>
    public static string? BordxStatus(IReadOnlyCollection<string?> claimStatuses) =>
        claimStatuses.Count > 0 && claimStatuses.All(s => s == "F") ? "F" : claimStatuses.Any(s => s is "F" or "P") ? "P" : null;

    /// <summary>The legacy CmdUpdate_Click checks.</summary>
    public static (string Field, string Message)? Validate(PayorReceiptUpdateRequest r)
    {
        if (Header(r.Header) is { } h) return h;
        if (string.IsNullOrWhiteSpace(r.Header.Bank)) return ("bank", "Please Enter Bank Name ");
        return Claim(r.Claim);
    }

    private static (string Field, string Message)? Header(PayorReceiptHeader h)
    {
        if (h.Amount is null) return ("amount", "Please Enter Receipt Amt!");
        if (string.IsNullOrWhiteSpace(h.ChequeNo)) return ("chequeNo", "Please Enter Cheque No!");
        if (h.ReceiptDate is null) return ("receiptDate", "Please Enter Receipt Date");
        if (h.AccountMonth is { } m && m is < 1 or > 12) return ("accountMonth", "Invalid account period month.");
        return null;
    }

    /// <summary>The legacy CmdAdd_Click ("Update Case") checks on a claim's details.</summary>
    public static (string Field, string Message)? Claim(PayorReceiptClaim c)
    {
        var contraCase = !string.IsNullOrWhiteSpace(c.ContraCase);
        var contraVersion = !string.IsNullOrWhiteSpace(c.ContraVersion);
        if (contraCase != contraVersion) return ("contraCase", "Invalid Contra Case..., Please Keyin Contra Case No. or Contra Version ");
        if (contraCase && c.ContraAmount is null) return ("contraAmount", "Please Keyin Contra Case Amount ");
        if (c.Paying < 0) return ("paying", $"{c.CaseNumber}-{c.Version}: the amount paid cannot be negative.");
        return null;
    }

    /// <summary>The case remark line the legacy save appended ("dd/mm/yyyy   Claim Receipt  -  RFnnnnn on dd/mm/yyyy ... user"), "~" separated.</summary>
    public static string CaseRemark(DateOnly today, string receiptNo, DateOnly receiptDate, string user) =>
        $"~{today:dd/MM/yyyy}   Claim Receipt  -  {receiptNo} on {receiptDate:dd/MM/yyyy}{new string(' ', 54)}{user} ";

    /// <summary>The sizes of the case remark columns, in the order the legacy save filled them (CASRemarks, CASRemarksTwo, CASRemarkThree).</summary>
    public static readonly int[] RemarkSlots = [2000, 2000, 2000, 2000, 3800, 3800, 3800];

    /// <summary>
    /// Which remark column takes an appended line, and its new text: the last column in use, or the next one when the line does not fit (the legacy
    /// save moved on at 1,800 / 3,700 characters). When every column is full the last one is cut to its size.
    /// </summary>
    public static (int Slot, string Text) AppendRemark(IReadOnlyList<string?> slots, string line)
    {
        var last = 0;
        for (var i = 0; i < RemarkSlots.Length; i++)
            if (i < slots.Count && !string.IsNullOrEmpty(slots[i])) last = i;
        var current = last < slots.Count ? slots[last] ?? "" : "";
        if (current.Length + line.Length <= RemarkSlots[last]) return (last, current + line);
        if (last + 1 < RemarkSlots.Length) return (last + 1, line.Length <= RemarkSlots[last + 1] ? line : line[..RemarkSlots[last + 1]]);
        var text = current + line;
        return (last, text[..Math.Min(text.Length, RemarkSlots[last])]);
    }
}

public interface IPayorReceiptRepository
{
    Task<PayorReceiptOptions> OptionsAsync(string user, CancellationToken ct = default);
    Task<PayorReceiptListing> SearchAsync(PayorReceiptSearch search, CancellationToken ct = default);
    Task<PayorReceiptResult> SaveAsync(PayorReceiptSaveRequest request, string user, CancellationToken ct = default);
    Task<PayorReceiptResult> UpdateAsync(string receiptNo, PayorReceiptUpdateRequest request, string user, CancellationToken ct = default);
    Task<PayorReceiptBordxListing> SearchBordereauxAsync(PayorReceiptSearch search, CancellationToken ct = default);
    Task<PayorReceiptResult> SaveManualAsync(ManualPayorReceiptRequest request, string user, CancellationToken ct = default);
}
