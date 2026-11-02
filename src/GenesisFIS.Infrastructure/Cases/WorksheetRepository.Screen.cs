using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>The legacy worksheet form around the grid: the "Invoices &amp; Diagnosis" tab, the "Reimbursement Not Cover" tab and the drop-down lists.</summary>
internal sealed partial class WorksheetRepository
{
    public async Task<WorksheetCaseDetail?> DetailAsync(string caseNumber, int version, CancellationToken ct = default)
    {
        if (await invoices.GetAsync(caseNumber, ct) is not { } screen) return null;
        var no = screen.Case.CaseNumber;
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASStatus, c.CASClaimability, c.MBMPatientCovID, m.MBMName, m.MBMMemberType, m.INSCode, m.HLTCode, m.MBMPayorEffDate, m.MBMPayorExpDate, m.MBMAddress1,
                   m.MBMAddress2, m.MBMAddress3, m.MBMPostCode, m.CTYCode, m.STACode, cp.MBMCName, y.PAYCompanyName,
                   (SELECT TOP 1 p.GRPCompany FROM PLN p WHERE p.PLNCode = m.PLNCode AND p.HLTCode = m.HLTCode ORDER BY p.PLNEffDate DESC) AS GRPCompany
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber
                 LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID LEFT JOIN PAY y ON y.PAYCode = m.PAYCode
            WHERE c.CASNumber = @no
            """, new { no }, ct)).First();
        string? T(string col) => Blank(S(c, col));
        var principal = T("MBMName");
        var patientId = T("MBMPatientCovID");

        var d = (await QueryAsync(GenesisDb.Main, "SELECT * FROM DIAFinal WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var diagnoses = d is null ? [] : Enumerable.Range(1, 3)
            .Select(i => new WorksheetDiagnosis(Blank(S(d, $"DIAFinalDiag{i}")), Day(d, $"DIADOS{i}"), Blank(S(d, $"ICDCode{i}"))))
            .Where(x => x.Description is not null || x.IcdCode is not null).ToList();

        var mine = screen.Invoices.Where(i => i.Version == version).ToList();
        var notCovered = CaseInvoiceCatalog.Fields.Where(f => f.Table == InvoiceTable.NotCovered)
            .Select(f => new WorksheetNotCoveredItem(f.Key, f.Label, mine.Sum(i => i.Amounts.GetValueOrDefault(f.Key)))).ToList();
        var lastDoc = mine.Select(i => i.ReceiptDate ?? i.InvoiceDate).Max();

        return new WorksheetCaseDetail(no, T("CASStatus"), T("CASClaimability"), screen.Case.MemberNumber, principal, patientId is null or "00" ? principal : T("MBMCName") ?? principal,
            patientId, T("MBMMemberType"), screen.Case.PolicyNo, screen.Case.PlanCode, T("INSCode"), T("HLTCode"), Day(c, "MBMPayorEffDate"), Day(c, "MBMPayorExpDate"),
            T("PAYCompanyName"), T("GRPCompany"), screen.Case.HospitalName ?? screen.Case.HospitalCode, screen.Case.Admitted, screen.Case.Discharged, Blank(screen.Remarks),
            diagnoses.Count == 0 ? null : string.Join("; ", diagnoses.Select(x => x.Description).OfType<string>()),
            diagnoses.Count == 0 ? null : string.Join(", ", diagnoses.Select(x => x.IcdCode).OfType<string>()),
            T("MBMAddress1"), T("MBMAddress2"), T("MBMAddress3"), T("MBMPostCode"), T("CTYCode"), T("STACode"), diagnoses, screen.Invoices, version, notCovered, lastDoc,
            await SavedHeaderAsync(no, version, ct) is { } saved ? SavedDetails(saved) : null);
    }

    public async Task<WorksheetOptions> OptionsAsync(CancellationToken ct = default)
    {
        var banks = (await QueryAsync(GenesisDb.Maintenance, "SELECT Bank, AccountDigits FROM BankAccounts ORDER BY Bank", null, ct))
            .Select(r => new WorksheetBank(S(r, "Bank")!.Trim(), I(r, "AccountDigits"))).ToList();
        var states = (await QueryAsync(GenesisDb.Main, "SELECT STACode, STADescription FROM STA ORDER BY STADescription", null, ct))
            .Select(r => new CaseOption(S(r, "STACode")!.Trim(), S(r, "STADescription")?.Trim() ?? "")).ToList();
        return new WorksheetOptions(WorksheetCatalog.WorksheetStatuses.Select(x => new CaseOption(x.Key, x.Value)).ToList(), banks, WorksheetOptionLists.PaymentModes, states);
    }

    /// <summary>Legacy checkAccNo: an account number must have as many digits as the chosen bank's accounts.</summary>
    private async Task CheckAccountAsync(WorksheetSaveRequest r, CancellationToken ct)
    {
        if (Blank(r.Bank) is not { } bank || Blank(r.AccountNo) is not { } account) return;
        var digits = (await QueryAsync(GenesisDb.Maintenance, "SELECT AccountDigits FROM BankAccounts WHERE Bank = @bank", new { bank }, ct)).Select(x => I(x, "AccountDigits")).FirstOrDefault();
        if (digits is { } n && n != account.Length) throw new BusinessRuleException($"Please check bank name or account number! {bank} account numbers have {n} digits.", field: "accountNo");
    }
}
