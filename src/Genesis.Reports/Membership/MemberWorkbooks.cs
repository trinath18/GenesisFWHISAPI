using ClosedXML.Excel;
using Genesis.Core.Membership;

namespace Genesis.Reports.Membership;

/// <summary>Replace the legacy Excel templates MBMExclusion.xlt and PrintHistory.xlt (no Office needed).</summary>
public static class MemberWorkbooks
{
    private const string DateFormat = "dd/mm/yyyy";
    private const string MoneyFormat = "#,##0.00";

    /// <summary>Legacy "Print 2" (MBMExclusion.xlt): member sheet, history sheet, exclusion and remarks sheet.</summary>
    public static byte[] ExclusionForm(MemberDetail m, IReadOnlyList<CoveredPerson> covered, IReadOnlyList<MemberHistoryItem> history,
        IReadOnlyList<MemberAccountHistoryItem> accountHistory, IReadOnlyList<MemberNote> exclusions, IReadOnlyList<MemberNote> remarks, MemberFees? fees)
    {
        using var wb = new XLWorkbook();

        var ws = wb.AddWorksheet("Member");
        ws.Cell(1, 1).Value = "Member Enquiry";
        ws.Cell(1, 1).Style.Font.Bold = true;
        (string, object?)[] left =
        [
            ("Membership No", m.Number), ("Policy No", m.PolicyNo), ("Date of Birth", m.DateOfBirth), ("Insurer", m.InsurerCode),
            ("Payor Effective", m.PayorEffDate), ("Payor Expiry", m.PayorExpDate), ("Bordx Date", m.BordxDate), ("Status", m.Status),
            ("Address 1", m.Address1), ("Address 2", m.Address2), ("Address 3", m.Address3), ("Post Code", m.PostCode), ("City", m.City), ("State", m.State),
        ];
        (string, object?)[] right =
        [
            ("Name", m.Name), ("IC / BC / Passport", m.IcBcPp), ("Sex", m.Sex), ("Plan", m.PlanCode), ("Take Over", m.Takeover), ("Renewal", m.RenewFlag),
            ("Member Type", m.MemberType), ("Data Status", m.DataStatus), ("Group Company", m.GroupCompany), ("Policy Year", m.PolicyYear),
        ];
        Pairs(ws, 3, 1, left);
        Pairs(ws, 3, 4, right);
        var r = 3 + left.Length + 1;
        ws.Cell(r, 1).Value = "Annual Limit / Fees";
        ws.Cell(r, 1).Style.Font.Bold = true;
        Pairs(ws, r + 1, 1, [("Premium", fees?.Premium), ("MCO Fee", fees?.McoFee), ("Access Fee", fees?.AccessFee),
            ("Available Annual Limit", m.AnnualLimitApplies ? m.AvailableLimit : "FULL")]);
        r += 7;
        ws.Cell(r, 1).Value = "Covered persons";
        ws.Cell(r, 1).Style.Font.Bold = true;
        Header(ws, r + 1, "Name", "Cover ID", "IC / BC / Passport", "DOB", "Sex", "Adult/Child", "Relationship", "Status", "Plan");
        var row = r + 2;
        foreach (var c in covered)
            Row(ws, row++, c.Name, c.CoverId, c.IcBcPp, c.DateOfBirth, c.Sex, c.AdultChild, c.Relationship, c.Status, c.PlanCode);
        ws.Columns().AdjustToContents();

        var hs = wb.AddWorksheet("History");
        hs.Cell(1, 1).Value = "Member history";
        hs.Cell(1, 1).Style.Font.Bold = true;
        Header(hs, 2, "Member No", "Cover ID", "Policy No", "Payor Effective", "Payor Expiry", "Data Status", "Status", "Bordx Date");
        row = 3;
        foreach (var h in history) Row(hs, row++, h.MemberNumber, h.CoverId, h.PolicyNo, h.PayorEffDate, h.PayorExpDate, h.DataStatus, h.Status, h.BordxDate);
        row += 2;
        hs.Cell(row++, 1).Value = "Account history";
        hs.Cell(row - 1, 1).Style.Font.Bold = true;
        Header(hs, row++, "Member No", "Admitted", "Case Registered", "Case", "DN/CN Ref", "DN/CN Date", "Payable", "Receipt No", "Receipt Date", "Receipt Amount");
        foreach (var a in accountHistory)
            Row(hs, row++, a.MemberNumber, a.Admitted, a.CaseRegistered, a.CaseReference, a.DebitCreditRef, a.DebitCreditDate, a.PayableByMedix, a.ReceiptNo, a.ReceiptDate, a.ReceiptAmount);
        hs.Columns().AdjustToContents();

        var ns = wb.AddWorksheet("Exclusion and Remarks");
        Header(ns, 1, "Member No", "Type", "Text");
        row = 2;
        foreach (var e in exclusions) Row(ns, row++, e.MemberNumber, "Exclusion", e.Text);
        foreach (var e in remarks) Row(ns, row++, e.MemberNumber, "Remark", e.Text);
        if (!string.IsNullOrWhiteSpace(m.PlanSpecialNote)) Row(ns, row, m.Number, "Plan special note", m.PlanSpecialNote);
        ns.Columns().AdjustToContents();
        ns.Column(3).Width = 80;

        return Save(wb);
    }

