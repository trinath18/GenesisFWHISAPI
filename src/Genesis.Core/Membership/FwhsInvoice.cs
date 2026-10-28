namespace Genesis.Core.Membership;

/// <summary>Tax variants of the legacy MCO Inv/Listing screens: plain (tax column of the fee rows), GST (6% written when invoiced) and SST (0.00 written when invoiced).</summary>
public static class FwhsTax
{
    public const string None = "none", Gst = "gst", Sst = "sst";
    public static decimal Rate(string tax) => tax == Gst ? 0.06m : 0m;
    public static bool IsValid(string? tax) => tax is None or Gst or Sst;
}

/// <summary>Criteria of MCO Inv/Listing: "new" lists uninvoiced fees of a payor between two dates; "reprint" lists the rows of one invoice number.</summary>
public sealed record FwhsInvoiceFilter(string? Payor, string? Mode, DateOnly? From, DateOnly? To, string? InvoiceNo, string? Tax);

public sealed record FwhsInvoiceRow(
    string Kind, string? PolicyNo, DateOnly? Date, string? BatchNo, int Workers, decimal Fee, decimal Tax, decimal Total, string? InvoiceNo, DateOnly? InvoiceDate, string? InvoiceBy);

public sealed record FwhsInvoiceListing(string PayorName, string BillingMode, bool MedixTax, IReadOnlyList<FwhsInvoiceRow> Rows, int TotalWorkers, decimal TotalFee, decimal TotalTax, decimal Total, bool Truncated);

public sealed record FwhsInvoiceResult(string InvoiceNo, int RowsInvoiced, FwhsInvoiceListing Listing);

public static class FwhsInvoiceRules
{
    public static (string Field, string Message)? Validate(FwhsInvoiceFilter f)
    {
        if (!FwhsTax.IsValid(f.Tax)) return ("tax", "Unknown tax variant.");
        if (string.IsNullOrWhiteSpace(f.Payor) || f.Payor.Trim().Length > 5) return ("payor", "Please Select Insurer");
        if (f.Mode == "reprint")
            return string.IsNullOrWhiteSpace(f.InvoiceNo) || f.InvoiceNo.Trim().Length > 15 || f.InvoiceNo.Any(char.IsControl) ? ("invoiceNo", "Please Enter Invoice Number") : null;
        if (f.Mode != "new") return ("mode", "Please Select Print Status");
        if (f.From is null) return ("from", "Please Enter Bordereaux Date From!");
        if (f.To is null) return ("to", "Please Enter Bordereaux Date To!");
        if (f.To < f.From) return ("to", "The end date cannot be before the start date.");
        return null;
    }
}

public interface IFwhsInvoiceRepository
{
    Task<FwhsInvoiceListing> ListAsync(FwhsInvoiceFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Print New: takes the next invoice number (FWMA + 5 digits from MBMMCOINVQ) and stamps it, with today and the user, on exactly the uninvoiced rows the listing shows.
    /// For GST / SST the tax amount (fee x 6% / 0%) is written to GSTAmount of those rows. One transaction.
    /// </summary>
    Task<FwhsInvoiceResult> InvoiceAsync(FwhsInvoiceFilter filter, string user, CancellationToken ct = default);
}
