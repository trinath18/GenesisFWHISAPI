namespace GenesisFIS.Core.Payments;

/// <summary>
/// The payment voucher printouts of Receipt > Payor (cmdPrintPV, FWHS\PaymentVoucher\PVHospital.xlt) and Payment > To Hospital (cmdPrintPV, PV-Hospital.xlt, and
/// cmdPrintCV, the covering letter PaymentHOSLetter.xlt). The Excel templates are not available, so the documents are a drafted standard layout printing the
/// fields the templates were filled with. Print Cheque (cmdPrintChq, PaymentCheque.xlt) and Print Cheque Range (frmPrintChequeRange) print the cheque face:
/// see <see cref="ChequeRules"/>.
/// </summary>
public sealed record VoucherInvoice(string? InvoiceNo, DateOnly? InvoiceDate, decimal Medical);

/// <summary>One claim paid by the voucher (a Payment2HOS row). <see cref="Medical"/> is the approved medical report fee (ClaimApprove.CLMMedical).</summary>
public sealed record VoucherClaim(
    string CaseNumber, int Version, string? ClaimId, string? Mrn, string? Patient, string? BordxNo, string? ReceiptNo, string? ChequeNo, string? ChequeNoMr,
    decimal BordxAmount, decimal Medical, decimal Paid, IReadOnlyList<VoucherInvoice> Invoices);

public sealed record VoucherData(
    string PvNo, DateOnly? PvDate, DateOnly? ChequeDate, string? Bank, string? Payee, string? HospitalName, string? Address1, string? Address2, string? Address3,
    IReadOnlyList<VoucherClaim> Claims, DateOnly PrintedOn, string PrintedBy);

/// <summary>A voucher line: an invoice of a claim with the hospital charges and / or the medical report fee it carries.</summary>
public sealed record VoucherLine(
    int No, string? InvoiceNo, DateOnly? InvoiceDate, string? Mrn, string? Patient, string? ClaimId, string CaseNumber, int Version, string? BordxNo, string? ReceiptNo,
    string? ChequeNo, decimal HospitalCharges, string? ChequeNoMr, decimal MedicalReport, decimal Total);

/// <summary>A covering letter line: one claim with its invoices and the amount paid.</summary>
public sealed record VoucherLetterLine(int No, string? InvoiceNos, DateOnly? InvoiceDate, string? Patient, string CaseNumber, int Version, decimal Paid);

public static class PaymentVoucherKinds
{
    public const string Voucher = "voucher";
    public const string Letter = "letter";
}

public static class PaymentVoucherRules
{
    /// <summary>
    /// The legacy PV lines (FrmReceiptPayor.cmdPrintPV): a claim with one invoice prints one line with the hospital charges (bordereau amount less the medical
    /// report fee), the medical report fee and their total; a claim with several invoices prints the hospital charges on its invoice without a medical report
    /// amount and the fee on the one with it. Legacy repeated the claim's full charges on every such invoice (and counted them again in the totals); here each
    /// amount is printed once, on the first matching invoice, so the lines add up to the bordereau amounts.
    /// </summary>
    public static IReadOnlyList<VoucherLine> Lines(VoucherData d)
    {
        var lines = new List<VoucherLine>();
        foreach (var c in d.Claims)
        {
            var charges = c.BordxAmount - c.Medical;
            var invoices = c.Invoices.Count > 0 ? c.Invoices : [new VoucherInvoice(null, null, 0)];
            VoucherLine Line(VoucherInvoice i, decimal hos, decimal mr) =>
                new(lines.Count + 1, i.InvoiceNo, i.InvoiceDate, c.Mrn, c.Patient, c.ClaimId, c.CaseNumber, c.Version, c.BordxNo, c.ReceiptNo,
                    hos != 0 ? c.ChequeNo : null, hos, mr != 0 ? c.ChequeNoMr : null, mr, hos + mr);
            if (invoices.Count == 1) { lines.Add(Line(invoices[0], charges, c.Medical)); continue; }
            var mrInvoice = invoices.FirstOrDefault(i => i.Medical != 0);
            var hosInvoice = invoices.FirstOrDefault(i => i.Medical == 0) ?? invoices[0];
            foreach (var i in invoices)
            {
                var hos = ReferenceEquals(i, hosInvoice) ? charges : 0;
                // No invoice carries the fee: it goes on the charges line, as for a single invoice.
                var mr = mrInvoice is null ? (ReferenceEquals(i, hosInvoice) ? c.Medical : 0) : ReferenceEquals(i, mrInvoice) ? c.Medical : 0;
                lines.Add(Line(i, hos, mr));
            }
        }
        return lines;
    }

    /// <summary>The covering letter lines (FrmPaymentHospital.cmdPrintCV): one per claim, its invoices listed together, with the amount paid on the voucher.</summary>
    public static IReadOnlyList<VoucherLetterLine> LetterLines(VoucherData d) =>
        d.Claims.Select((c, n) => new VoucherLetterLine(n + 1,
            c.Invoices.Count == 0 ? null : string.Join(", ", c.Invoices.Select(i => i.InvoiceNo).OfType<string>().Distinct()),
            c.Invoices.Select(i => i.InvoiceDate).FirstOrDefault(x => x is not null), c.Patient, c.CaseNumber, c.Version, c.Paid)).ToList();
}

