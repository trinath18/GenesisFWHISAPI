namespace GenesisFIS.Core.Membership;

public static class McoReceiptTypes
{
    public const string McoFees = "M";
    public static readonly IReadOnlyDictionary<string, string> Others = new Dictionary<string, string> { ["A"] = "A", ["C"] = "C", ["F"] = "F", ["O"] = "O" };
    public static string? DefaultAccount(string type) => type switch { "F" => "6720", "C" => "2040", _ => null };
}

/// <summary>An MCO invoice (or a credit note) that can be receipted, with what has already been received on it.</summary>
public sealed record McoReceiptInvoice(string Number, DateOnly? Date, decimal Total, string Payor, string Kind, int ReceiptLines, decimal Received, string? ReceiptNo);

public sealed record McoReceiptInvoiceSearch(string? Payor, string? NumberFrom, string? NumberTo, DateOnly? DateFrom, DateOnly? DateTo, bool IncludeCreditNotes, bool OnlyOpen);

public sealed record McoReceiptInvoiceListing(IReadOnlyList<McoReceiptInvoice> Rows, bool Truncated);

/// <summary>
/// A new receipt. Type M receipts MCO invoices (and credit notes): one RECMCO line per invoice. Types A, C, F, O are other receipts: one RECOthers row with an account code.
/// ReceiptNo is generated (MB + 5 digits) unless the manual screen supplies it.
/// </summary>
public sealed record McoReceiptRequest(string? Type, string? ReceiptNo, DateOnly? ReceiptDate, decimal? Amount, string? ChequeNo, DateOnly? ChequeDate, string? Remarks, string? AccountCode, IReadOnlyList<string>? Invoices);

public sealed record McoReceiptResult(string ReceiptNo, int Lines, decimal Allocated, decimal Difference);

public sealed record McoReceiptSearch(string? Type, string? ReceiptFrom, string? ReceiptTo, DateOnly? DateFrom, DateOnly? DateTo, string? InvoiceNo);

public sealed record McoReceiptRow(string Kind, int Index, string ReceiptNo, DateOnly? ReceiptDate, decimal? Amount, string Type, string? InvoiceNo, DateOnly? InvoiceDate, decimal? Allocated,
    string? ChequeNo, string? ChequeDate, string? AccountCode, string? Remarks, string? UpdatedBy);

public sealed record McoReceiptListing(IReadOnlyList<McoReceiptRow> Rows, decimal TotalAmount, bool Truncated);

/// <summary>Changes the header fields of a receipt line (all lines of an MCO receipt share them); Allocated applies to the chosen MCO line only.</summary>
public sealed record McoReceiptUpdate(DateOnly? ReceiptDate, decimal? Amount, string? ChequeNo, DateOnly? ChequeDate, string? Remarks, decimal? Allocated);

public static class McoReceiptRules
{
    public const decimal MaxAmount = 922337203685477m;
    public const int MaxInvoices = 200;

    private static bool Bad(string? s, int max) => (s?.Trim().Length ?? 0) > max || (s?.Any(char.IsControl) ?? false);

    public static (string Field, string Message)? Validate(McoReceiptRequest r, bool manual)
    {
        var type = r.Type?.Trim() ?? "";
        if (type != McoReceiptTypes.McoFees && !McoReceiptTypes.Others.ContainsKey(type)) return ("type", "Please Enter Rec. Type");
        if (r.ReceiptDate is null || r.ReceiptDate < new DateOnly(1900, 1, 1)) return ("receiptDate", "Please Enter Receipt Date");
        if (r.Amount is null) return ("amount", "Please Enter Receipt Amount");
        if (r.Amount <= 0 || r.Amount > MaxAmount) return ("amount", "The receipt amount must be more than zero.");
        if (manual && (string.IsNullOrWhiteSpace(r.ReceiptNo) || Bad(r.ReceiptNo, 20) || r.ReceiptNo.Trim().Any(c => !char.IsLetterOrDigit(c) && c != '-'))) return ("receiptNo", "Please Enter Receipt No");
        if (Bad(r.ChequeNo, type == "M" ? 20 : 15)) return ("chequeNo", $"The cheque number can be at most {(type == "M" ? 20 : 15)} characters.");
        if (Bad(r.Remarks, 50)) return ("remarks", "The remarks can be at most 50 characters.");
        if (type == "M")
        {
            if (r.Invoices is null || r.Invoices.Count == 0) return ("invoices", "Please select the invoices to receipt.");
            if (r.Invoices.Count > MaxInvoices) return ("invoices", $"At most {MaxInvoices} invoices can be receipted at once.");
            if (r.Invoices.Any(i => string.IsNullOrWhiteSpace(i) || Bad(i, 20)) || r.Invoices.Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Invoices.Count) return ("invoices", "An invoice number is invalid or selected twice.");
        }
        else
        {
            var account = string.IsNullOrWhiteSpace(r.AccountCode) ? McoReceiptTypes.DefaultAccount(type) : r.AccountCode.Trim();
            if (string.IsNullOrWhiteSpace(account) || Bad(account, 15)) return ("accountCode", "Please Enter Acc Code");
        }
        return null;
    }

    public static (string Field, string Message)? ValidateUpdate(McoReceiptUpdate u, string kind)
    {
        if (u.ReceiptDate is null || u.ReceiptDate < new DateOnly(1900, 1, 1)) return ("receiptDate", "Invalid Receipt Date!");
        if (u.Amount is null || u.Amount <= 0 || u.Amount > MaxAmount) return ("amount", "Please Enter Cheque Amt!");
        if (Bad(u.ChequeNo, kind == "M" ? 20 : 15)) return ("chequeNo", "The cheque number is too long.");
        if (Bad(u.Remarks, 50)) return ("remarks", "The remarks can be at most 50 characters.");
        if (kind == "M" && u.Allocated is null) return ("allocated", "Please Enter Invoice Receipt Amount");
        return null;
    }
}

public interface IMcoReceiptRepository
{
    Task<McoReceiptInvoiceListing> InvoicesAsync(McoReceiptInvoiceSearch search, CancellationToken ct = default);
    Task<McoReceiptListing> ListAsync(McoReceiptSearch search, CancellationToken ct = default);
    Task<McoReceiptResult> CreateAsync(McoReceiptRequest request, bool manual, string user, CancellationToken ct = default);
    Task UpdateAsync(string kind, int index, McoReceiptUpdate update, string user, CancellationToken ct = default);
    Task DeleteAsync(string kind, int index, string user, CancellationToken ct = default);
}
