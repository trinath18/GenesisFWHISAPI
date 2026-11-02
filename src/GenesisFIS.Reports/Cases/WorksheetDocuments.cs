using ClosedXML.Excel;
using GenesisFIS.Core.Cases;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Cases;

/// <summary>
/// Replaces the Office templates of the legacy worksheet forms: CreditNote.xlt / DebitNote.xlt, EXCLetterRBNew.DOT (excess letter), Cheque Letter.DOT and the
/// worksheet Export. The layout and wording are a drafted standard that prints the fields the legacy templates filled; swap in your approved wording if it differs.
/// </summary>
public static class WorksheetDocuments
{
    static WorksheetDocuments() => QuestPDF.Settings.License = LicenseType.Community;

    private static string D(DateOnly? d) => d?.ToString("dd/MM/yyyy") ?? "-";
    private static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s;
    private static string M(decimal? m) => (m ?? 0).ToString("#,##0.00");
    private static string Ref(WorksheetDocumentData d) => $"{d.Case.CaseNumber}-{d.Version.Version}";

    public static string Title(string kind) => kind switch
    {
        WorksheetDocumentKinds.CreditNote => "CREDIT NOTE",
        WorksheetDocumentKinds.DebitNote => "DEBIT NOTE",
        WorksheetDocumentKinds.ExcessLetter => "EXCESS LETTER",
        _ => "CHEQUE LETTER"
    };