    /// <summary>Legacy "Print History" (PrintHistory.xlt): membership history and case history, by IC and by name + date of birth.</summary>
    public static byte[] History(MemberDetail m, MemberHistory history, MemberCaseHistory cases)
    {
        using var wb = new XLWorkbook();

        var ms = wb.AddWorksheet("Member History");
        ms.Cell(1, 1).Value = $"Member history - {m.Number} {m.Name}";
        ms.Cell(1, 1).Style.Font.Bold = true;
        var row = 3;
        row = HistoryBlock(ms, row, "By IC / BC / Passport", history.ByIc);
        HistoryBlock(ms, row + 1, "By name and date of birth", history.ByNameDob);
        if (history.CountsDiffer) ms.Cell(2, 1).Value = "Warning: the two lists have different counts - please check.";
        ms.Columns().AdjustToContents();

        var cs = wb.AddWorksheet("Case History");
        cs.Cell(1, 1).Value = $"Case history - {m.Number} {m.Name}";
        cs.Cell(1, 1).Style.Font.Bold = true;
        row = 3;
        row = CaseBlock(cs, row, "By IC / BC / Passport", cases.ByIc);
        CaseBlock(cs, row + 1, "By name and date of birth", cases.ByNameDob);
        if (cases.CountsDiffer) cs.Cell(2, 1).Value = "Warning: the two lists have different counts - please check.";
        cs.Columns().AdjustToContents();

        return Save(wb);
    }

    private static int HistoryBlock(IXLWorksheet ws, int row, string title, IReadOnlyList<MemberHistoryItem> items)
    {
        ws.Cell(row, 1).Value = $"{title} ({items.Count})";
        ws.Cell(row, 1).Style.Font.Bold = true;
        Header(ws, row + 1, "Member No", "Cover ID", "Top-up", "Top-up No", "Policy No", "Payor Effective", "Payor Expiry", "Data Status", "Status", "Bordx Date");
        row += 2;
        foreach (var h in items) Row(ws, row++, h.MemberNumber, h.CoverId, h.TopupInd, h.TopupNumber, h.PolicyNo, h.PayorEffDate, h.PayorExpDate, h.DataStatus, h.Status, h.BordxDate);
        return row;
    }

    private static int CaseBlock(IXLWorksheet ws, int row, string title, IReadOnlyList<MemberCase> items)
    {
        ws.Cell(row, 1).Value = $"{title} ({items.Count})";
        ws.Cell(row, 1).Style.Font.Bold = true;
        Header(ws, row + 1, "Case No", "Member No", "Patient", "Rel", "Admitted", "Discharged", "Claimability", "Diagnosis", "Status", "Hospital", "Payor");
        row += 2;
        foreach (var c in items) Row(ws, row++, c.CaseNumber, c.MemberNumber, c.PatientName, c.Relationship, c.Admitted, c.Discharged, c.Claimability, c.Diagnosis, c.Status, c.Hospital, c.PayorCode);
        return row;
    }

    private static void Pairs(IXLWorksheet ws, int row, int col, (string Label, object? Value)[] pairs)
    {
        foreach (var (label, value) in pairs)
        {
            ws.Cell(row, col).Value = label;
            ws.Cell(row, col).Style.Font.FontColor = XLColor.Gray;
            Put(ws.Cell(row, col + 1), value);
            row++;
        }
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
            case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = DateFormat; break;
            case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = MoneyFormat; break;
            case int i: cell.Value = i; break;
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
