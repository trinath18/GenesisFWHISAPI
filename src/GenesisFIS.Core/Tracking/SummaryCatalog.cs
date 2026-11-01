using GenesisFIS.Core.Data;
using static GenesisFIS.Core.Tracking.TrackingFilterKind;

namespace GenesisFIS.Core.Tracking;

/// <summary>
/// Summary (group-by) reports. Each replaces dozens of hard-coded aggregate branches in the legacy ClaimReport / DCUReport / BordxTracking
/// stored procedures: the user picks the grouping columns and measures instead.
/// </summary>
public static class SummaryCatalog
{
    private static SummaryDimension Dim(string key, string header, string? expr = null) => new(key, header, expr ?? key);
    private static SummaryDimension Part(string key, string header, string column, string fn) => new(key, header, $"{fn}({column})");
    private static SummaryMeasure Money(string key, string header, string expr) => new(key, header, expr, TrackingColumnType.Money);
    private static SummaryMeasure Num(string key, string header, string expr) => new(key, header, expr, TrackingColumnType.Number);
    private static SummaryMeasure Amount(string key, string header, string column) => new(key, header, $"SUM(CASE WHEN ISNUMERIC({column}) = 1 THEN CONVERT(money, {column}) ELSE 0 END)", TrackingColumnType.Money);
    private static TrackingFilter F(string key, string label, TrackingFilterKind kind, string? column = null) => new(key, label, column ?? key, kind);

    /// <summary>Calendar parts of each date column: year and month, as in the legacy "by month" reports.</summary>
    private static IEnumerable<SummaryDimension> DateParts(params (string Key, string Header, string Column)[] dates) =>
        dates.SelectMany(d => new[] { Part(d.Key + "Year", d.Header + " year", d.Column, "YEAR"), Part(d.Key + "Month", d.Header + " month", d.Column, "MONTH") });