    public static byte[] ToPdf(string kind, WorksheetDocumentData d) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(x => x.FontSize(10));
        var number = kind switch
        {
            WorksheetDocumentKinds.CreditNote or WorksheetDocumentKinds.DebitNote => d.Version.DebitCreditRefNo?.Trim(),
            WorksheetDocumentKinds.ExcessLetter => d.Excess?.Number,
            _ => null
        };
        var date = kind switch
        {
            WorksheetDocumentKinds.CreditNote or WorksheetDocumentKinds.DebitNote => d.Version.DebitCreditDate ?? d.Date,
            WorksheetDocumentKinds.ExcessLetter => d.Excess?.Date ?? d.Date,
            _ => d.Date
        };
        page.Header().Column(c =>
        {
            c.Item().Text(Title(kind)).FontSize(15).Bold();
            c.Item().Text($"Date: {D(date)}    Case No: {Ref(d)}" + (number is null ? "" : $"    No: {number}")).FontColor(Colors.Grey.Darken2);
            c.Item().PaddingBottom(8).LineHorizontal(1);
        });
        page.Content().Column(c =>
        {
            c.Spacing(8);
            switch (kind)
            {
                case WorksheetDocumentKinds.CreditNote or WorksheetDocumentKinds.DebitNote:
                    c.Item().Text($"{V(d.PatientName)}\n{string.Join("\n", new[] { d.Address1, d.Address2, d.Address3 }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.Case.MemberNumber), ("Case / Version", Ref(d)), ("Hospital", d.Case.HospitalName),
                        ("Date of admission", D(d.Case.Admitted)), ("Disability", d.Disability));
                    c.Item().Border(0.5f).Padding(8).Row(r =>
                    {
                        r.RelativeItem().Text(kind == WorksheetDocumentKinds.CreditNote ? "Amount payable to you" : "Amount due from you").Bold();
                        r.ConstantItem(140).AlignRight().Text("RM " + M(d.NoteAmount)).Bold();
                    });
                    if (d.DebitCreditRemarks is not null) c.Item().Text("Remarks: " + d.DebitCreditRemarks);
                    break;
                case WorksheetDocumentKinds.ExcessLetter:
                    var x = d.Excess!;
                    c.Item().Text($"To: {V(d.Case.HospitalName)}\nAttention: {V(d.HospitalContact)}    Fax: {V(d.HospitalFax)}");
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.Case.MemberNumber), ("Case No", d.Case.CaseNumber), ("Date of admission", D(d.Case.Admitted)),
                        ("Date of discharge", D(d.Case.Discharged)), ("Invoice No", x.InvoiceNo), ("Invoice amount (RM)", x.InvoiceAmount is null ? null : M(x.InvoiceAmount)));
                    c.Item().Text("The following charges on the above bill exceed the benefits of the member's plan and are to be collected from the member:");
                    c.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(120); });
                        t.Header(h => { h.Cell().BorderBottom(0.5f).Text("Benefit").Bold(); h.Cell().BorderBottom(0.5f).AlignRight().Text("Excess (RM)").Bold(); });
                        foreach (var l in x.Lines) { t.Cell().Text(l.Label); t.Cell().AlignRight().Text(M(l.Amount)); }
                        if (x.LimitExceeded > 0) { t.Cell().Text("Exceeded annual limit"); t.Cell().AlignRight().Text(M(x.LimitExceeded)); }
                        t.Cell().BorderTop(0.5f).Text("Total excess").Bold(); t.Cell().BorderTop(0.5f).AlignRight().Text(M(x.Amount + x.LimitExceeded)).Bold();
                    });
                    c.Item().Text("Prepared by " + V(x.IssuedBy ?? d.IssuedBy)).FontColor(Colors.Grey.Darken2);
                    break;
                default:
                    c.Item().Text($"{V(d.PrincipalName)}\n{string.Join("\n", new[] { d.Address1, d.Address2, d.Address3, string.Join(" ", new[] { d.PostCode, d.City }.Where(s => !string.IsNullOrWhiteSpace(s))), d.State }.Where(s => !string.IsNullOrWhiteSpace(s)))}");
                    Fields(c, ("Member No", d.Case.MemberNumber), ("Policy No", d.Case.PolicyNo), ("Patient", d.PatientName), ("Hospital", d.Case.HospitalName),
                        ("Date of admission", D(d.Case.Admitted)), ("Date of discharge", D(d.Case.Discharged)), ("Payor", d.Payor));
                    c.Item().Text($"We enclose our cheque for RM {M(d.FinalApproved ?? d.Version.TotalApproved)} being the approved amount of your claim for the above admission, made on behalf of {V(d.Payor)}.");
                    c.Item().Text($"Should you have any query, please contact {V(d.PayorContact)}.");
                    break;
            }
            c.Item().Text("Issued by " + d.IssuedBy).FontColor(Colors.Grey.Darken2);
        });
        page.Footer().Text("Generated by Genesis FIS").FontSize(8).FontColor(Colors.Grey.Medium);
    })).GeneratePdf();

    private static void Fields(ColumnDescriptor c, params (string Label, string? Value)[] fields)
    {
        foreach (var (l, v) in fields)
            c.Item().Row(r => { r.ConstantItem(140).Text(l).FontColor(Colors.Grey.Darken2); r.RelativeItem().Text(V(v)); });
    }

    /// <summary>Legacy CmdExport_Click: the worksheet header, the grid, the totals and the payee, on one sheet.</summary>
    public static byte[] Export(WorksheetDocumentData d)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Worksheet");
        ws.Cell(1, 1).Value = "Worksheet " + Ref(d);
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        var v = d.Version;
        (string, object?)[] left =
        [
            ("Member Name", d.PrincipalName), ("Patient", d.PatientName), ("Hospital", d.Case.HospitalName), ("Disability", d.Disability), ("WS Remarks", d.WorksheetRemarks),
            ("Group Company", d.GroupCompany),
        ];
        (string, object?)[] right =
        [
            ("Plan", d.Case.PlanCode), ("Payor", d.Payor), ("Policy Eff. Date", d.PolicyEffective?.ToDateTime(TimeOnly.MinValue)), ("ICD Code", d.IcdCodes),
            ("Admission", d.Case.Admitted?.ToDateTime(TimeOnly.MinValue)), ("Discharge", d.Case.Discharged?.ToDateTime(TimeOnly.MinValue)),
        ];
        (string, object?)[] third =
        [
            ("Member No", d.Case.MemberNumber), ("Case No", Ref(d)), ("Policy No", d.Case.PolicyNo), ("Status", v.WorksheetStatus is null ? null : $"{v.WorksheetStatus} - {v.WorksheetStatusName}"),
            ("Bordx Ref", v.BordxRefNo), ("CN/DN No", v.DebitCreditRefNo),
        ];
        Pairs(ws, 3, 1, left); Pairs(ws, 3, 4, right); Pairs(ws, 3, 7, third);

        var r = 3 + left.Length + 1;
        string[] head = ["Code", "Benefit", "Limit", "Actual", "Approved", "Hospital", "Member"];
        for (var i = 0; i < head.Length; i++) { ws.Cell(r, i + 1).Value = head[i]; ws.Cell(r, i + 1).Style.Font.Bold = true; ws.Cell(r, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray; }
        r++;
        foreach (var l in v.Lines)
        {
            ws.Cell(r, 1).Value = l.BenefitCode ?? "";
            ws.Cell(r, 2).Value = l.Label;
            ws.Cell(r, 3).Value = l.Limit ?? "-";
            Money(ws.Cell(r, 4), l.Actual); Money(ws.Cell(r, 5), l.Approved); Money(ws.Cell(r, 6), l.Hospital); Money(ws.Cell(r, 7), l.Member);
            r++;
        }
        r++;
        Pairs(ws, r, 1,
        [
            ("Total Actual", v.TotalActual), ("Total Approved", v.TotalApproved), ("Payable to Hospital", v.PayableToHospital), ("Payable to Member", v.PayableToMember),
            ("Excess", v.Excess), ("Bal Lmt After This Claim", v.BalanceLimit),
        ]);
        Pairs(ws, r, 4, [("Pay To", d.PayTo), ("Bank", d.Bank), ("Account No", d.AccountNo)]);
        r += 7;
        Pairs(ws, r, 1, [("Invoices", string.Join(", ", v.Invoices.Select(i => i.InvoiceNo))), ("Prepared by", d.IssuedBy), ("Date", d.Date.ToDateTime(TimeOnly.MinValue))]);
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Money(IXLCell cell, string? text)
    {
        if (WorksheetCalculator.Parse(text) is { } m) { cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00;(#,##0.00);-"; }
        else cell.Value = text ?? "-";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void Pairs(IXLWorksheet ws, int row, int col, (string Label, object? Value)[] pairs)
    {
        foreach (var (label, value) in pairs)
        {
            ws.Cell(row, col).Value = label;
            ws.Cell(row, col).Style.Font.FontColor = XLColor.Gray;
            var cell = ws.Cell(row, col + 1);
            switch (value)
            {
                case null: cell.Value = "-"; break;
                case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00;(#,##0.00)"; break;
                case DateTime t: cell.Value = t; cell.Style.DateFormat.Format = "dd/mm/yyyy"; break;
                default: cell.Value = value.ToString(); break;
            }
            row++;
        }
    }
}
