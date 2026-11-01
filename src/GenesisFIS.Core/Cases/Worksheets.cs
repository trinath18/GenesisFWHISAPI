namespace GenesisFIS.Core.Cases;

/// <summary>
/// One row of the legacy worksheet grid (frmNewWorksheet2010 Fgrid, rows from view_bnf_cat_subcat plus the fixed tax rows). Each row is stored as the same
/// text column in ClaimActual, ClaimLimit, ClaimApprove, ClaimHos and ClaimMember. Plans whose code starts with 2-5 keep rows 8 and 13 as combined
/// surgical / medical sums (<see cref="CombinedColumn"/>, <see cref="CombinedInvoiceColumns"/>). <see cref="InvoiceColumns"/> are the ClaimInvoiceActual
/// columns the legacy getDatatoWS added up for the row.
/// </summary>
public sealed record WorksheetLine(int Row, string? BenefitCode, string Label, string Column, IReadOnlyList<string> InvoiceColumns,
    string? CombinedColumn = null, IReadOnlyList<string>? CombinedInvoiceColumns = null);

public static class WorksheetCatalog
{
    public static readonly IReadOnlyList<WorksheetLine> Lines =
    [
        new(1, "101", "Room & Board", "CLMBoardAmt", ["CLMBoardAmt", "CLMNewRBTax"]),
        new(2, "102", "Intensive Care Unit", "CLMICUAmt", ["CLMICUAmt", "CLMNewICUTax"]),
        new(3, "103", "Hospital Supplies & Services", "CLMServices", ["CLMServices"]),
        new(4, "104", "Operating Theatre", "CLMTheatre", ["CLMTheatre"]),
        new(5, "105", "MRI", "CLMMRI", ["CLMMRI"]),
        new(6, "201", "Pre-Hospital X-ray and lab. charges", "CLMPreHSAmt", ["CLMPreHSAmt"]),
        new(7, "202", "Pre-Hospital Specialist Fee", "CLMPreHosp", ["CLMPreHosp"]),
        new(8, "203", "Surgical Fees", "CLMSurgicalAmt", ["CLMSurgicalAmt"], "CLMCOPreHSSum", ["CLMCHospSum"]),
        new(9, "204", "Anaesthetist's Fee", "CLMAna", ["CLMAna"]),
        new(10, "301", "Pre-Hospital Specialist Fee (Non-Surgical)", "CLMNSPreHos", ["CLMNSPreHos"]),
        new(11, "302", "Daily In-Hospital Physician's Visit", "CLMPhysician", ["CLMPhysician"]),
        new(12, "303", "Post-Hospitalisation Treatment", "CLMPost", ["CLMPost"]),
        new(13, "304", "Medical Benefit", "CLMMPreHSAmt", ["CLMMPreHosAmt"], "CLMCOMPreHSSum", ["CLMMHospSum"]),
        new(14, "401", "Out-Patient Accident", "CLMAccPostAmt", ["CLMAccPostAmt"]),
        new(15, "402", "Ambulance Fees", "CLMAmbulance", ["CLMAmbulance"]),
        new(16, "403", "Physiotherapy Treatment", "CLMOutpatient", ["CLMOutpatient"]),
        new(17, "404", "Kidney Dialysis & Cancer Treatment", "CLMMonthly", ["CLMMonthly"]),
        new(18, "501", "Organ Transplantation", "CLMOrgan", ["CLMOrgan"]),
        // The 2010 worksheet stores the government hospital cash benefit in the CLMTax column.
        new(19, "601", "Govt. Hospitals Income Benefit", "CLMTax", ["CLMCashAmt"]),
        new(20, "701", "Child's Guardian Allowance", "CLMGuardianAmt", ["CLMGuardianAmt", "CLMNewLodgerTax"]),
        new(21, "801", "Medical Report", "CLMMedical", ["CLMMedical"]),
        new(22, "802", "Telephone Charges", "CLMPhone", ["CLMPhone"]),
        new(23, "803", "Admin / Registration", "CLMAdmin", ["CLMAdmin"]),
        new(24, "804", "Home Nursing Care", "CLMHomeNCAmt", ["CLMHomeNCAmt"]),
        new(25, "901", "Others", "CLMOthers1", ["CLMOthers1"]),
        new(26, null, "Others (No Co-Pay)", "CLMOthersNotCOPay", ["CLMOthersNotCOPay"]),
        new(27, null, "Tax - Room & Board", "CLMBoardTaxAmt", ["CLMRBTax"]),
        new(28, null, "Tax - ICU", "CLMICUTaxAmt", ["CLMICUTax"]),
        new(29, null, "Tax - Guardian / Lodger", "CLMLodgerTaxAmt", ["CLMLodgerTax"]),
    ];

