using GenesisFIS.Core.Payments;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Payments;

/// <summary>
/// Replaces the Excel templates of the legacy payment voucher prints: FWHS\PaymentVoucher\PVHospital.xlt / PV-Hospital.xlt (payment voucher) and
/// PaymentHOSLetter.xlt (covering letter to the hospital), and PaymentCheque.xlt (the cheque face). The templates are not available, so this is a drafted standard layout with the fields they were
/// filled with; swap in the approved letterhead and wording if they differ.
/// </summary>
public static class PaymentVoucherDocuments
{
    static PaymentVoucherDocuments() => QuestPDF.Settings.License = LicenseType.Community;

    private static string D(DateOnly? d) => d?.ToString("dd/MM/yyyy") ?? "";
    private static string M(decimal m) => m.ToString("#,##0.00");
    private static string V(string? s) => s ?? "";

    public static byte[] ToPdf(string kind, VoucherData d) => kind == PaymentVoucherKinds.Letter ? Letter(d) : Voucher(d);

    private static void Address(ColumnDescriptor c, VoucherData d)
    {
        c.Item().Text(V(d.HospitalName)).Bold();
        foreach (var a in new[] { d.Address1, d.Address2, d.Address3 }.Where(x => !string.IsNullOrWhiteSpace(x))) c.Item().Text(a!);
    }

    private static void Pair(TableDescriptor t, string label, string value)
    {
        t.Cell().Text(label).FontColor(Colors.Grey.Darken2);
        t.Cell().Text(value).SemiBold();
    }

