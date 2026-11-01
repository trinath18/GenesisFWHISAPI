using ClosedXML.Excel;
using GenesisFIS.Core.Cases;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Cases;

/// <summary>Replace the legacy CaseEnq.xlt, CasePayment.xlt and CaseRemark.DOT outputs (no Office needed).</summary>
public static class CaseReports
{
    static CaseReports() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] EnquiryWorkbook(CaseDetail c, IReadOnlyList<CaseWorksheet> worksheets)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Case");
        ws.Cell(1, 1).Value = $"Case Enquiry - {c.Header.CaseNumber}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        (string, object?)[] rows =
        [
            ("Case No", c.Header.CaseNumber), ("Member No", c.Header.MemberNumber), ("Patient", c.Header.PatientName), ("Policy No", c.Member.PolicyNo),
            ("Member", c.Member.Name), ("IC / BC / Passport", c.Member.IcBcPp), ("Status", c.Header.Status), ("Claimability", c.Header.Claimability),
            ("Payor effective", c.Member.PayorEffDate), ("Payor expiry", c.Member.PayorExpDate), ("Available limit", c.Member.AvailableLimit),
            ("Hospital", c.Header.HospitalName), ("Admitted", c.Header.Admitted), ("Discharged", c.Header.Discharged), ("Admission reason", c.Header.AdmissionReason),
            ("Referred by", c.Header.ReferredBy), ("Attending doctors", string.Join(", ", c.Clinical.AttendingDoctors)),
            ("Final diagnosis", string.Join("; ", c.FinalDiagnoses.Select(d => $"{d.IcdCode} {d.Description}".Trim()))),
            ("Preliminary diagnosis", string.Join("; ", c.FirstDiagnoses.Select(d => $"{d.IcdCode} {d.Description}".Trim()))),
            ("Procedures", string.Join("; ", c.Clinical.Procedures.Where(p => !p.NonSurgical).Select(p => p.Procedure))),
        ];
        var r = 3;
        foreach (var (label, value) in rows)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.FontColor = XLColor.Gray;
            Put(ws.Cell(r, 2), value);
            r++;
        }
        ws.Column(1).Width = 24;
        ws.Column(2).Width = 80;

        var hs = wb.AddWorksheet("Worksheet history");
        Header(hs, 1, "Version", "Total approved", "Total actual", "Payable to hospital", "Payable to member", "Bordx ref", "Bordx date", "Bordx amount", "DN/CN ref", "DN/CN date");
        r = 2;
        foreach (var w in worksheets)
            Row(hs, r++, w.Version, w.TotalApproved, w.TotalActual, w.PayableToHospital, w.PayableToMember, w.BordxRefNo, w.BordxDate, w.BordxAmount, w.DebitCreditRef, w.DebitCreditDate);
        hs.Columns().AdjustToContents();
        return Save(wb);
    }

    public static byte[] PaymentsWorkbook(string caseNumber, CasePayments p)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Payments");
        ws.Cell(1, 1).Value = $"Payments and receipts - {caseNumber}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        var r = 3;
        r = Block(ws, r, "Payment to hospital", p.ToHospital);
        r = Block(ws, r, "Payment to member", p.ToMember);
        r = Block(ws, r, "Payment to member (reissued)", p.ToMemberReissued);
        r = Block(ws, r, "Receipt from member", p.ReceiptsFromMember);
        r = Block(ws, r, "Receipt from payor", p.ReceiptsFromPayor);
        Block(ws, r, "Receipt from MNRB", p.ReceiptsFromMnrb);
        ws.Columns().AdjustToContents();
        return Save(wb);
    }

    public static byte[] NotesPdf(string caseNumber, bool internalNotes, string text, string generatedBy) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(10));
            page.Header().Column(c =>
            {
                c.Item().Text(internalNotes ? $"{caseNumber} (Internal Notes)" : caseNumber).FontSize(15).SemiBold();
                c.Item().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by {generatedBy}").FontColor(Colors.Grey.Darken1);
            });
            page.Content().PaddingVertical(10).Text(string.IsNullOrWhiteSpace(text) ? "(no notes)" : text);
            page.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();

    private static int Block(IXLWorksheet ws, int row, string title, IReadOnlyList<CasePayment> items)
    {
        ws.Cell(row, 1).Value = $"{title} ({items.Count})";
        ws.Cell(row, 1).Style.Font.Bold = true;
        Header(ws, row + 1, "No", "Reference", "Version", "Date", "Cheque no", "Cheque date", "Amount", "Status", "Remark");
        row += 2;
        var n = 1;
        foreach (var i in items) Row(ws, row++, n++, i.Reference, i.Version, i.Date, i.ChequeNo, i.ChequeDate, i.Amount, i.Status, i.Remark);
        return row + 1;
    }

    private static void Header(IXLWorksheet ws, int row, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            ws.Cell(row, i + 1).Value = titles[i];
            ws.Cell(row, i + 1).Style.Font.Bold = true;
            ws.Cell(row, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }
    }

    private static void Row(IXLWorksheet ws, int row, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++) Put(ws.Cell(row, i + 1), values[i]);
    }

    private static void Put(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: break;
            case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = "dd/mm/yyyy"; break;
            case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00"; break;
            case int n: cell.Value = n; break;
            default: cell.Value = value.ToString()!.Trim(); break;
        }
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