    private static readonly List<TrackingFilter> ClaimFilters =
    [
        F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"),
        F("hospital", "Hospital", Contains, "HOSName"), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "Insurer", Exact, "INSCode"), F("plan", "Plan", Exact, "PLNCode"),
        F("group", "Group company", Contains, "MBMGroupCompany"), F("diagnosis", "Final diagnosis", Contains, "FinalDiag"), F("state", "State", Exact, "STACode"),
        F("claimNature", "Claim nature", Exact, "ClaimNatureCode"), F("bordxRef", "Bordx ref", Exact, "BordxRefNo"), F("claimStatus", "Claim status", Exact, "ClaimStatus"),
        F("admitted", "Admitted", DateRange, "CASDateAdmitted"), F("discharged", "Discharged", DateRange, "CASDischargeDate"), F("registered", "Case registered", DateRange, "CaseRegDate"),
        F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("effective", "Payor effective", DateRange, "MBMPayorEffDate"), F("expiry", "Payor expiry", DateRange, "MBMPayorExpDate"),
    ];

    private static List<SummaryDimension> ClaimDimensions(bool withBordx = true) =>
    [
        Dim("payor", "Payor", "PAYCode"), Dim("insurer", "Insurer", "INSCode"), Dim("plan", "Plan", "PLNCode"), Dim("ageBand", "Age band", "Ageband"), Dim("sex", "Sex"),
        Dim("claimNature", "Claim nature", "ClaimNatureCode"), Dim("hospital", "Hospital", "HOSName"), Dim("diagnosis", "Final diagnosis", "FinalDiag"), Dim("state", "State", "STACode"),
        Dim("policyYear", "Policy year", "MBMPolicyYear"), Dim("relation", "Relation"), Dim("stayType", "Stay type", "CASStayType"), Dim("payType", "Pay type", "CASPAYType"),
        Dim("claimability", "Claimability", "CASClaimability"), Dim("group", "Group company", "MBMGroupCompany"),
        ..(withBordx ? [Dim("bordxRef", "Bordx ref", "BordxRefNo")] : Array.Empty<SummaryDimension>()),
        ..DateParts(("reg", "Registered", "CaseRegDate"), ("adm", "Admitted", "CASDateAdmitted"), ("dis", "Discharged", "CASDischargeDate"), ("bordx", "Bordx", "BordxDate"),
            ("eff", "Payor effective", "MBMPayorEffDate"), ("exp", "Payor expiry", "MBMPayorExpDate")),
    ];

    private static List<SummaryDimension> CostDimensions() =>
    [
        Dim("payor", "Payor", "PAYCode"), Dim("insurer", "Insurer", "INSCode"), Dim("plan", "Plan", "PLNCode"), Dim("ageBand", "Age band", "Ageband"), Dim("sex", "Sex"),
        Dim("claimNature", "Claim nature", "ClaimNatureCode"), Dim("hospital", "Hospital", "HOSName"), Dim("diagnosis", "Final diagnosis", "FinalDiag"),
        Dim("policyYear", "Policy year", "MBMPolicyYear"), Dim("relation", "Relation"), Dim("takeover", "Take over", "MBMTakeover"), Dim("bordxRef", "Bordx ref", "BordxRefNo"),
        ..DateParts(("adm", "Admitted", "CASDateAdmitted"), ("dis", "Discharged", "CASDischargeDate"), ("bordx", "Bordx", "BordxDate")),
    ];

    private static List<SummaryMeasure> CostMeasures() =>
    [
        Num("claims", "Claims", "COUNT(*)"), Num("cases", "Cases", "COUNT(DISTINCT CASNumber)"), Money("actual", "Total actual", "SUM(CLMTotalActual)"), Money("approved", "Total approved", "SUM(CLMTotalApproved)"),
        Amount("board", "Board", "CLMBoardAmt"), Amount("icu", "ICU", "CLMICUAmt"), Amount("services", "Services", "CLMServices"), Amount("theatre", "Theatre", "CLMTheatre"),
        Amount("surgical", "Surgical", "CLMSurgicalAmt"), Amount("anaesthetist", "Anaesthetist", "CLMAna"), Amount("physician", "Physician", "CLMPhysician"),
        Amount("emergency", "Emergency", "CLMEmergency"), Amount("ambulance", "Ambulance", "CLMAmbulance"), Amount("outpatient", "Outpatient", "CLMOutpatient"),
        Amount("mri", "MRI", "CLMMRI"), Amount("tax", "Tax", "CLMTax"), Amount("others", "Others", "CLMOthers1"),
    ];

    private static SummaryDefinition Cost(string id, string title, int position, string view) =>
        new(id, title, "Claim", position, GenesisDb.Worksheet, view, ClaimFilters.Where(f => f.Column is not ("STACode" or "ClaimStatus")).ToList(), CostDimensions(), CostMeasures(),
            ["payor"], ["claims", "actual", "approved"], RequiresFilter: true);

    public static readonly IReadOnlyList<SummaryDefinition> All =
    [
        new("claim-summary", "Claim Summary (counts and amounts)", "Claim", 44, GenesisDb.Worksheet, "VIEW_NEWCLMReport", ClaimFilters, ClaimDimensions(),
            [
                Num("cases", "Cases", "COUNT(DISTINCT CASNumber)"), Num("claims", "Claims", "COUNT(*)"), Money("actual", "Total actual", "SUM(CLMTotalActual)"),
                Money("approved", "Total approved", "SUM(CLMTotalApproved)"), Money("payableHospital", "Payable to hospital", "SUM(CLMPayableByMedix2H)"),
                Money("payableMember", "Payable to member", "SUM(CLMPayableByMedix2M)"), Money("btbg", "BTBG amount", "SUM(CLMBTBGAmt)"), Money("recovery", "Recovery", "SUM(CLMRecoveryPaidByM)"),
                Num("days", "Days of stay", "SUM(NoofDay)"),
            ],
            ["payor"], ["cases", "actual", "approved"], RequiresFilter: true,
            Description: "Replaces Claim Report 1 and the Simple report: choose any grouping (payor, plan, age band, hospital, diagnosis, month ...) and measures."),

        new("dcu-summary", "DCU Claim Summary", "Claim", 155, GenesisDb.Worksheet, "VIEW_CLMReportDCU",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("hospital", "Hospital", Contains, "HOSName"), F("payor", "Payor", Exact, "PAYCode"),
                F("insurer", "Insurer", Exact, "INSCode"), F("plan", "Plan", Exact, "PLNCode"), F("group", "Group company", Contains, "MBMGroupCompany"), F("diagnosis", "Final diagnosis", Contains, "FinalDiag"),
                F("state", "State", Exact, "STACode"), F("claimNature", "Claim nature", Exact, "ClaimNatureCode"), F("admitted", "Admitted", DateRange, "CASDateAdmitted"),
                F("discharged", "Discharged", DateRange, "CASDischargeDate"), F("registered", "Case registered", DateRange, "CaseRegDate"), F("bordxDate", "Bordx date", DateRange, "BordxDate"),
            ],
            [
                Dim("payor", "Payor", "PAYCode"), Dim("insurer", "Insurer", "INSCode"), Dim("plan", "Plan", "PLNCode"), Dim("ageBand", "Age band", "Ageband"), Dim("sex", "Sex"),
                Dim("claimNature", "Claim nature", "ClaimNatureCode"), Dim("hospital", "Hospital", "HOSName"), Dim("diagnosis", "Final diagnosis", "FinalDiag"), Dim("state", "State", "STACode"),
                Dim("stayType", "Stay type", "CASStayType"), Dim("payType", "Pay type", "CASPAYType"), Dim("group", "Group company", "MBMGroupCompany"),
                ..DateParts(("reg", "Registered", "CaseRegDate"), ("adm", "Admitted", "CASDateAdmitted"), ("dis", "Discharged", "CASDischargeDate"), ("bordx", "Bordx", "BordxDate"),
                    ("eff", "Payor effective", "MBMPayorEffDate"), ("exp", "Payor expiry", "MBMPayorExpDate")),
            ],
            [
                Num("cases", "Cases", "COUNT(DISTINCT CASNumber)"), Num("claims", "Claims", "COUNT(*)"), Money("actual", "Total actual", "SUM(CLMTotalActual)"),
                Money("approved", "Total approved", "SUM(CLMTotalApproved)"), Money("recovery", "Recovery", "SUM(RecoveryAmt)"), Num("days", "Days of stay", "SUM(NoofDay)"),
            ],
            ["payor"], ["cases", "actual", "approved"], RequiresFilter: true),

        Cost("claim-cost-actual", "Claim Cost Breakdown (Actual)", 45, "VIEW_CLMReport_Act"),
        Cost("claim-cost-approved", "Claim Cost Breakdown (Approved)", 45, "VIEW_CLMReport_App"),
        Cost("claim-cost-hospital", "Claim Cost Breakdown (Hospital)", 45, "VIEW_CLMReport_Hos"),
        Cost("claim-cost-member", "Claim Cost Breakdown (Member)", 45, "VIEW_CLMReport_MBM"),

        new("hospital-invoices-not-bordx", "Hospital Invoices Not Yet on Bordx", "Claim Bordx", 43, GenesisDb.Worksheet, "VIEW_HospInv_NotBordx",
            [F("hospital", "Hospital", Contains, "HOSName"), F("invNo", "Invoice No", Prefix, "InvNo")],
            [Dim("hospital", "Hospital", "HOSName")],
            [Num("invoices", "Invoices", "COUNT(InvNo)"), Money("actual", "Total actual", "SUM(CLMTotalActual)"), Money("approved", "Total approved", "SUM(CLMTotalApproved)"), Num("days", "Days", "SUM(NoofDay)")],
            ["hospital"], ["invoices", "actual", "approved"]),

        new("payor-invoices-not-bordx", "Member Invoices Not Yet on Bordx", "Claim Bordx", 43, GenesisDb.Worksheet, "VIEW_MBMInv_NotBordx",
            [F("payor", "Payor", Contains, "Payor"), F("caseNo", "Case No", Prefix, "CASNumber")],
            [Dim("payor", "Payor", "Payor")],
            [Num("invoices", "Invoices", "COUNT(CASNumber)"), Money("actual", "Total actual", "SUM(CLMTotalActual)"), Money("approved", "Total approved", "SUM(CLMTotalApproved)"), Num("days", "Days", "SUM(NoofDay)")],
            ["payor"], ["invoices", "actual", "approved"]),
    ];

    public static SummaryDefinition? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}