    private static IContainer Head(IContainer c) => c.BorderBottom(0.75f).PaddingVertical(3).PaddingHorizontal(2).DefaultTextStyle(x => x.SemiBold());
    private static IContainer Cell(IContainer c) => c.BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2).PaddingHorizontal(2);

    private static byte[] Voucher(VoucherData d)
    {
        var lines = PaymentVoucherRules.Lines(d);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape()); page.Margin(28); page.DefaultTextStyle(x => x.FontSize(8));
            page.Header().Column(c =>
            {
                c.Item().AlignCenter().Text("PAYMENT VOUCHER").FontSize(15).Bold();
                c.Item().PaddingTop(8).Row(r =>
                {
                    r.RelativeItem().Column(a => { a.Item().Text("Pay to:").FontColor(Colors.Grey.Darken2); Address(a, d); });
                    r.ConstantItem(230).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(80); cd.RelativeColumn(); });
                        Pair(t, "PV No", d.PvNo); Pair(t, "PV Date", D(d.PvDate)); Pair(t, "Cheque Date", D(d.ChequeDate)); Pair(t, "Bank", V(d.Bank));
                    });
                });
                c.Item().PaddingTop(8);
            });
            page.Content().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    // No, Invoice, Inv date, MRN, Patient, Claim ID, Case, Bordx, Receipt, Cheque, Hosp charges, MR cheque, Medical report, Total.
                    cd.ConstantColumn(20); cd.RelativeColumn(1.3f); cd.ConstantColumn(54); cd.RelativeColumn(1); cd.RelativeColumn(1.8f); cd.RelativeColumn(1.6f); cd.ConstantColumn(56);
                    cd.ConstantColumn(56); cd.ConstantColumn(54); cd.RelativeColumn(1.3f); cd.ConstantColumn(62); cd.RelativeColumn(1.4f); cd.ConstantColumn(56); cd.ConstantColumn(62);
                });
                t.Header(h =>
                {
                    foreach (var x in new[] { "No", "Invoice No", "Inv Date", "MRN", "Patient", "Claim ID", "Case No", "Bordx No", "Receipt No", "Cheque No" })
                        h.Cell().Element(Head).Text(x);
                    h.Cell().Element(Head).AlignRight().Text("Hosp Charges");
                    h.Cell().Element(Head).Text("MR Cheque No");
                    h.Cell().Element(Head).AlignRight().Text("Medical Rpt");
                    h.Cell().Element(Head).AlignRight().Text("Total (RM)");
                });
                foreach (var l in lines)
                {
                    foreach (var x in new[] { l.No.ToString(), V(l.InvoiceNo), D(l.InvoiceDate), V(l.Mrn), V(l.Patient), V(l.ClaimId), $"{l.CaseNumber}-{l.Version}", V(l.BordxNo), V(l.ReceiptNo), V(l.ChequeNo) })
                        t.Cell().Element(Cell).Text(x);
                    t.Cell().Element(Cell).AlignRight().Text(M(l.HospitalCharges));
                    t.Cell().Element(Cell).Text(V(l.ChequeNoMr));
                    t.Cell().Element(Cell).AlignRight().Text(M(l.MedicalReport));
                    t.Cell().Element(Cell).AlignRight().Text(M(l.Total));
                }
                t.Cell().ColumnSpan(10).PaddingTop(4).AlignRight().Text("Grand Total").Bold();
                t.Cell().PaddingTop(4).AlignRight().Text(M(lines.Sum(l => l.HospitalCharges))).Bold();
                t.Cell();
                t.Cell().PaddingTop(4).AlignRight().Text(M(lines.Sum(l => l.MedicalReport))).Bold();
                t.Cell().PaddingTop(4).AlignRight().Text(M(lines.Sum(l => l.Total))).Bold();
            });
            page.Footer().Column(c =>
            {
                c.Item().PaddingTop(16).Row(r =>
                {
                    foreach (var sign in new[] { $"Prepared by: {d.PrintedBy}", "Checked by:", "Approved by:" })
                        r.RelativeItem().PaddingRight(24).Column(s => { s.Item().Height(24); s.Item().LineHorizontal(0.5f); s.Item().Text(sign); });
                });
                c.Item().PaddingTop(6).Row(r =>
                {
                    r.RelativeItem().Text($"Printed {D(d.PrintedOn)}").FontColor(Colors.Grey.Darken1);
                    r.RelativeItem().AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" of "); x.TotalPages(); });
                });
            });
        })).GeneratePdf();
    }

    private static byte[] Letter(VoucherData d)
    {
        var lines = PaymentVoucherRules.LetterLines(d);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(x => x.FontSize(10));
            page.Content().Column(c =>
            {
                c.Spacing(6);
                c.Item().Row(r =>
                {
                    r.RelativeItem().Column(a => Address(a, d));
                    r.ConstantItem(190).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.RelativeColumn(); });
                        Pair(t, "Our Ref", d.PvNo); Pair(t, "Date", D(d.PvDate ?? d.PrintedOn));
                    });
                });
                c.Item().PaddingTop(12).Text("Dear Sir / Madam,");
                c.Item().Text($"PAYMENT OF HOSPITAL BILLS - PAYMENT VOUCHER {d.PvNo}").Bold();
                c.Item().Text("We enclose our payment for the following hospital bills. Kindly acknowledge receipt and update your records accordingly.");
                c.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(26); cd.RelativeColumn(1.6f); cd.ConstantColumn(62); cd.RelativeColumn(2); cd.RelativeColumn(1.1f); cd.ConstantColumn(80); });
                    t.Header(h =>
                    {
                        foreach (var x in new[] { "No", "Invoice No", "Inv Date", "Patient", "Case No" }) h.Cell().Element(Head).Text(x);
                        h.Cell().Element(Head).AlignRight().Text("Amount (RM)");
                    });
                    foreach (var l in lines)
                    {
                        foreach (var x in new[] { l.No.ToString(), V(l.InvoiceNos), D(l.InvoiceDate), V(l.Patient), $"{l.CaseNumber}-{l.Version}" }) t.Cell().Element(Cell).Text(x);
                        t.Cell().Element(Cell).AlignRight().Text(M(l.Paid));
                    }
                    t.Cell().ColumnSpan(5).PaddingTop(4).AlignRight().Text("Total").Bold();
                    t.Cell().PaddingTop(4).AlignRight().Text(M(lines.Sum(l => l.Paid))).Bold();
                });
                c.Item().PaddingTop(12).Text("Thank you.");
                c.Item().Text("Yours faithfully,");
                c.Item().PaddingTop(30).Text("______________________________");
                c.Item().Text("Claims Department");
                c.Item().PaddingTop(6).Text("This is a computer-generated letter.").FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        })).GeneratePdf();
    }

    /// <summary>
    /// Cheque faces, one page each, for pre-printed cheque stock (legacy PaymentCheque.xlt filled row 1 with the date digits DD MM YY, row 3 with the payee,
    /// rows 5 and 7 with the amount in words and the amount beside the first line). 178 x 89 mm; adjust the offsets to the bank's cheque if needed.
    /// </summary>
    public static byte[] Cheques(IReadOnlyList<ChequeData> cheques) => Document.Create(doc =>
    {
        foreach (var c in cheques)
            doc.Page(page =>
            {
                page.Size(178, 89, Unit.Millimetre); page.Margin(6, Unit.Millimetre); page.DefaultTextStyle(x => x.FontSize(10));
                page.Content().Column(col =>
                {
                    var digits = c.ChequeDate is { } d ? d.ToString("ddMMyy") : "      ";
                    col.Item().AlignRight().Row(r =>
                    {
                        foreach (var (ch, i) in digits.Select((ch, i) => (ch, i)))
                            r.ConstantItem(i % 2 == 1 && i < 5 ? 7 : 5, Unit.Millimetre).PaddingRight(i % 2 == 1 && i < 5 ? 2 : 0, Unit.Millimetre)
                                .Border(0.5f).AlignCenter().Text(ch.ToString()).SemiBold();
                    });
                    col.Item().PaddingTop(6, Unit.Millimetre).Row(r =>
                    {
                        r.ConstantItem(12, Unit.Millimetre).Text("Pay").FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().BorderBottom(0.5f).Text(V(c.Payee)).SemiBold();
                    });
                    col.Item().PaddingTop(5, Unit.Millimetre).Row(r =>
                    {
                        r.ConstantItem(12, Unit.Millimetre).Text("RM").FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().BorderBottom(0.5f).Text(c.Wording1);
                        r.ConstantItem(40, Unit.Millimetre).PaddingLeft(4, Unit.Millimetre).Border(0.75f).PaddingHorizontal(2).AlignRight().Text($"**{M(c.Amount)}**").Bold();
                    });
                    col.Item().PaddingTop(4, Unit.Millimetre).Row(r =>
                    {
                        r.ConstantItem(12, Unit.Millimetre);
                        r.RelativeItem().BorderBottom(0.5f).Text(c.Wording2.Length == 0 ? " " : c.Wording2);
                        r.ConstantItem(40, Unit.Millimetre);
                    });
                    col.Item().ExtendVertical().AlignBottom().Row(r =>
                    {
                        r.RelativeItem().Text($"Cheque {c.ChequeNo}   PV {V(c.PvNo)}").FontSize(7).FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().AlignRight().Text(c.BankAccount is null ? "" : $"A/C {c.BankAccount}").FontSize(7).FontColor(Colors.Grey.Darken1);
                    });
                });
            });
    }).GeneratePdf();
}