    /// <summary>Plans whose code starts with 2-5 (legacy Mid(PLNCode, 1, 1)) use the combined surgical / medical rows.</summary>
    public static bool UsesCombinedRows(string? planCode) => planCode?.Trim() is { Length: > 0 } p && p[0] is >= '2' and <= '5';

    /// <summary>Legacy grid text: "-" for zero, otherwise "#,#.00" (VB Format).</summary>
    public static string Format(decimal amount) => amount == 0 ? "-" : amount.ToString("#,#.00", System.Globalization.CultureInfo.InvariantCulture);

    public static readonly IReadOnlyDictionary<string, string> WorksheetStatuses = new Dictionary<string, string>
    {
        ["AA"] = "Accepted", ["RR"] = "Repudiated", ["CC"] = "Cancelled", ["SA"] = "Special Approval", ["PP"] = "Pay & Claim", ["NN"] = "Not Decided",
        ["NA"] = "Not Decided/Accepted", ["NR"] = "Not Decided/Repudiated", ["PA"] = "Pay&Claim/Accepted", ["PR"] = "Pay&Claim/Rejected", ["AR"] = "Accepted/Rejected",
        ["RA"] = "Rejected/Accepted", ["BR"] = "BTBG/Rejected", ["XR"] = "Recovery/Rejected", ["AI"] = "Accepted/Insurance",
    };
}

/// <summary>One grid row of a worksheet as stored (legacy text: amounts, "-", "FULL" and so on).</summary>
public sealed record WorksheetLineValue(int Row, string? BenefitCode, string Label, string? Limit, string? Actual, string? Approved, string? Hospital, string? Member);

public sealed record WorksheetInvoiceRef(int Index, string InvoiceNo, DateOnly? InvoiceDate, string? PayTo, decimal Amount, decimal PayAmount, bool InWorksheet);

public sealed record WorksheetVersion(
    int Version, string? Status, string? WorksheetStatus, string? WorksheetStatusName, DateOnly? Opened, DateOnly? Closed, string? OpenedBy,
    decimal? TotalActual, decimal? TotalApproved, decimal? PayableToHospital, decimal? PayableToMember, decimal? Excess, decimal? BalanceLimit, decimal? PreviousBalance,
    string? StayType, string? CashType, string? BordxRefNo, string? DebitCreditRefNo, DateOnly? DebitCreditDate, string? PayorApprovalStatus,
    IReadOnlyDictionary<string, string?> LimitDays, IReadOnlyList<WorksheetLineValue> Lines, IReadOnlyList<WorksheetInvoiceRef> Invoices);

/// <summary>Worksheet Enquiry: the case and every saved worksheet version.</summary>
public sealed record WorksheetEnquiry(CaseInvoiceCase Case, bool CombinedRows, IReadOnlyList<WorksheetVersion> Versions);

/// <summary>
/// Worksheet Registration before saving: the invoices waiting for the next version, added up per grid row (actual = all invoices, hospital = invoices
/// paid to the hospital), and the limits, approved amounts and payable split worked out from the plan's benefits (<see cref="WorksheetCalculator"/>).
/// </summary>
public sealed record WorksheetPreview(CaseInvoiceCase Case, int Version, bool CombinedRows, IReadOnlyList<WorksheetInvoiceRef> Invoices, string? Claimability, bool Claimable,
    bool SameLimit, WorksheetCalculation Calculation);

/// <summary>Limits typed into the grid (row number to a number or FULL), as the legacy FGrid_Click allowed.</summary>
public sealed record WorksheetCalculateRequest(IReadOnlyDictionary<int, string>? LimitOverrides);

/// <summary>
/// Saving a worksheet (legacy cmdApprove_Click). <see cref="ExpectedFinalApproved"/> is the amount the officer saw: the save is refused when the
/// recalculation differs (an invoice changed meanwhile). The member fields are needed when the member is paid (reimbursement).
/// </summary>
public sealed record WorksheetSaveRequest(
    string? WorksheetStatus, decimal? ExpectedFinalApproved, IReadOnlyDictionary<int, string>? LimitOverrides, string? Remarks, string? DebitCreditRemarks, string? Note,
    bool ExGratia, string? PayTo, string? Bank, string? AccountNo, string? Address1, string? Address2, string? Address3, string? PostCode, string? City, string? State,
    string? PaymentMode, string? Attention);

