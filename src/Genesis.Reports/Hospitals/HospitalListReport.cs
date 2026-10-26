using ClosedXML.Excel;
using Genesis.Core.Hospitals;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Genesis.Reports.Hospitals;

public static class HospitalListReport
{
    static HospitalListReport() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] ToPdf(IReadOnlyList<HospitalListItem> rows, string generatedBy) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Header().Column(c =>
            {
                c.Item().Text("Hospital List").FontSize(16).SemiBold();
                c.Item().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by {generatedBy} - {rows.Count} record(s)").FontColor(Colors.Grey.Darken1);
            });
            page.Content().PaddingVertical(8).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(60); c.RelativeColumn(3); c.RelativeColumn(1.5f);
                    c.RelativeColumn(1.5f); c.RelativeColumn(1.2f); c.RelativeColumn(2); c.ConstantColumn(40);
                });
                t.Header(h =>
                {
                    foreach (var title in new[] { "Code", "Name", "Town", "State", "Telephone", "Email", "Status" })
                        h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(title).SemiBold();
                });
                foreach (var r in rows)
                    foreach (var v in new[] { r.Code, r.Name, r.Town, r.State, r.Telephone, r.Email, r.Status })
                        t.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(v?.Trim() ?? "");
            });
            page.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();

    public static byte[] ToExcel(IReadOnlyList<HospitalListItem> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Hospitals");
        string[] headers = ["Code", "Name", "Town", "State", "Telephone", "Email", "Status"];
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < rows.Count; r++)
        {
            var x = rows[r];
            string?[] values = [x.Code, x.Name, x.Town, x.State, x.Telephone, x.Email, x.Status];
            for (var c = 0; c < values.Length; c++) ws.Cell(r + 2, c + 1).Value = values[c]?.Trim() ?? "";
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
