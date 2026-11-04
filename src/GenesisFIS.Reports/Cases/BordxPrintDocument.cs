using GenesisFIS.Core.Bordereaux;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Cases;

/// <summary>
/// Claim Bordx > Print Bordx (legacy FrmPrintBordx.DetailPrint into Template\Claim\BordxDetailsTemplate.xlt). The template is not available, so this is
/// a drafted layout with the fields it was filled with. Per bordereau: a cover (sheet 1: payor contact, address, phone and fax, bordereau number and date,
/// number of cases and the totals) and the claim list (sheet 2) with the legacy signature block and company address.
/// </summary>
public static class BordxPrintDocument
{
    static BordxPrintDocument() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>The sender block legacy DetailPrint wrote under the claim list.</summary>
    private static readonly string[] Company =
        ["FWHS Medik Sdn Bhd (841234-P)", "Parklane Commercial Hub, Block F-G-7, No 21 Jalan SS7/26,", "47301 Petaling Jaya", "Selangor Darul Ehsan", "Tel. No. 603-7884 1818"];

    private static string D(DateOnly? d) => d?.ToString("yyyy/MM/dd") ?? "";
    private static string M(decimal m) => m.ToString("#,##0.00");
    private static string V(string? s) => s ?? "";

    private static IContainer Head(IContainer c) => c.BorderBottom(0.75f).PaddingVertical(3).PaddingHorizontal(2).DefaultTextStyle(x => x.SemiBold());
    private static IContainer Cell(IContainer c) => c.BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2).PaddingHorizontal(2);

    public static byte[] ToPdf(IReadOnlyList<BordxPrint> bordereaux, string printedBy) => Document.Create(doc =>
    {
        foreach (var b in bordereaux)
        {
            var payor = $"{b.Payor.Code} - {V(b.Payor.Name)}";
            doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(36); page.DefaultTextStyle(x => x.FontSize(10));
                page.Content().Column(c =>
                {
                    c.Spacing(4);
                    c.Item().AlignCenter().Text("CLAIM BORDEREAUX").FontSize(15).Bold();
                    c.Item().PaddingTop(16).Row(r =>
                    {
                        r.RelativeItem().Column(a =>
                        {
                            a.Item().Text($"Attn: {V(b.Payor.Contact)}");
                            a.Item().Text(payor).Bold();
                            foreach (var line in new[] { b.Payor.Address1, b.Payor.Address2, b.Payor.Address3 }.Where(x => x is not null)) a.Item().Text(line!);
                            if (b.Payor.Phone is not null) a.Item().Text($"Tel: {b.Payor.Phone}");
                            if (b.Payor.Fax is not null) a.Item().Text($"Fax: {b.Payor.Fax}");
                        });
                        r.ConstantItem(200).Column(a =>
                        {
                            a.Item().Text(x => { x.Span("Bordx No: ").FontColor(Colors.Grey.Darken2); x.Span(b.BordxNo).Bold(); });
                            a.Item().Text(x => { x.Span("Bordx Date: ").FontColor(Colors.Grey.Darken2); x.Span(D(b.BordxDate)); });
                        });
                    });
                    c.Item().PaddingTop(24).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                        foreach (var h in new[] { "No. of Cases", "Hospital Charges (RM)", "Medical Report (RM)", "Total (RM)" }) t.Cell().Element(Head).AlignRight().Text(h);
                        t.Cell().Element(Cell).AlignRight().Text(b.Claims.Count.ToString());
                        t.Cell().Element(Cell).AlignRight().Text(M(b.HospitalCharges));
                        t.Cell().Element(Cell).AlignRight().Text(M(b.MedicalReport));
                        t.Cell().Element(Cell).AlignRight().Text(M(b.Total)).Bold();
                    });
                });
                page.Footer().Text($"Printed {DateTime.Today:yyyy/MM/dd} by {printedBy}").FontSize(8).FontColor(Colors.Grey.Darken1);
            });

            doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape()); page.Margin(24); page.DefaultTextStyle(x => x.FontSize(6.5f));
                page.Header().Column(c =>
                {
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Column(a =>
                        {
                            a.Item().Text(payor).Bold();
                            foreach (var line in new[] { b.Payor.Address1, b.Payor.Address2, b.Payor.Address3 }.Where(x => x is not null)) a.Item().Text(line!);
                        });
                        r.ConstantItem(180).AlignRight().Column(a =>
                        {
                            a.Item().AlignRight().Text($"Bordx No: {b.BordxNo}").Bold();
                            a.Item().AlignRight().Text($"Bordx Date: {D(b.BordxDate)}");
                        });
                    });
                    c.Item().PaddingTop(6);
                });
                page.Content().Column(col =>
                {
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd =>
                        {
                            // No, Policy, Name, IC, Claim, DOA, DOD, Hospital, Pay to, Diagnosis, MRN, Claim ID, Hosp charges, Medical report, Total.
                            cd.ConstantColumn(16); cd.ConstantColumn(88); cd.RelativeColumn(1.4f); cd.RelativeColumn(1f); cd.ConstantColumn(46); cd.ConstantColumn(42); cd.ConstantColumn(42);
                            cd.RelativeColumn(1.2f); cd.RelativeColumn(1.2f); cd.RelativeColumn(1.6f); cd.RelativeColumn(0.9f); cd.ConstantColumn(88); cd.ConstantColumn(46); cd.ConstantColumn(38); cd.ConstantColumn(46);
                        });
                        t.Header(h =>
                        {
                            foreach (var x in new[] { "No", "Policy No", "Name", "IC/BC/PP", "Claim No", "DOA", "DOD", "Hospital", "Pay To", "Diagnosis", "MRN", "Claim ID" })
                                h.Cell().Element(Head).Text(x);
                            foreach (var x in new[] { "Hosp Charges", "Medical Rpt", "Total (RM)" }) h.Cell().Element(Head).AlignRight().Text(x);
                        });
                        foreach (var l in b.Claims)
                        {
                            foreach (var x in new[] { l.No.ToString(), V(l.PolicyNo), V(l.Patient), V(l.IcBcPp), $"{l.CaseNumber}-{l.Version}", D(l.Admitted), D(l.Discharged), V(l.Hospital),
                                         V(l.PayTo), V(l.Diagnosis), V(l.Mrn), V(l.ClaimId) })
                                t.Cell().Element(Cell).Text(x);
                            t.Cell().Element(Cell).AlignRight().Text(M(l.HospitalCharges));
                            t.Cell().Element(Cell).AlignRight().Text(M(l.MedicalReport));
                            t.Cell().Element(Cell).AlignRight().Text(M(l.Total));
                        }
                        t.Cell().ColumnSpan(12).PaddingTop(4).AlignRight().Text("Total").Bold();
                        t.Cell().PaddingTop(4).AlignRight().Text(M(b.HospitalCharges)).Bold();
                        t.Cell().PaddingTop(4).AlignRight().Text(M(b.MedicalReport)).Bold();
                        t.Cell().PaddingTop(4).AlignRight().Text(M(b.Total)).Bold();
                    });
                    col.Item().PaddingTop(24).ShowEntire().Row(r =>
                    {
                        r.RelativeItem().Column(a =>
                        {
                            a.Item().Text("Approved By :").FontSize(9);
                            a.Item().PaddingTop(30).Text("Company Stamp").FontSize(9);
                            a.Item().PaddingTop(14).Text("Date :").FontSize(9);
                        });
                        r.RelativeItem().Column(a =>
                        {
                            a.Item().Text("Submitted By :").FontSize(9);
                            a.Item().PaddingTop(30).Text(Company[0]).FontSize(9).Bold();
                            foreach (var line in Company.Skip(1)) a.Item().Text(line).FontSize(9);
                        });
                    });
                });
                page.Footer().AlignRight().Text(x => { x.Span($"{b.BordxNo}   Page "); x.CurrentPageNumber(); x.Span(" of "); x.TotalPages(); });
            });
        }
    }).GeneratePdf();
}
