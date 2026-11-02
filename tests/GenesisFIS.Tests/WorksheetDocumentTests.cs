using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Reports.Cases;
using Microsoft.Data.SqlClient;

namespace GenesisFIS.Tests;

public class WorksheetExcessCatalogTests
{
    private static WorksheetLineValue Line(int row, string? member) => new(row, null, "Row " + row, null, null, null, null, member);

    [Fact]
    public void Negative_member_cells_are_the_excess_the_member_owes()
    {
        var lines = WorksheetExcessCatalog.Lines([Line(1, "(120.00)"), Line(3, "-"), Line(21, "40.00"), Line(25, "(1,750.50)")]);
        Assert.Equal([120m, 0m, 0m, 1750.50m], lines.Select(l => l.Amount));
        Assert.Equal(1870.50m, WorksheetExcessCatalog.Total([Line(1, "(120.00)"), Line(25, "(1,750.50)"), Line(21, "40.00")]));
        // Every grid row but home nursing (24) and "others, no co-pay" (26) has a CAS_Excess column.
        Assert.Equal(Enumerable.Range(1, 29).Except([24, 26]), WorksheetExcessCatalog.Columns.Keys.Order());
    }

    private static WorksheetDocumentData Data(string? note, decimal? payM, decimal? reimburse = null, WorksheetExcess? excess = null) => new(
        new CaseInvoiceCase("ZZ1", "M1", "PRINCIPAL", "POL", "00", "FH01", "00001", "HOSPITAL", new DateOnly(2024, 5, 1), new DateOnly(2024, 5, 4), "I"),
        new WorksheetVersion(1, "C", "AA", "Approved", null, null, null, 1500, 1380, 1380, payM, -120, null, null, null, null, null, note, new DateOnly(2024, 5, 5), null,
            new Dictionary<string, string?>(), WorksheetCatalog.Lines.Select(l => new WorksheetLineValue(l.Row, l.BenefitCode, l.Label, "-", "-", "-", "-", l.Row == 1 ? "(120.00)" : "-")).ToList(), []),
        false, "PATIENT", "PRINCIPAL", "1 JALAN", null, null, "50000", "KL", "14", "PAYOR", "CONTACT", "ATTN", "03-1", "GROUP", null, "FEVER", "A00", "OK", null, null, null, null,
        reimburse, 1380, excess, "TEST", new DateOnly(2024, 5, 6));

    [Fact]
    public void The_documents_follow_the_saved_note_excess_and_member_payment()
    {
        Assert.Equal([WorksheetDocumentKinds.Export], Data(null, 0).Available);
        Assert.Equal([WorksheetDocumentKinds.CreditNote, WorksheetDocumentKinds.ChequeLetter, WorksheetDocumentKinds.Export], Data("CWM00042", 900).Available);
        var excess = new WorksheetExcess("EZZ1-1", null, "H1", 1500, 120, 0, "TEST", [new(1, "Room & Board", 120)]);
        Assert.Equal([WorksheetDocumentKinds.ExcessLetter, WorksheetDocumentKinds.Export], Data(null, 0, excess: excess).Available);

        Assert.Equal(900m, Data("CWM00042", 900).NoteAmount);
        // A debit note asks back what the member was reimbursed beyond their share.
        Assert.Equal(12366.75m, Data("DAX00023", 11847, 24213.75m).NoteAmount);
    }

    [Fact]
    public void Every_document_renders()
    {
        var excess = new WorksheetExcess("EZZ1-1", new DateOnly(2024, 5, 5), "H1", 1500, 120, 50, "TEST", [new(1, "Room & Board", 120)]);
        foreach (var kind in WorksheetDocumentKinds.Letters)
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(WorksheetDocuments.ToPdf(kind, Data(kind == WorksheetDocumentKinds.DebitNote ? "D1" : "C1", 900, 1000, excess)), 0, 4));
        var xlsx = WorksheetDocuments.Export(Data("C1", 900));
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(xlsx, 0, 2));
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);
        Assert.Equal("Worksheet ZZ1-1", ws.Cell(1, 1).GetString());
        Assert.Contains(ws.CellsUsed(), c => c.GetString() == "Room & Board");
    }

    /// <summary>
    /// The excess rule, checked against the excess records the legacy save wrote: each CAS_Excess amount is the negative member cell of that row in the
    /// worksheet version with the record's sequence number.
    /// </summary>
    [DatabaseFact]
    public void Excess_amounts_match_the_saved_legacy_excess_records()
    {
        using var conn = new SqlConnection(TestHost.Config()["ConnectionStrings:Worksheet"]);
        conn.Open();
        var rows = conn.Query("""
            SELECT TOP 500 x.*, m.* FROM CAS_Excess x
            JOIN ClaimMember m ON m.CASNumber = x.CASNumber AND m.CLMVersion = CAST(SUBSTRING(x.EXCNumber, CHARINDEX('-', x.EXCNumber) + 1, 5) AS int)
            WHERE x.EXCDate >= '2019-01-01' ORDER BY x.EXCDate DESC
            """).Cast<IDictionary<string, object?>>().ToList();
        if (rows.Count == 0) return;
        var cells = 0; var mismatches = new List<string>();
        foreach (var r in rows)
        {
            var lines = WorksheetExcessCatalog.Lines(WorksheetCatalog.Lines.Select(l => new WorksheetLineValue(l.Row, l.BenefitCode, l.Label, null, null, null, null, Convert.ToString(r[l.Column]))));
            foreach (var (row, col) in WorksheetExcessCatalog.Columns)
            {
                cells++;
                var stored = WorksheetCalculator.Parse(Convert.ToString(r[col])) ?? 0;
                if (stored != lines.Single(l => l.Row == row).Amount) mismatches.Add($"{r["EXCNumber"]} {col}");
            }
        }
        Assert.True(mismatches.Count <= cells / 100, $"{mismatches.Count} of {cells} cells differ: {string.Join("; ", mismatches.Take(10))}");
    }
}
