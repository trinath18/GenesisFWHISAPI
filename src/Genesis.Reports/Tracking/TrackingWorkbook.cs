using ClosedXML.Excel;
using Genesis.Core.Tracking;

namespace Genesis.Reports.Tracking;

/// <summary>Generic Excel export for any tracking definition (replaces the per-form Excel/ASCII export code of the legacy screens).</summary>
public static class TrackingWorkbook
{
    public static byte[] ToExcel(TrackingDefinition def, TrackingResult result, IReadOnlyDictionary<string, string> filters, string generatedBy)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(def.Title.Length > 31 ? def.Title[..31] : def.Title);
        ws.Cell(1, 1).Value = def.Title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by {generatedBy} - {result.Rows.Count} row(s){(result.Truncated ? " (limit reached - narrow the filters)" : "")}";
        var applied = string.Join("; ", filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => $"{f.Key}={f.Value}"));
        if (applied.Length > 0) ws.Cell(3, 1).Value = "Filters: " + applied;

        const int headerRow = 5;
        for (var c = 0; c < result.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = result.Columns[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        for (var r = 0; r < result.Rows.Count; r++)
            for (var c = 0; c < result.Columns.Count; c++)
            {
                var cell = ws.Cell(headerRow + 1 + r, c + 1);
                switch (result.Rows[r][c])
                {
                    case null: break;
                    case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = result.Columns[c].Type == "money" ? "#,##0.00" : "#,##0.##"; break;
                    case string s when result.Columns[c].Type == "date" && DateTime.TryParse(s, out var d): cell.Value = d; cell.Style.DateFormat.Format = "dd/mm/yyyy"; break;
                    case var other: cell.Value = other.ToString(); break;
                }
            }
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents(headerRow, headerRow + Math.Min(result.Rows.Count, 200) + 1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