/// <summary>The saved worksheet: its number (case-version), amounts and the credit / debit note raised for the member's share.</summary>
public sealed record WorksheetSaveResult(string WorksheetNumber, int Version, decimal FinalApproved, decimal PayableToHospital, decimal PayableToMember, decimal Excess,
    string? DebitCreditNote, decimal? BalanceAfter, int Invoices);

public static class WorksheetSaveRules
{
    private static bool Bad(string? s, int max) => s is not null && (s.Trim().Length > max || s.Any(c => char.IsControl(c) && c is not ('\r' or '\n')));

    /// <summary>The legacy cmdApprove_Click checks that do not need the database, in the legacy order.</summary>
    public static (string Field, string Message)? Validate(WorksheetSaveRequest r, bool memberPaid)
    {
        if (string.IsNullOrWhiteSpace(r.WorksheetStatus) || !WorksheetCatalog.WorksheetStatuses.ContainsKey(r.WorksheetStatus.Trim())) return ("worksheetStatus", "Please select worksheet status!");
        if (memberPaid)
        {
            var bank = !string.IsNullOrWhiteSpace(r.Bank); var account = !string.IsNullOrWhiteSpace(r.AccountNo);
            if (bank && !account) return ("accountNo", "Please Enter Bank Account Number");
            if (!bank && account) return ("bank", "Please Enter Bank Name");
            if (string.IsNullOrWhiteSpace(r.Address1)) return ("address1", "Please Enter Member's Address");
            if (string.IsNullOrWhiteSpace(r.PostCode)) return ("postCode", "Please Enter Post Code");
            if (string.IsNullOrWhiteSpace(r.City)) return ("city", "Please Enter City");
            if (string.IsNullOrWhiteSpace(r.State)) return ("state", "Please Enter State");
        }
        foreach (var (field, value, max) in new[] { ("payTo", r.PayTo, 50), ("bank", r.Bank, 40), ("accountNo", r.AccountNo, 30), ("address1", r.Address1, 50), ("address2", r.Address2, 50),
                     ("address3", r.Address3, 50), ("postCode", r.PostCode, 6), ("city", r.City, 50), ("state", r.State, 50), ("paymentMode", r.PaymentMode, 50), ("attention", r.Attention, 60),
                     ("remarks", r.Remarks, 160), ("debitCreditRemarks", r.DebitCreditRemarks, 200), ("note", r.Note, 100) })
            if (Bad(value, max)) return (field, $"The {field} can be at most {max} characters.");
        return null;
    }
}

/// <summary>
/// Worksheet Adjustment (legacy frmNewWSAdjust2010): a saved version as stored, the same version recalculated from its invoices (with any typed limits), and the
/// details it was saved with, to start the amendment from. Only the latest version, not yet on a bordereau, can be amended (<see cref="BlockedReason"/> otherwise).
/// </summary>
public sealed record WorksheetAdjustmentView(CaseInvoiceCase Case, int Version, IReadOnlyList<int> Versions, bool CanAmend, string? BlockedReason,
    WorksheetVersion Saved, WorksheetPreview Recalculated, WorksheetSaveRequest Details);

public interface IWorksheetRepository
{
    /// <summary>A version for Worksheet Adjustment; the latest when <paramref name="version"/> is null. Null when the case or version does not exist.</summary>
    Task<WorksheetAdjustmentView?> AdjustmentAsync(string caseNumber, int? version, IReadOnlyDictionary<int, string>? limitOverrides = null, CancellationToken ct = default);

    /// <summary>Recalculates and rewrites a saved version in place (legacy Worksheet Adjustment), then updates the member's balance.</summary>
    Task<WorksheetSaveResult> AmendAsync(string caseNumber, int version, WorksheetSaveRequest request, string user, CancellationToken ct = default);

    /// <summary>Saves the next worksheet version of the case from its open invoices (all worksheet tables in one transaction), then updates the case and the member's balance.</summary>
    Task<WorksheetSaveResult> SaveAsync(string caseNumber, WorksheetSaveRequest request, string user, CancellationToken ct = default);

    Task<WorksheetEnquiry?> EnquiryAsync(string caseNumber, CancellationToken ct = default);
    Task<WorksheetPreview?> PreviewAsync(string caseNumber, IReadOnlyDictionary<int, string>? limitOverrides = null, CancellationToken ct = default);

    /// <summary>The calculator input of one version: its invoices (only those not yet in a worksheet when <paramref name="openOnly"/>), the plan's benefits,
    /// same-limit days, claimability and annual balance. Null when the case does not exist.</summary>
    Task<WorksheetInput?> InputAsync(string caseNumber, int version, bool openOnly, CancellationToken ct = default);
}