/// <summary>
/// One cheque face (legacy PaymentCheque.xlt: payee, the amount in words on two lines, the amount and the date as DD MM YY boxes). One cheque per
/// cheque number, date, amount and payee, as VIEW_Payment2HOS_Chq grouped the voucher rows.
/// </summary>
public sealed record ChequeData(string ChequeNo, DateOnly? ChequeDate, decimal Amount, string? Payee, string Wording1, string Wording2, string AccountType,
    string? PvNo, string? BankAccount, bool Printed);

public sealed record ChequeRangeRequest(string? AccountType, string? ChequeNoFrom, string? ChequeNoTo);

/// <summary>The cheques of a range. <see cref="Expected"/> is the count the numbers imply (null when they are not a numeric series); Print needs them equal.</summary>
public sealed record ChequeRangeListing(IReadOnlyList<ChequeData> Cheques, int? Expected, string? Problem);

public sealed record ChequeAccount(string AccountType, string? BankAccount, int Vouchers);

public static class ChequeRules
{
    /// <summary>The legacy Print Cheque checks (cmdPrintChq_Click), in its order. The payee defaults to the hospital when the voucher has none.</summary>
    public static string? Problem(ChequeData c) =>
        string.IsNullOrWhiteSpace(c.Payee) ? "Please Enter Payee"
        : string.IsNullOrWhiteSpace(c.ChequeNo) ? "Please Enter Cheque No"
        : c.ChequeDate is null ? "Please Enter Cheque Date"
        : c.Amount <= 0 ? "Please Enter Cheque Amount"
        : null;

    /// <summary>The stored wording, or the amount in words when the voucher has none (legacy Update recomputed it the same way).</summary>
    public static (string, string) Wording(string? line1, string? line2, decimal amount) =>
        string.IsNullOrWhiteSpace(line1) ? HospitalPaymentRules.Wording(amount) : (line1.Trim(), line2?.Trim() ?? "");

    /// <summary>
    /// How many cheques the range should hold (legacy: the numeric part of To - From + 1, "Cheque range not in series" when fewer are found). Legacy took
    /// characters 3-10 as the number; here the trailing digits after the same prefix. Null when the numbers are not such a series (e.g. FWHS references).
    /// </summary>
    public static int? SeriesCount(string from, string to)
    {
        static (string, long)? Split(string s)
        {
            s = s.Trim();
            var i = s.Length;
            while (i > 0 && char.IsAsciiDigit(s[i - 1])) i--;
            return i == s.Length || s.Length - i > 15 ? null : (s[..i], long.Parse(s[i..]));
        }
        if (Split(from) is not var (p1, n1) || Split(to) is not var (p2, n2) || !string.Equals(p1, p2, StringComparison.OrdinalIgnoreCase)) return null;
        var count = n2 - n1 + 1;
        return count is > 0 and <= int.MaxValue ? (int)count : 0;
    }

    public static string? RangeProblem(ChequeRangeRequest r) =>
        string.IsNullOrWhiteSpace(r.AccountType) ? "Please select payment type"
        : string.IsNullOrWhiteSpace(r.ChequeNoFrom) || string.IsNullOrWhiteSpace(r.ChequeNoTo) ? "Please Enter Cheque No"
        : string.Compare(r.ChequeNoFrom.Trim(), r.ChequeNoTo.Trim(), StringComparison.OrdinalIgnoreCase) > 0 || SeriesCount(r.ChequeNoFrom, r.ChequeNoTo) == 0
            ? "Please ensure cheque range entered correctly"
        : null;

    /// <summary>The listing check that enabled legacy Print: cheques found, and as many as the series implies.</summary>
    public static string? ListingProblem(int found, int? expected) =>
        found == 0 ? "No record found" : expected is { } e && e != found ? "Cheque range not in series" : null;
}

public interface IPaymentVoucherRepository
{
    /// <summary>The voucher with its claims, or null when no Payment2HOS row has this PV number.</summary>
    Task<VoucherData?> LoadAsync(string pvNo, string printedBy, CancellationToken ct = default);

    /// <summary>The cheque(s) of one voucher (usually one), or an empty list when no Payment2HOS row has this PV number.</summary>
    Task<IReadOnlyList<ChequeData>> ChequesAsync(string pvNo, CancellationToken ct = default);

    /// <summary>The voucher series (first two letters of the PV number) with the bank account of ChequeType where it has one.</summary>
    Task<IReadOnlyList<ChequeAccount>> ChequeAccountsAsync(CancellationToken ct = default);

    /// <summary>The cheques not yet printed (PVChequeStatus empty) in the range, as frmPrintChequeRange listed them.</summary>
    Task<ChequeRangeListing> ChequeRangeAsync(ChequeRangeRequest request, CancellationToken ct = default);

    /// <summary>Lists the range, refuses it when Print would be disabled, marks the vouchers PVChequeStatus 'Y' and returns the cheques to print.</summary>
    Task<IReadOnlyList<ChequeData>> PrintChequeRangeAsync(ChequeRangeRequest request, string user, CancellationToken ct = default);
}
