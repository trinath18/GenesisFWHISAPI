using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// The data behind the legacy worksheet documents: the Credit/Debit Note (CreditNote.xlt / DebitNote.xlt), the excess letter (EXCLetterRBNew.DOT, from
/// CAS_Excess), the cheque letter (Cheque Letter.DOT) and the worksheet Export.
/// </summary>
internal sealed partial class WorksheetRepository
{
    public async Task<WorksheetDocumentData?> DocumentAsync(string caseNumber, int version, string user, CancellationToken ct = default)
    {
        if (await EnquiryAsync(caseNumber, ct) is not { } enquiry || enquiry.Versions.FirstOrDefault(v => v.Version == version) is not { } saved) return null;
        var no = enquiry.Case.CaseNumber;
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.MBMPatientCovID, m.MBMName, m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.MBMPostCode, m.CTYCode, m.STACode, m.MBMPayorEffDate, cp.MBMCName,
                   y.PAYCompanyName, y.PAYMainContact, h.HOSContact, h.HOSFaxNo, d.DIAFinalDiag1, d.DIAFinalDiag2, d.DIAFinalDiag3, d.ICDCode1, d.ICDCode2, d.ICDCode3,
                   (SELECT TOP 1 p.GRPCompany FROM PLN p WHERE p.PLNCode = m.PLNCode AND p.HLTCode = m.HLTCode ORDER BY p.PLNEffDate DESC) AS GRPCompany
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber
                 LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID
                 LEFT JOIN PAY y ON y.PAYCode = m.PAYCode LEFT JOIN HOS h ON h.HOSCode = c.CASHOSCode LEFT JOIN DIAFinal d ON d.CASNumber = c.CASNumber
            WHERE c.CASNumber = @no
            """, new { no }, ct)).First();
        string? T(string col) => Blank(S(c, col));
        var principal = T("MBMName") ?? "";
        var patient = T("MBMPatientCovID") is null or "00" ? principal : T("MBMCName") ?? principal;
        static string? Join(string sep, params string?[] parts) => parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList() is { Count: > 0 } l ? string.Join(sep, l) : null;

        var w = (await QueryAsync(GenesisDb.Worksheet, """
            SELECT k.CLMReimburse, k.CLMFinalApprove, k.CLMPayTo, k.CLMBank, k.CLMAccountNo, o.CLMWSRemarks, o.CLMDCNoteRemarks
            FROM Claim k LEFT JOIN ClaimOthers o ON o.CASNumber = k.CASNumber AND o.CLaimVersion = k.ClaimVersion WHERE k.CASNumber = @no AND k.ClaimVersion = @version
            """, new { no, version }, ct)).First();
        string? W(string col) => Blank(S(w, col)) is { } v && v != "0" ? v : null;

        // Legacy bookmark(): the case's excess record (the latest when a case has several).
        var x = (await QueryAsync(GenesisDb.Worksheet, "SELECT TOP 1 * FROM CAS_Excess WHERE CASNumber = @no ORDER BY EXCDate DESC, EXCNumber DESC", new { no }, ct)).FirstOrDefault();
        WorksheetExcess? excess = null;
        if (x is not null)
        {
            var lines = WorksheetCatalog.Lines.Where(l => WorksheetExcessCatalog.Columns.ContainsKey(l.Row))
                .Select(l => new WorksheetExcessLine(l.Row, l.Label, WorksheetCalculator.Parse(S(x, WorksheetExcessCatalog.Columns[l.Row])) ?? 0)).Where(l => l.Amount != 0).ToList();
            excess = new WorksheetExcess(S(x, "EXCNumber")!.Trim(), Day(x, "EXCDate"), Blank(S(x, "INVNo")), Dec(x, "INVAmt"), Dec(x, "ExcessAmt") ?? lines.Sum(l => l.Amount),
                WorksheetCalculator.Parse(S(x, "EXCTotalExceed")) ?? 0, Blank(S(x, "EXCIssuedBy")), lines);
        }

        return new WorksheetDocumentData(enquiry.Case, saved, enquiry.CombinedRows, patient, principal, T("MBMAddress1"), T("MBMAddress2"), T("MBMAddress3"), T("MBMPostCode"),
            T("CTYCode"), T("STACode"), T("PAYCompanyName"), T("PAYMainContact"), T("HOSContact"), T("HOSFaxNo"), T("GRPCompany"), Day(c, "MBMPayorEffDate"),
            Join("; ", T("DIAFinalDiag1"), T("DIAFinalDiag2"), T("DIAFinalDiag3")), Join(", ", T("ICDCode1"), T("ICDCode2"), T("ICDCode3")),
            W("CLMWSRemarks"), W("CLMDCNoteRemarks"), W("CLMPayTo"), W("CLMBank"), W("CLMAccountNo"), Dec(w, "CLMReimburse"), Dec(w, "CLMFinalApprove"), excess, user,
            DateOnly.FromDateTime(DateTime.Today));
    }
}
