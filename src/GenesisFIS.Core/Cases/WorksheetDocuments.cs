namespace GenesisFIS.Core.Cases;

public sealed record WorksheetExcessLine(int Row, string Label, decimal Amount);

/// <summary>
/// The excess record (CAS_Excess, numbered E + case + "-" + sequence) the legacy worksheet save wrote when the hospital billed more than was approved: what the
/// member owes the hospital per benefit row. The excess letter prints it.
/// </summary>
public sealed record WorksheetExcess(string Number, DateOnly? Date, string? InvoiceNo, decimal? InvoiceAmount, decimal Amount, decimal LimitExceeded, string? IssuedBy,
    IReadOnlyList<WorksheetExcessLine> Lines);

/// <summary>Legacy saveExcessLetter: the grid rows that have a CAS_Excess column (rows 24 and 26 had none).</summary>
public static class WorksheetExcessCatalog
{
    public static readonly IReadOnlyDictionary<int, string> Columns = new Dictionary<int, string>
    {
        [1] = "EXCBoardAmt", [2] = "EXCICUAmt", [3] = "EXCServices", [4] = "EXCTheatre", [5] = "EXCMRI", [6] = "EXCPreHSAmt", [7] = "EXCPreHosp", [8] = "EXCSurgicalAmt",
        [9] = "EXCAna", [10] = "EXCNSPreHOS", [11] = "EXCPhysician", [12] = "EXCPost", [13] = "EXCMPreHSAmt", [14] = "EXCEmergency", [15] = "EXCAmbulance",
        [16] = "EXCOutpatient", [17] = "EXCMonthly", [18] = "EXCOrgan", [19] = "EXCTax", [20] = "EXCGuardianAMT", [21] = "EXCMedical", [22] = "EXCPhone", [23] = "EXCAdmin",
        [25] = "EXCOthers1", [27] = "EXCBoardTaxAmt", [28] = "EXCICUTaxAmt", [29] = "EXCLodgerTaxAmt",
    };

    /// <summary>A negative member cell is an excess the member owes; it is stored as a positive amount, every other row as 0.</summary>
    public static IReadOnlyList<WorksheetExcessLine> Lines(IEnumerable<WorksheetLineValue> lines) =>
        lines.Select(l => new WorksheetExcessLine(l.Row, l.Label, Math.Max(0, -(WorksheetCalculator.Parse(l.Member) ?? 0)))).ToList();

    /// <summary>The member's excess on a worksheet: every negative member cell (the Claim.CLMExcess figure, as a positive amount).</summary>
    public static decimal Total(IEnumerable<WorksheetLineValue> lines) => Lines(lines).Sum(l => l.Amount);
}

public static class WorksheetDocumentKinds
{
    public const string CreditNote = "credit-note", DebitNote = "debit-note", ExcessLetter = "excess-letter", ChequeLetter = "cheque-letter", Export = "worksheet";
    public static readonly string[] Letters = [CreditNote, DebitNote, ExcessLetter, ChequeLetter];
}

/// <summary>Everything the worksheet documents print for one saved version (replaces the Excel/Word templates of the legacy worksheet forms).</summary>
public sealed record WorksheetDocumentData(
    CaseInvoiceCase Case, WorksheetVersion Version, bool CombinedRows, string PatientName, string PrincipalName, string? Address1, string? Address2, string? Address3, string? PostCode,
    string? City, string? State, string? Payor, string? PayorContact, string? HospitalContact, string? HospitalFax, string? GroupCompany, DateOnly? PolicyEffective,
    string? Disability, string? IcdCodes, string? WorksheetRemarks, string? DebitCreditRemarks, string? PayTo, string? Bank, string? AccountNo, decimal? Reimburse, decimal? FinalApproved,
    WorksheetExcess? Excess, string IssuedBy, DateOnly Date)
{
    /// <summary>
    /// Legacy enabling: the credit or debit note goes with the note number the save raised (C… or D…), the excess letter needs an excess record, the cheque letter
    /// a payment to the member. The worksheet export is always there.
    /// </summary>
    public IReadOnlyList<string> Available
    {
        get
        {
            var l = new List<string>();
            var note = Version.DebitCreditRefNo?.Trim() ?? "";
            if (note.StartsWith('C')) l.Add(WorksheetDocumentKinds.CreditNote);
            if (note.StartsWith('D')) l.Add(WorksheetDocumentKinds.DebitNote);
            if (Excess is not null) l.Add(WorksheetDocumentKinds.ExcessLetter);
            if ((Version.PayableToMember ?? 0) > 0) l.Add(WorksheetDocumentKinds.ChequeLetter);
            l.Add(WorksheetDocumentKinds.Export);
            return l;
        }
    }

    /// <summary>
    /// The note amount: a credit note pays the member's share; a debit note asks back what the member was reimbursed beyond it (the legacy "total payable to
    /// member" with the sign turned).
    /// </summary>
    public decimal NoteAmount
    {
        get
        {
            var payM = Version.PayableToMember ?? 0;
            if (!(Version.DebitCreditRefNo?.Trim() ?? "").StartsWith('D')) return payM;
            var back = (Reimburse ?? 0) - payM;
            return back > 0 ? back : Math.Abs(payM);
        }
    }
}
