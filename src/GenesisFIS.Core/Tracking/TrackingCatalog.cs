using GenesisFIS.Core.Data;
using static GenesisFIS.Core.Tracking.TrackingFilterKind;

namespace GenesisFIS.Core.Tracking;

/// <summary>
/// Read-only tracking screens migrated from the legacy forms. `ScreenPosition` is the USRAccess position the legacy menu checks
/// (see docs/inventory/menu_map.csv). Columns/filters were taken from the views the legacy stored procedures select from.
/// </summary>
public static class TrackingCatalog
{
    private static TrackingColumn T(string c, string? h = null) => new(c, h ?? c, TrackingColumnType.Text);
    private static TrackingColumn D(string c, string? h = null) => new(c, h ?? c, TrackingColumnType.Date);
    private static TrackingColumn M(string c, string? h = null) => new(c, h ?? c, TrackingColumnType.Money);
    private static TrackingColumn N(string c, string? h = null) => new(c, h ?? c, TrackingColumnType.Number);
    private static TrackingFilter F(string key, string label, TrackingFilterKind kind, string? column = null, params (string, string)[] choices) =>
        new(key, label, column ?? key, kind, choices.Length == 0 ? null : choices.Select(c => new TrackingChoice(c.Item1, c.Item2)).ToList());

    private static readonly (string, string)[] Claimability =
    [
        ("AA", "AA - Accepted"), ("RR", "RR - Rejected"), ("CC", "CC - Cancelled"), ("SA", "SA - Special Approval"), ("PP", "PP - Pay & Claim"),
        ("NN", "NN - Not Decided"), ("NA", "NA - Not Decided/Accepted"), ("NR", "NR - Not Decided/Rejected"), ("PA", "PA - Pay&Claim/Accepted"),
        ("PR", "PR - Pay&Claim/Rejected"), ("AR", "AR - Accepted/Rejected"), ("RA", "RA - Rejected/Accepted"),
    ];

    private static readonly (string, string)[] CaseStatus = [("O", "O - Open"), ("C", "C - Close"), ("S", "S - Suspend"), ("D", "D - Delete"), ("R", "R - Repudiation")];

    // Claim Report 3 / 4 (FrmClaimRpt3 / FrmClaimRpt4). Their views (VIEW_CLMReportCas3 / Cas4 and the ones built on them) read the HIS (Medix) database, so on
    // FWHS they listed HIS cases and matched almost no FWHS claim. The same columns are rebuilt here from the FWHS views, with the legacy day differences.
    // On FWHS the policy expiry is the employer policy's (GroupCompanyFW.CoPolicyExpDate): MBM.MBMPayorExpDate just repeats the effective date, so
    // VIEW_NEWCLMReport already takes it from GroupCompanyFW and the case-level sources below do the same (PolicyExpDate).
    private static string Lags(string expiry) => $"""
        DATEDIFF(day, r.MBMPayorEffDate, r.{expiry}) AS DayInsured, DATEDIFF(day, r.MBMPayorEffDate, r.CASDateAdmitted) AS DOAEFF,
        DATEDIFF(day, r.CASDateAdmitted, r.CaseRegDate) AS DORDOA, DATEDIFF(day, r.CASDateAdmitted, r.{expiry}) AS EXPDOA,
        DATEDIFF(day, r.CASTokenDate, r.CASDischargeDate) AS DPODOD
        """;

    /// <summary>Print Label: the simple member view with the mailing address from MBM (the view has none).</summary>
    private const string MemberLabelSource = """
        SELECT s.*, m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.MBMPostCode
        FROM VIEW_MBM_Search2_Simple s INNER JOIN MBM m ON m.MBMNumber = s.MBMNumber
        """;

    private static readonly string ClaimLags = Lags("MBMPayorExpDate");

    private static readonly string ClaimLagSource = $"SELECT r.*, {ClaimLags}, DATEDIFF(day, r.CASDischargeDate, r.BordxDate) AS BORDOD FROM VIEW_NEWCLMReport r";

    private static readonly string InvoiceLagSource = $"""
        SELECT r.*, {ClaimLags}, i.InvNo, i.InvDate, i.INVEnteredDate, i.INVFolUpDate, i.INVPayTo AS InvoicePayTo, i.INVAmount, i.InvPaidByMem,
               k.CLMExcess, k.CLMReimburse, k.ClaimCloseDate,
               DATEDIFF(day, i.InvDate, i.INVEnteredDate) AS INVDORINV, DATEDIFF(day, i.InvDate, r.CASDischargeDate) AS INVDOD,
               DATEDIFF(day, i.INVFolUpDate, r.CASDateAdmitted) AS PREDOA, DATEDIFF(day, i.INVFolUpDate, r.CASDischargeDate) AS FOLDOD
        FROM VIEW_NEWCLMReport r
        JOIN ClaimInvoice i ON i.CASNumber = r.CASNumber AND i.INVWSversion = CONVERT(nvarchar(2), r.ClaimVersion)
        JOIN Claim k ON k.CASNumber = r.CASNumber AND k.ClaimVersion = r.ClaimVersion
        """;

    /// <summary>Every case (main database) with its token date and the employer policy's expiry date.</summary>
    private const string CaseSource = """
        SELECT v.*, c.CASTokenDate, g.CoPolicyExpDate AS PolicyExpDate
        FROM VIEW_CAS_TRACKING v
        JOIN CAS c ON c.CasNumber = v.CASNumber
        LEFT JOIN MBM m ON m.MBMNumber = v.MBMNumber
        LEFT JOIN GroupCompanyFW g ON g.CoInsurerRefNo = m.MBMInsurerUniqueNo
        """;

    /// <summary>The case-level day differences.</summary>
    private static readonly string CaseLagSource = $"SELECT r.*, {Lags("PolicyExpDate")} FROM ({CaseSource}) r";

    /// <summary>
    /// The "Selection Criteria" of Membership > Tracking (Simple) (frmNewMBMTrackingSimple), shared by its listing and its summary. VIEW_MBM_Search2_Simple holds
    /// 14.2M members, so both screens need a filter. On FWHS the supplementary views are empty and MBMPayorExpDate repeats the effective date, so the
    /// legacy Member Type (principal / supplementary) split and the expiry-based selections are not offered.
    /// </summary>
    internal static List<TrackingFilter> MemberSimpleFilters(bool listing) =>
    [
        F("memberNo", "Member No", TextRange, "MBMNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("ic", "IC / BC / Passport", Exact, "MBMIcBcPp"),
        F("name", "Name", Contains, "MBMName"), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "INS type", Exact, "INSCode"), F("health", "HLT type", Exact, "HLTCode"),
        F("plan", "Plan code", TextRange, "PLNCode"), F("group", "Group company", Contains, "MBMGroupCompany"), F("department", "Department", Exact, "MBMDepartment"),
        F("agent", "Agent code", Exact, "MBMAgentCode"), F("employee", "Employee No", Exact, "MBMEmployeeNo"), F("state", "State", Exact, "STACode"),
        F("race", "Race", Exact, "RACCode"), F("ageCode", "Age band", Exact, "AGECode"), F("sex", "Sex", Choice, "MBMSex", ("M", "M - Male"), ("F", "F - Female")),
        F("adultChild", "Adult / child", Choice, "MBMAdultChild", ("A", "A - Adult"), ("C", "C - Child")),
        F("status", "Policy status", Choice, "MBMStatus", ("A", "A - Active"), ("C", "C - Cancelled"), ("S", "S - Suspended"), ("D", "D - Deleted")),
        F("dataStatus", "Data status", Choice, "MBMDataStatus", DataStatus), F("expired", "Expired", Choice, "MBMExpiredStatus", YesNo),
        F("renew", "Renewal", Choice, "MBMRenewFlag", ("R", "R - Renewal"), ("N", "N - New")), F("takeover", "Take over", Exact, "MBMTakeover"),
        F("premiumMode", "Premium mode", Exact, "MBMInstallment"), F("batch", "Batch No", Integer, "MBMBatchNo"), F("invoiceNo", "Invoice No", Exact, "MBMInvoiceNo"),
        F("registered", "Date registered", DateRange, "MBMRegDate"), F("effective", "Eff date", DateRange, "MBMPayorEffDate"), F("bordx", "Bordx date", DateRange, "MBMBordxDate"),
        F("invoiced", "Invoice date", DateRange, "MBMInvoiceDate"), F("joined", "Date join", DateRange, "MBMFirstJoinedDate"), F("dob", "DOB", DateRange, "MBMDOB"),
        ..(listing
            ? [F("ageDays", "Age (days)", NumberRange, "AgeDays"), F("delayReg", "Delay: eff - reg", NumberRange, "DelayReg"), F("delayBordx", "Delay: eff - bordx", NumberRange, "DelayBordx"),
               F("premium", "Premium", NumberRange, "MBMPremium"), F("mco", "MCO fee", NumberRange, "MBMMCOFee")]
            : Array.Empty<TrackingFilter>()),
    ];

    private static readonly (string, string)[] YesNo = [("Y", "Y - Yes"), ("N", "N - No")];
    private static readonly (string, string)[] DataStatus = [("A", "A - Active"), ("D", "D - Delete")];
    private static readonly (string, string)[] StayType = [("I", "I - In-patient"), ("O", "O - Out-patient")];
    private static readonly (string, string)[] CashType = [("C", "C - Cashless"), ("R", "R - Reimbursement")];
    private static readonly (string, string)[] PayTo = [("H", "H - Hospital"), ("M", "M - Member")];

    /// <summary>The legacy "Selection Criteria" frame shared by Claim Report 3 and 4, for a claim-level source (VIEW_NEWCLMReport).</summary>
    private static List<TrackingFilter> ClaimReportFilters(params TrackingFilter[] extra) =>
    [
        F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", TextRange, "MBMNumber"), F("payor", "Payor", Exact, "PAYCode"),
        F("insurer", "INS type", Exact, "INSCode"), F("health", "HLT type", Exact, "HLTCode"), F("plan", "Plan code", TextRange, "PLNCode"),
        F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("agent", "Agent code", Contains, "MBMAgentCode"), F("group", "Group company", Contains, "MBMGroupCompany"),
        F("patient", "Patient name", Contains, "PatientName"), F("hospital", "Hospital", Contains, "HOSName"), F("hospitalGroup", "Hospital group", Contains, "HOSGrpName"),
        F("doctor", "Doctor", Contains, "AttendDoctor"), F("firstDiag", "Pre. diagnosis", TextRange, "FirstDiag"), F("finalDiag", "Final diagnosis", TextRange, "FinalDiag"),
        F("repCat", "Repudiation category", TextRange, "RepCatCode"), F("repCode", "Repudiation code", TextRange, "RepCode"), F("claimNature", "Claim nature", TextRange, "ClaimNatureCode"),
        F("status", "Case status", Choice, "CASStatus", CaseStatus), F("claimability", "Claimability", Choice, "CASClaimability", Claimability),
        F("policyStatus", "Policy status", Exact, "MBMStatus"), F("dataStatus", "Data status", Choice, "MBMDataStatus", DataStatus), F("expired", "Expired", Choice, "MBMExpiredStatus", YesNo),
        F("stayType", "Stay type", Choice, "CASStayType", StayType), F("payType", "Cash type", Choice, "CASPAYType", CashType), F("casePending", "Case pending", Choice, "CASPending", YesNo),
        F("claimPending", "Claim pending", Choice, "CLMPending", YesNo),
        F("admitted", "DOA", DateRange, "CASDateAdmitted"), F("discharged", "DOD", DateRange, "CASDischargeDate"), F("registered", "DORC", DateRange, "CaseRegDate"),
        F("effective", "Eff date", DateRange, "MBMPayorEffDate"), F("expiry", "Exp date", DateRange, "MBMPayorExpDate"), F("joined", "Date join", DateRange, "MBMFirstJoinedDate"),
        F("dob", "DOB", DateRange, "DOB"), F("bordxNo", "Bordx No", TextRange, "BordxRefNo"), F("bordxDate", "Bordx date", DateRange, "BordxDate"),
        .. extra,
    ];

    /// <summary>The same frame for a case-level source (main database VIEW_CAS_TRACKING, which names the doctor and repudiation columns differently).</summary>
    private static List<TrackingFilter> CaseReportFilters(params TrackingFilter[] extra) =>
    [
        F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", TextRange, "MBMNumber"), F("payor", "Payor", Exact, "PAYCode"),
        F("insurer", "INS type", Exact, "INSCode"), F("health", "HLT type", Exact, "HLTCode"), F("plan", "Plan code", TextRange, "PLNCode"),
        F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("agent", "Agent code", Contains, "MBMAgentCode"), F("group", "Group company", Contains, "MBMGroupCompany"),
        F("patient", "Patient name", Contains, "PatientName"), F("hospital", "Hospital", Contains, "HOSName"), F("doctor", "Doctor", Contains, "CASAttendDoctor1"),
        F("firstDiag", "Pre. diagnosis", TextRange, "FirstDiag"), F("finalDiag", "Final diagnosis", TextRange, "FinalDiag"), F("repCode", "Repudiation code", TextRange, "CASREPCode1"),
        F("claimNature", "Claim nature", TextRange, "ClaimNatureCode"), F("status", "Case status", Choice, "CASStatus", CaseStatus),
        F("claimability", "Claimability", Choice, "CASClaimability", Claimability), F("policyStatus", "Policy status", Exact, "MBMStatus"),
        F("dataStatus", "Data status", Choice, "MBMDataStatus", DataStatus), F("expired", "Expired", Choice, "MBMExpiredStatus", YesNo),
        F("stayType", "Stay type", Choice, "CASStayType", StayType), F("payType", "Cash type", Choice, "CASPAYType", CashType), F("casePending", "Case pending", Choice, "CASPending", YesNo),
        F("admitted", "DOA", DateRange, "CASDateAdmitted"), F("discharged", "DOD", DateRange, "CASDischargeDate"), F("registered", "DORC", DateRange, "CaseRegDate"),
        F("effective", "Eff date", DateRange, "MBMPayorEffDate"), F("expiry", "Exp date", DateRange, "PolicyExpDate"), F("dob", "DOB", DateRange, "DOB"),
        .. extra,
    ];

    /// <summary>The day-difference ranges of Claim Report 3 (legacy labels; days insured is expiry - effective date).</summary>
    private static readonly TrackingFilter[] CaseLagFilters =
    [
        F("dayInsured", "Days insured (Exp - Eff)", NumberRange, "DayInsured"), F("doaEff", "DOA - Eff date", NumberRange, "DOAEFF"),
        F("dorDoa", "DOR - DOA", NumberRange, "DORDOA"), F("expDoa", "Exp - DOA", NumberRange, "EXPDOA"), F("dpoDod", "DPO - DOD", NumberRange, "DPODOD"),
    ];

    public static readonly IReadOnlyList<TrackingDefinition> All =
    [
        new("case-tracking", "Case Tracking", "Case", 14, GenesisDb.Main, "VIEW_CAS_TRACKING", "CASNumber DESC",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", TextRange, "MBMNumber"), F("payor", "Payor", Exact, "PAYCode"),
                F("insurer", "Insurer", Exact, "INSCode"), F("health", "Health code", Exact, "HLTCode"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"),
                F("patient", "Patient name", Contains, "PatientName"), F("ic", "IC / BC / Passport", Exact, "ICNo"), F("hospital", "Hospital", Contains, "HOSName"),
                F("doctor", "Attending doctor", Contains, "CASAttendDoctor1"), F("group", "Group company", Contains, "MBMGroupCompany"),
                F("status", "Case status", Choice, "CASStatus", CaseStatus), F("claimability", "Claimability", Choice, "CASClaimability", Claimability),
                F("pending", "Case pending", Choice, "CASPending", ("Y", "Yes"), ("N", "No")), F("openBy", "Registered by", Exact, "CaseOpenBy"),
                F("registered", "Registered date", DateRange, "CaseRegDate"), F("admitted", "Admitted date", DateRange, "CASDateAdmitted"),
                F("discharged", "Discharged date", DateRange, "CASDischargeDate"), F("effective", "Payor effective date", DateRange, "MBMPayorEffDate"),
                F("expiry", "Payor expiry date", DateRange, "MBMPayorExpDate"), F("drd", "DRD", DateRange, "CASDRD"),
            ],
            [
                T("CASNumber", "Case No"), T("MBMNumber", "Member No"), T("PatientName", "Patient"), T("ICNo", "IC"), T("PAYCode", "Payor"), T("MBMPolicyNo", "Policy No"),
                T("HOSName", "Hospital"), D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"), D("CaseRegDate", "Registered"),
                T("CASStatus", "Status"), T("CASClaimability", "Claimability"), T("CASPending", "Pending"), N("NoofDay", "Days"), T("FinalDiag", "Final diagnosis"),
                M("CASReserveAmount", "Reserve"), M("CASInvAmt", "Invoice amount"), T("CaseOpenBy", "Registered by"), T("MBMGroupCompany", "Group company"),
            ]),

        new("lg-tracking", "LG Tracking", "Case", 17, GenesisDb.Main, "VIEW_LG_TRACKING", "LGDate DESC, CASENo",
            [
                F("caseNo", "Case No", Prefix, "CASENo"), F("lgNo", "LG No", Exact, "LGNo"), F("memberNo", "Member No", Prefix, "MBMNumber"),
                F("member", "Member name", Contains, "MBMName"), F("payor", "Payor", Exact, "PAYCode"), F("hospital", "Hospital", Contains, "HOSName"),
                F("issuedBy", "Issued by", Exact, "LGIssuedBy"), F("lgDate", "LG date", DateRange, "LGDate"), F("admitted", "Admitted date", DateRange, "CASDateAdmitted"),
            ],
            [T("CASENo", "Case No"), N("LGNo", "LG No"), D("LGDate", "LG date"), M("LGAmt", "Amount"), T("LGIssuedBy", "Issued by"), T("MBMNumber", "Member No"),
                T("PatientName", "Patient"), T("PAYCode", "Payor"), D("CASDateAdmitted", "Admitted"), T("HOSName", "Hospital")]),

        new("audit-review", "Audit Review Tracking", "Case", 16, GenesisDb.Main, "VIEW_ARReport", "CASAROpenDate DESC, CASNumber",
            [
                F("caseNo", "Case No", Prefix, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("hospital", "Hospital", Contains, "HOSName"),
                F("arStatus", "Audit review status", Exact, "CASARStatus"), F("opened", "AR open date", DateRange, "CASAROpenDate"),
                F("letterSent", "Letter sent", DateRange, "CASLetterSent"), F("reply", "Hospital reply", DateRange, "CASHospReply"),
            ],
            [T("CASNumber", "Case No"), T("MBMNumber", "Member No"), T("HOSName", "Hospital"), T("CASARStatus", "AR status"), D("CASAROpenDate", "AR opened"),
                D("CASLetterSent", "Letter sent"), D("CASHospReply", "Hospital reply"), M("CASInvAmt", "Invoice amount"), M("CASDisRec", "Discount received"),
                D("CASDRC", "DRC"), D("CASDTC", "DTC"), T("FinalICDCode", "Final ICD")]),

        new("mma-schedule", "MMA / CPT Schedule", "Case", 150, GenesisDb.Maintenance, "VIEW_MMA_Schedule", "OPCSCode",
            [
                F("opcs", "OPCS code", Prefix, "OPCSCode"), F("narrative", "Narrative", Contains, "OPCSNarrative"), F("cpt", "CPT code", Prefix, "CPTCode"),
                F("cptDesc", "CPT description", Contains, "CPTDesc"), F("main", "Main category", Contains, "MainCategory"), F("sub", "Sub category", Contains, "SubCategory"),
            ],
            [T("OPCSCode", "OPCS"), T("OPCSNarrative", "Narrative"), T("CPTCode", "CPT"), T("CPTDesc", "CPT description"), T("MainCategory", "Main category"),
                T("SubCategory", "Sub category"), M("SurgeonsFees", "Surgeon fees"), M("AnaesthetistsFees", "Anaesthetist fees"), N("Surgeon13th", "Surgeon 13th"),
                N("Anesthetist13th", "Anaesthetist 13th"), T("ProcedureDescription", "Procedure")]),

        new("invoice-tracking", "Invoice Tracking", "Invoice", 27, GenesisDb.Worksheet, "VIEW_Invoice_Tracking", "INVEnteredDate DESC, CASNumber",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("invNo", "Invoice No", Prefix, "InvNo"), F("payee", "Payee", Exact, "INVPayee"),
                F("payTo", "Pay to", Choice, "INVPayTo", ("H", "Hospital"), ("M", "Member")), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "Insurer", Exact, "INSCode"),
                F("memberNo", "Member No", Prefix, "MBMNumber"), F("member", "Member name", Contains, "MBMName"), F("patient", "Patient", Contains, "PatientName"),
                F("hospital", "Hospital", Contains, "HOSName"), F("group", "Group company", Contains, "MBMGroupCompany"),
                F("invDate", "Invoice date", DateRange, "InvDate"), F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("entered", "Entered date", DateRange, "INVEnteredDate"),
                F("admitted", "Admitted date", DateRange, "CASDateAdmitted"), F("discharged", "Discharged date", DateRange, "CASDischargeDate"),
            ],
            [T("CASNumber", "Case No"), T("INVWSversion", "WS ver"), T("InvNo", "Invoice No"), D("InvDate", "Invoice date"), T("INVPayee", "Payee"), T("INVPayTo", "Pay to"),
                M("INVAmount", "Amount"), T("INVBordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("MBMNumber", "Member No"), T("PatientName", "Patient"),
                T("PAYCode", "Payor"), T("HOSName", "Hospital"), D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"), T("INVEnteredBy", "Entered by"),
                D("INVEnteredDate", "Entered"), M("CLMTotalApproved", "Total approved"), T("FinalDiag", "Final diagnosis")]),

        new("main-dcn", "Main DCN Checking", "Invoice", 29, GenesisDb.Worksheet, "VIEW_MainDCN", "DebitCreditDate DESC, CASNumber",
            [
                F("caseNo", "Case No", Prefix, "CASNumber"), F("dcn", "DN/CN ref", Prefix, "DebitCreditRefNo"), F("dcnDate", "DN/CN date", DateRange, "DebitCreditDate"),
                F("payType", "Pay type", Exact, "CASPAYType"),
            ],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("DebitCreditRefNo", "DN/CN ref"), D("DebitCreditDate", "DN/CN date"), T("DebitCreditHRefNo", "Hospital DN/CN ref"),
                M("DebitCreditHAmt", "Hospital DN/CN amount"), M("CLMTotalApproved", "Approved"), M("CLMPayableByMedix2H", "Payable to hospital"),
                M("CLMPayableByMedix2M", "Payable to member"), M("CLMReimburse", "Reimburse"), T("CASPAYType", "Pay type")]),

        new("worksheet-tracking", "Worksheet Tracking", "Worksheet", 68, GenesisDb.Worksheet, "VIEW_WKSTrackingNew", "CASNumber DESC, ClaimVersion",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("version", "Claim version", Integer, "ClaimVersion"), F("payor", "Payor", Exact, "PAYCode"),
                F("insurer", "Insurer", Exact, "INSCode"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"),
                F("hospital", "Hospital", Contains, "HOSName"), F("patient", "Patient", Contains, "PatientName"), F("ic", "IC", Exact, "ICno"),
                F("bordxRef", "Bordx ref", Exact, "BordxRefNo"), F("dcn", "DN/CN ref", Prefix, "DebitCreditRefNo"), F("claimability", "Claimability", Choice, "CASClaimability", Claimability),
                F("claimStatus", "Claim status", Exact, "ClaimStatus"), F("pending", "Claim pending", Exact, "CLMPending"),
                F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("registered", "Case registered", DateRange, "CaseRegDate"), F("admitted", "Admitted", DateRange, "CASDateAdmitted"),
                F("discharged", "Discharged", DateRange, "CASDischargeDate"), F("opened", "Claim opened", DateRange, "ClaimOpenDate"), F("closed", "Claim closed", DateRange, "ClaimCloseDate"),
            ],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("PatientName", "Patient"), T("PAYCode", "Payor"), T("HOSName", "Hospital"),
                D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"), M("CLMTotalApproved", "Approved"), M("CLMPayableByMedix2H", "Payable to hospital"),
                M("CLMPayableByMedix2M", "Payable to member"), T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), M("BordxClaimAmt", "Bordx amount"),
                T("DebitCreditRefNo", "DN/CN ref"), T("ClaimStatus", "Claim status"), T("CLMPending", "Pending"), D("ClaimOpenDate", "Opened"), D("ClaimCloseDate", "Closed")]),

        new("bordx-worksheet", "Bordx Worksheet", "Claim Bordx", 42, GenesisDb.Worksheet, "VIEW_Bordx_Wks2", "BordxDate DESC, BordxRefNo, CASNumber",
            [
                F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "BordxPAYCode"), F("caseNo", "Case No", Prefix, "CASNumber"),
                F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("member", "Member name", Contains, "MBMName"), F("status", "Bordx status", Exact, "BordxStatus"),
                F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("mnrbRec", "MNRB received", DateRange, "BordxMNRBRecDate"), F("payRec", "Payor received", DateRange, "BordxPAYRecDate"),
            ],
            [T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("BordxPAYCode", "Payor"), T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMName", "Member"),
                T("MBMPolicyNo", "Policy No"), M("BordxClaimAmt", "Bordx amount"), M("CLMTotalApproved", "Approved"), M("CLMPayableByMedix2H", "Payable to hospital"),
                T("BordxStatus", "Status"), D("BordxMNRBRecDate", "MNRB received"), D("BordxPAYRecDate", "Payor received"), T("ReceiptNoPAY", "Payor receipt"),
                M("RecPAYAmt", "Payor received amt"), T("ReceiptNoMNRB", "MNRB receipt"), M("RecMNRBAmt", "MNRB received amt"), T("DIAFinalDiag1", "Final diagnosis")]),

        new("outstanding-bordx", "Outstanding Bordx", "Claim Bordx", 41, GenesisDb.Worksheet, "VIEW_OS_Bordx", "BordxRefNo",
            [
                F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "PAYCode"), F("payorName", "Payor name", Contains, "PAYCompanyName"),
                F("payorSent", "Sent to payor", DateRange, "BordxPAYSentDate"), F("mnrbSent", "Sent to MNRB", DateRange, "BordxMNRBSentDate"),
            ],
            [T("BordxRefNo", "Bordx ref"), T("PAYCode", "Payor"), T("PAYCompanyName", "Payor name"), T("HLTCode", "Health"), N("BordxNoClaims", "Claims"), M("BordxClaimAmt", "Amount"),
                D("BordxPAYSentDate", "Sent to payor"), D("BordxMNRBSentDate", "Sent to MNRB"), T("BordxPAYFRStatus", "Payor FR status"), T("BordxMNRBFRStatus", "MNRB FR status")]),

        new("bordx-control", "Bordx Control (Normal)", "Claim Bordx", 40, GenesisDb.Worksheet, "VIEW_Bordx_ControlN2", "BordxDate DESC, BordxRefNo",
            [F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "BordxPAYCode"), F("health", "Health", Exact, "HLTCode"), F("bordxDate", "Bordx date", DateRange, "BordxDate"),
                F("sent", "Sent to payor", DateRange, "BordxPAYSentDate"), F("received", "Received from payor", DateRange, "BordxPAYRecDate")],
            [T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("BordxPAYCode", "Payor"), T("HLTCode", "Health"), N("BordxNoClaims", "Claims"), M("BordxClaimAmt", "Amount"),
                M("CLMCaseMgmtFees", "Case mgmt fees"), D("BordxPAYSentDate", "Sent"), D("BordxPAYRecDate", "Received"), D("BordxPAYRecDocDate", "Documents received"),
                T("BordxPAYToStatus", "To status"), T("BordxPAYFRStatus", "From status"), T("BordxStatus", "Status")]),

        new("bordx-control-supp", "Bordx Control (Supplementary)", "Claim Bordx", 40, GenesisDb.Worksheet, "VIEW_Bordx_ControlS2", "BordxDate DESC, BordxRefNo",
            [F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "BordxPAYCode"), F("health", "Health", Exact, "HLTCode"), F("bordxDate", "Bordx date", DateRange, "BordxDate"),
                F("payorSent", "Sent to payor", DateRange, "BordxPAYSentDate"), F("mnrbSent", "Sent to MNRB", DateRange, "BordxMNRBSentDate")],
            [T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("BordxPAYCode", "Payor"), T("HLTCode", "Health"), N("BordxNoClaims", "Claims"), M("BordxClaimAmt", "Amount"),
                D("BordxPAYSentDate", "Sent to payor"), D("BordxMNRBSentDate", "Sent to MNRB"), D("BordxMNRBRecDate", "MNRB received"), T("BordxMNRBToStatus", "MNRB to status"),
                T("BordxMNRBFRStatus", "MNRB from status"), T("BordxPAYFRStatus", "Payor from status"), T("BordxStatus", "Status")]),

        new("storage-enquiry", "Storage Enquiry", "Claim Bordx", 49, GenesisDb.Worksheet, "View_Storage_CLm", "LastUpdateDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("carton", "Carton No", Exact, "STRCartonNo"), F("fileStatus", "File status", Exact, "STRFileStatus"),
                F("requestedBy", "Requested by", Exact, "STRRequestBy"), F("requested", "Request date", DateRange, "DORequest"), F("updated", "Last update", DateRange, "LastUpdateDate")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("STRCartonNo", "Carton No"), T("CRTCartonStatus", "Carton status"), T("STRFileStatus", "File status"),
                T("STRRequestBy", "Requested by"), D("DORequest", "Requested"), D("DODelivery", "Delivered"), D("DOReturn", "Returned"), T("STRRemarks", "Remarks"),
                T("LastUpdateBy", "Updated by"), D("LastUpdateDate", "Updated"), M("CLMTotalActual", "Total actual")]),

        new("storage-repudiated", "Storage Enquiry (Repudiated)", "Claim Bordx", 152, GenesisDb.Worksheet, "View_Storage_CLmRep", "LastUpdateDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("carton", "Carton No", Exact, "STRCartonNo"), F("fileStatus", "File status", Exact, "STRFileStatus"),
                F("requestedBy", "Requested by", Exact, "STRRequestBy"), F("requested", "Request date", DateRange, "DORequest"), F("registered", "Case registered", DateRange, "CaseRegDate")],
            [T("CASNumber", "Case No"), T("STRCartonNo", "Carton No"), T("CRTCartonStatus", "Carton status"), T("STRFileStatus", "File status"), T("STRRequestBy", "Requested by"),
                D("DORequest", "Requested"), D("DODelivery", "Delivered"), D("DOReturn", "Returned"), T("STRRemarks", "Remarks"), T("CASClaimability", "Claimability"),
                D("CaseRegDate", "Case registered"), T("CaseOpenBy", "Opened by"), T("LastUpdateBy", "Updated by"), D("LastUpdateDate", "Updated")]),

        new("receipt-member", "Receipt Tracking (From Member)", "Receipt", 63, GenesisDb.Payment, "VIEW_ReceiptfrMBMTracking", "ReceiptDate DESC, ReceiptNo",
            ReceiptFilters(), [T("CASNumber", "Case No"), T("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("MBMName", "Member"), T("PAYCode", "Payor"), T("ReceiptNo", "Receipt No"),
                D("ReceiptDate", "Receipt date"), M("ReceiptAmt", "Amount"), T("DebitCreditRefNo", "DN/CN ref"), T("BordxNo", "Bordx"), T("PVRecMBMStatus", "Status"),
                N("RECMBMAccPeriodYr", "Acc year"), N("RECMBMAccPeriodMth", "Acc month"), T("ReceiptRemarks", "Remarks")]),

        new("receipt-payor", "Receipt Tracking (From Payor)", "Receipt", 63, GenesisDb.Payment, "VIEW_ReceiptfrPayorTracking", "ReceiptDate DESC, ReceiptNo",
            ReceiptFilters(), [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("MBMName", "Member"), T("PAYCode", "Payor"), T("ReceiptNo", "Receipt No"),
                D("ReceiptDate", "Receipt date"), M("ReceiptAmt", "Amount"), M("RecPAYAmt", "Payor amount"), M("CheckAmt", "Cheque amount"), T("DebitCreditRefNo", "DN/CN ref"),
                T("BordxNo", "Bordx"), N("RECAccPeriodYR", "Acc year"), N("RECAccPeriodMth", "Acc month"), T("Remark", "Remark")]),

        new("receipt-mnrb", "Receipt Tracking (From MNRB)", "Receipt", 63, GenesisDb.Payment, "VIEW_ReceiptfrMNRBTracking", "ReceiptDate DESC, ReceiptNo",
            ReceiptFilters(), [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("MBMName", "Member"), T("PAYCode", "Payor"), T("ReceiptNo", "Receipt No"),
                D("ReceiptDate", "Receipt date"), M("ReceiptAmt", "Amount"), M("RecMNRBAmt", "MNRB amount"), M("CheckAmt", "Cheque amount"), T("DebitCreditRefNo", "DN/CN ref"),
                T("BordxNo", "Bordx"), N("RECAccPeriodYR", "Acc year"), N("RECAccPeriodMth", "Acc month"), T("Remark", "Remark")]),

        new("payment-member", "Payment Enquiry (To Member)", "Payment", 146, GenesisDb.Payment, "Payment2MBM", "PVDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("pvNo", "PV No", Prefix, "PVNo"), F("cheque", "Cheque No", Exact, "PVChequeNo"), F("pvDate", "PV date", DateRange, "PVDate"),
                F("chequeDate", "Cheque date", DateRange, "PVChequeDate"), F("status", "Status", Exact, "PVStatus")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PVNo", "PV No"), D("PVDate", "PV date"), T("PVChequeNo", "Cheque No"), D("PVChequeDate", "Cheque date"),
                M("MBMPaidAmt", "Paid amount"), T("PVStatus", "Status"), T("PVPayMBMStatus", "Payment status"), T("Remark", "Remark")]),

        new("bordx-tracking", "Bordx Tracking", "Claim Bordx", 43, GenesisDb.Worksheet, "View_BordxTracking2", "BordxDate DESC, BordxRefNo",
            [F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "BordxPAYCode"), F("health", "Health", Exact, "HLTCode"), F("status", "Bordx status", Exact, "BordxStatus"),
                F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("sent", "Sent to payor", DateRange, "BordxPAYSentDate"), F("received", "Received from payor", DateRange, "BordxPAYRecDate"),
                F("sentAc", "Sent to A/C", DateRange, "BordxSendACDate")],
            [T("BordxRefNo", "Bordx ref"), T("BordxPAYCode", "Payor"), T("HLTCode", "Health"), D("BordxDate", "Bordx date"), M("BordxClaimAmt", "Amount"), M("CLMCaseMgmtFees", "Case mgmt fees"),
                T("BordxStatus", "Status"), D("BordxMNRBSentDate", "Sent to MNRB"), D("BordxPAYSentDate", "Sent to payor"), D("BordxPAYRecDate", "Received from payor"),
                D("BordxSendACDate", "Sent to A/C"), T("PAYToACPeriodYR", "A/C year"), T("PAYToACPeriodMth", "A/C month"), T("ClaimStatus", "Claim status")]),

        new("monthly-dn", "Monthly DN Report", "Claim Bordx", 16, GenesisDb.Worksheet, "VIEW_Monthly_DNRpt", "DebitCreditDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("dcn", "DN/CN ref", Prefix, "DebitCreditRefNo"), F("payor", "Payor", Exact, "PAYCode"), F("enteredBy", "Invoice entered by", Exact, "INVEnteredBy"),
                F("dcnDate", "DN/CN date", DateRange, "DebitCreditDate"), F("entered", "Invoice entered", DateRange, "INVEnteredDate"), F("receipt", "Receipt date", DateRange, "ReceiptDate")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("DebitCreditRefNo", "DN/CN ref"), D("DebitCreditDate", "DN/CN date"), M("CLMTotalActual", "Total actual"), M("CLMTotalApproved", "Total approved"),
                M("CLMPayableByMedix2M", "Payable to member"), T("PatientName", "Patient"), T("PAYCode", "Payor"), T("INVEnteredBy", "Entered by"), D("INVEnteredDate", "Entered"),
                D("ReceiptDate", "Receipt date"), M("ReceiptAmt", "Receipt amount"), T("CaseOpenBy", "Case opened by"), T("ClaimOpenBy", "Claim opened by")]),

        new("print-bordx", "Bordx Listing (Print Bordx)", "Claim Bordx", 32, GenesisDb.Worksheet, "View_PrintBordx", "BordxDate DESC, BordxRefNo, CASNumber",
            [F("bordxRef", "Bordx ref", Prefix, "BordxRefNo"), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "Insurer", Exact, "INSCode"), F("plan", "Plan", Exact, "PLNCode"),
                F("caseNo", "Case No", Prefix, "CASNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("hospital", "Hospital", Contains, "HOSName"), F("bordxDate", "Bordx date", DateRange, "BordxDate")],
            [T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("PatientName", "Patient"),
                T("MBMPolicyNo", "Policy No"), T("PAYCode", "Payor"), T("HOSName", "Hospital"), D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"),
                M("CLMTotalActual", "Total actual"), M("CLMTotalApproved", "Total approved"), M("CLMPayableByMedix2H", "Payable to hospital"), M("CLMPayableByMedix2M", "Payable to member"),
                M("CLMCaseMgmtFees", "Case mgmt fees"), T("DebitCreditRefNo", "DN/CN ref"), T("ClaimID", "Claim ID")],
            "Print Bordx prints the bordereaux found (legacy FrmPrintBordx details, BordxDetailsTemplate.xlt): one section per bordereau with the payor, its claims, " +
            "hospital charges, medical report fee and total.",
            RequiresFilter: true, Prints: [new("bordx-details", "Print Bordx")]),

        new("recovery-report", "Recovery Report", "Claim Bordx", 32, GenesisDb.Worksheet, "VIEW_NEWCLMReport", "BordxDate DESC, CASNumber",
            [F("caseNo", "Case No", TextRange, "CASNumber"), F("payor", "Payor", Exact, "PAYCode"), F("hospital", "Hospital", Contains, "HOSName"), F("bordxRef", "Bordx ref", Exact, "BordxRefNo"),
                F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("admitted", "Admitted", DateRange, "CASDateAdmitted")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PatientName", "Patient"), T("PAYCode", "Payor"), T("HOSName", "Hospital"), D("CASDateAdmitted", "Admitted"),
                M("CLMTotalApproved", "Total approved"), M("CLMRecoveryPaidByM", "Recovery"), T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date")],
            RequiresFilter: true, FixedWhere: "CLMRecoveryPaidByM <> 0"),

        new("panel-hospitals", "Panel Hospitals", "Case", 3, GenesisDb.Main, "HOS INNER JOIN PanelHospitals ON HOS.HOSCode = PanelHospitals.HOSCode", "HOS.HOSName",
            [F("panel", "Panel code", Prefix, "PanelHospitals.HosPanelCode"), F("hospital", "Hospital name", Contains, "HOS.HOSName"), F("code", "Hospital code", Prefix, "HOS.HOSCode"),
                F("state", "State", Contains, "HOS.HOSState"), F("town", "Town", Contains, "HOS.HOSTown")],
            [T("PanelHospitals.HosPanelCode", "Panel code"), T("HOS.HOSCode", "Hospital code"), T("HOS.HOSName", "Hospital"), T("HOS.HOSTelNo", "Telephone"), T("HOS.HOSFaxNo", "Fax"),
                T("HOS.HOSAdd1", "Address 1"), T("HOS.HOSAdd2", "Address 2"), T("HOS.HOSAdd3", "Address 3"), T("HOS.HOSTown", "Town"), T("HOS.HOSState", "State")]),
        new("member-tracking", "Membership Tracking", "Membership", 6, GenesisDb.Main, "VIEW_MBM_Search2", "MBMNumber",
            [
                F("memberNo", "Member No", Prefix, "MBMNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"), F("ic", "IC / BC / Passport", Exact, "MBMIcBcPp"),
                F("name", "Name", Contains, "MBMName"), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "Insurer", Exact, "INSCode"), F("plan", "Plan", Exact, "PLNCode"),
                F("health", "Health code", Exact, "HLTCode"), F("status", "Status", Exact, "MBMStatus"), F("group", "Group company", Contains, "MBMGroupCompany"),
                F("agent", "Agent code", Exact, "MBMAgentCode"), F("employee", "Employee No", Exact, "MBMEmployeeNo"),
                F("effective", "Payor effective", DateRange, "MBMPayorEffDate"), F("expiry", "Payor expiry", DateRange, "MBMPayorExpDate"), F("bordx", "Bordx date", DateRange, "MBMBordxDate"),
                F("registered", "Registered", DateRange, "MBMRegDate"), F("dob", "Date of birth", DateRange, "MBMDOB"),
            ],
            [T("MBMNumber", "Member No"), T("MBMCCoverID", "Cover"), T("MBMName", "Name"), T("MBMIcBcPp", "IC / BC / PP"), T("MBMPolicyNo", "Policy No"), T("PAYCode", "Payor"),
                T("INSCode", "Insurer"), T("PLNCode", "Plan"), T("HLTCode", "Health"), T("MBMStatus", "Status"), D("MBMPayorEffDate", "Payor effective"), D("MBMPayorExpDate", "Payor expiry"),
                D("MBMBordxDate", "Bordx date"), N("MBMBatchNo", "Batch"), T("MBMGroupCompany", "Group company"), M("MBMPremium", "Premium"), M("MBMMCOFee", "MCO fee"),
                T("MBMAgentCode", "Agent"), T("MBMEmployeeNo", "Employee No")],
            RequiresFilter: true),

        new("member-tracking-simple", "Membership Tracking (Simple): Listing", "Membership", 6, GenesisDb.Main, "VIEW_MBM_Search2_Simple", "MBMNumber",
            MemberSimpleFilters(listing: true),
            [T("MBMNumber", "Member No"), T("MBMName", "Name"), T("MBMIcBcPp", "IC / BC / PP"), T("MBMPolicyNo", "Policy No"), T("PAYCode", "Payor"), T("INSCode", "INS"),
                T("HLTCode", "HLT"), T("PLNCode", "Plan"), T("ProductName", "Product"), T("MBMStatus", "Status"), T("MBMSex", "Sex"), T("AGECode", "Age band"), T("RACCode", "Race"),
                D("MBMDOB", "DOB"), T("STACode", "State"), T("MBMGroupCompany", "Group company"), T("MBMEmployeeNo", "Employee No"), T("MBMAgentCode", "Agent"),
                D("MBMRegDate", "Registered"), D("MBMPayorEffDate", "Eff date"), D("MBMBordxDate", "Bordx date"), N("MBMBatchNo", "Batch"), T("MBMRenewFlag", "Renewal"),
                T("MBMInstallment", "Premium mode"), M("MBMPremium", "Premium"), M("MBMMCOFee", "MCO fee"), T("MBMInvoiceNo", "Invoice No"), D("MBMInvoiceDate", "Invoice date"),
                N("AgeDays", "Age (days)"), N("DelayReg", "Delay eff-reg"), N("DelayBordx", "Delay eff-bordx")],
            "Legacy Membership > Tracking (Simple) > Detail / General Listing. The summaries of the same form are on Membership Tracking (Simple): Summary.",
            RequiresFilter: true),

        new("member-labels", "Membership Tracking (Simple): Print Label", "Membership", 6, GenesisDb.Main, "MemberLabel", "MBMNumber",
            MemberSimpleFilters(listing: true),
            [T("MBMNumber", "Member No"), T("MBMPolicyNo", "Policy No"), T("MBMName", "Member Name"), T("MBMAddress1", "Address 1"), T("MBMAddress2", "Address 2"),
                T("MBMAddress3", "Address 3"), T("MBMPostCode", "Post Code"), T("CTYCode", "City"), T("STACode", "State"), T("MBMGroupCompany", "Group Company")],
            "Legacy Membership > Tracking (Simple) > General Listing > Print Label: the members' mailing addresses. Print Labels makes an A4 sheet of address labels " +
            "(3 x 7, 63.5 x 38.1 mm) for the members found; members without an address are left off the sheet.",
            RequiresFilter: true, SourceQuery: MemberLabelSource, Prints: [new("member-labels", "Print Labels")]),

        new("claim-report", "Claim Report (Simple)", "Claim", 44, GenesisDb.Worksheet, "VIEW_NEWCLMReport", "CASNumber DESC, ClaimVersion",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("policyNo", "Policy No", Contains, "MBMPolicyNo"),
                F("patient", "Patient", Contains, "PatientName"), F("hospital", "Hospital", Contains, "HOSName"), F("payor", "Payor", Exact, "PAYCode"), F("insurer", "Insurer", Exact, "INSCode"),
                F("plan", "Plan", Exact, "PLNCode"), F("group", "Group company", Contains, "MBMGroupCompany"), F("claimStatus", "Claim status", Exact, "ClaimStatus"),
                F("claimability", "Claimability", Choice, "CASClaimability", Claimability), F("bordxRef", "Bordx ref", Exact, "BordxRefNo"),
                F("admitted", "Admitted", DateRange, "CASDateAdmitted"), F("discharged", "Discharged", DateRange, "CASDischargeDate"), F("opened", "Claim opened", DateRange, "ClaimOpenDate"),
                F("bordxDate", "Bordx date", DateRange, "BordxDate"), F("registered", "Case registered", DateRange, "CaseRegDate"),
            ],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("PatientName", "Patient"), T("MBMPolicyNo", "Policy No"), T("PAYCode", "Payor"),
                T("PLNCode", "Plan"), T("HOSName", "Hospital"), D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"), N("NoofDay", "Days"), T("FinalDiag", "Final diagnosis"),
                T("CASClaimability", "Claimability"), M("CLMTotalActual", "Total actual"), M("CLMTotalApproved", "Total approved"), M("CLMPayableByMedix2H", "Payable to hospital"),
                M("CLMPayableByMedix2M", "Payable to member"), T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date"), T("ClaimStatus", "Claim status"), T("MBMGroupCompany", "Group company")],
            RequiresFilter: true),

        new("claim-report3-invoice", "Claim Report 3: Dates by Invoice", "Claim", 46, GenesisDb.Worksheet, "InvoiceLag", "CASNumber DESC, ClaimVersion, InvNo",
            ClaimReportFilters([.. CaseLagFilters,
                F("invDorInv", "Inv DOR - Inv date", NumberRange, "INVDORINV"), F("invDod", "Inv date - DOD", NumberRange, "INVDOD"),
                F("preDoa", "PreHosp - DOA", NumberRange, "PREDOA"), F("folDod", "FolUp - DOD", NumberRange, "FOLDOD"), F("payTo", "Pay to", Choice, "InvoicePayTo", PayTo)]),
            [T("InvNo", "Invoice No"), T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PatientName", "Patient"), T("PAYCode", "Payor"), N("INVDORINV", "Inv DOR - Inv date"),
                N("INVDOD", "Inv date - DOD"), N("PREDOA", "PreHosp - DOA"), N("FOLDOD", "FolUp - DOD"), D("CASDateAdmitted", "DOA"), D("CASDischargeDate", "DOD"),
                D("InvDate", "Invoice date"), D("INVEnteredDate", "Invoice entered"), D("INVFolUpDate", "Follow-up date"), T("InvoicePayTo", "Pay to"), T("BordxRefNo", "Bordx No")],
            "Legacy Claim Report 3 > Date > By Invoice No: days between the invoice, admission, discharge and follow-up dates of each worksheet invoice.",
            RequiresFilter: true, SourceQuery: InvoiceLagSource),

        new("claim-report3-case", "Claim Report 3: Dates by Case", "Claim", 46, GenesisDb.Main, "CaseLag", "CASNumber DESC",
            CaseReportFilters(CaseLagFilters),
            [T("CASNumber", "Case No"), T("MBMNumber", "Member No"), T("PatientName", "Patient"), T("PAYCode", "Payor"), N("DayInsured", "Days insured"), N("DOAEFF", "DOA - Eff date"),
                N("DORDOA", "DOR - DOA"), N("DPODOD", "DPO - DOD"), N("EXPDOA", "Exp - DOA"), D("CASDateAdmitted", "DOA"), D("CASDischargeDate", "DOD"), D("CaseRegDate", "DORC"),
                D("CASTokenDate", "Token date"), D("MBMPayorEffDate", "Eff date"), D("PolicyExpDate", "Exp date")],
            "Legacy Claim Report 3 > Date > By Case No: days between the policy, admission, registration and discharge dates of each case (with or without a worksheet).",
            RequiresFilter: true, SourceQuery: CaseLagSource),

        new("claim-report3-claim", "Claim Report 3: Dates by Claim", "Claim", 46, GenesisDb.Worksheet, "ClaimLag", "CASNumber DESC, ClaimVersion",
            ClaimReportFilters([.. CaseLagFilters, F("bordDod", "Bordx - DOD", NumberRange, "BORDOD")]),
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PatientName", "Patient"), T("PAYCode", "Payor"), N("DayInsured", "Days insured"), N("DOAEFF", "DOA - Eff date"),
                N("DORDOA", "DOR - DOA"), N("DPODOD", "DPO - DOD"), N("BORDOD", "Bordx - DOD"), N("EXPDOA", "Exp - DOA"), D("CASDateAdmitted", "DOA"), D("CASDischargeDate", "DOD"),
                D("CaseRegDate", "DORC"), D("CASTokenDate", "Token date"), D("BordxDate", "Bordx date"), D("MBMPayorEffDate", "Eff date"), D("MBMPayorExpDate", "Exp date"),
                T("BordxRefNo", "Bordx No")],
            "Legacy Claim Report 3 > Date > By Claim No: the case day differences plus discharge to bordereau, for each worksheet version.",
            RequiresFilter: true, SourceQuery: ClaimLagSource),

        new("claim-report3-value", "Claim Report 3: Claim Value Control", "Claim", 46, GenesisDb.Worksheet, "InvoiceLag", "CASNumber DESC, ClaimVersion, InvNo",
            ClaimReportFilters([.. CaseLagFilters, F("payTo", "Pay to", Choice, "InvoicePayTo", PayTo), F("closed", "Claim close date", DateRange, "ClaimCloseDate")]),
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("InvNo", "Invoice No"), T("PatientName", "Patient"), T("PAYCode", "Payor"), D("ClaimCloseDate", "Close date"),
                M("INVAmount", "Invoice amount"), M("CLMExcess", "Excess"), M("InvPaidByMem", "Paid by member"), M("CLMPayableByMedix2H", "Payable to hospital"),
                M("CLMReimburse", "Reimburse"), M("CLMTotalActual", "Total actual"), T("InvoicePayTo", "Pay to")],
            "Legacy Claim Report 3 > CLM Value Control: invoice amount against the worksheet's excess, member share, hospital payable and reimbursement.",
            RequiresFilter: true, SourceQuery: InvoiceLagSource),

        new("claim-report4-worksheets", "Claim Report 4: Cases & Worksheets", "Claim", 47, GenesisDb.Worksheet, "VIEW_NEWCLMReport", "CASNumber DESC, ClaimVersion",
            ClaimReportFilters(),
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("MBMPolicyNo", "Policy No"), T("Relation", "Relation"), T("MBMName", "Member"),
                T("PatientName", "Patient"), T("Sex", "Sex"), T("Ageband", "Age band"), T("ICNo", "IC No"), T("RACCode", "Race"), T("HOSName", "Hospital"),
                T("FirstDiag", "Pre. diagnosis"), T("FinalDiag", "Final diagnosis"), T("CASClaimability", "Claimability"), T("CASStatus", "Status"), D("CASDateAdmitted", "DOA"),
                D("CASDischargeDate", "DOD"), D("MBMPayorEffDate", "Eff date"), D("MBMPayorExpDate", "Exp date"), M("CLMTotalActual", "Total actual"),
                M("CLMRecoveryPaidByM", "Recovery"), M("CLMPayableByMedix2H", "Payable to hospital"), M("CASReserveAmount", "Reserve"), T("CASAdmissionReason", "Admission reason"),
                T("CASUDReasons1", "Underlying diagnosis"), D("DOB", "DOB"), D("CaseRegDate", "DORC")],
            "Legacy Claim Report 4 > General Listing > Cases & Worksheets.", RequiresFilter: true),

        new("claim-report4-no-worksheet", "Claim Report 4: Cases without Worksheets", "Claim", 47, GenesisDb.Main, "CaseList", "CASNumber DESC",
            CaseReportFilters(),
            [T("CASNumber", "Case No"), T("MBMNumber", "Member No"), T("MBMPolicyNo", "Policy No"), T("MBMPatientCovID", "Cov ID"), T("MBMName", "Member"), T("PatientName", "Patient"),
                T("MBMSex", "Member sex"), T("AGECode", "Age code"), T("Sex", "Sex"), T("AGEBand", "Age band"), T("INSCode", "INS"), T("PLNCode", "Plan"), T("HOSName", "Hospital"),
                T("FirstDiag", "Pre. diagnosis"), T("FinalDiag", "Final diagnosis"), T("CASClaimability", "Claimability"), T("CASStatus", "Status"), T("MBMStatus", "Policy status"),
                D("CASDateAdmitted", "DOA"), D("CASDischargeDate", "DOD"), D("MBMPayorEffDate", "Eff date"), D("PolicyExpDate", "Exp date"), D("CaseRegDate", "DORC"),
                N("NoofDay", "Days"), T("CASPAYType", "Cash type"), T("CASPending", "Pending"), T("MBMDataStatus", "Data status")],
            "Legacy Claim Report 4 > General Listing > Cases without Worksheets (both versions): registered cases that have no worksheet yet.",
            FixedWhere: "NOT EXISTS (SELECT 1 FROM FWHSHISWorksheet.dbo.Claim k WHERE k.CASNumber = CaseList.CASNumber)", SourceQuery: CaseSource),

        new("dcu-report", "DCU Claim Report", "Claim", 155, GenesisDb.Worksheet, "VIEW_CLMReportDCU", "CASNumber DESC, ClaimVersion",
            [
                F("caseNo", "Case No", TextRange, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("patient", "Patient", Contains, "PatientName"),
                F("hospital", "Hospital", Contains, "HOSName"), F("payor", "Payor", Exact, "PAYCode"), F("group", "Group company", Contains, "MBMGroupCompany"),
                F("admitted", "Admitted", DateRange, "CASDateAdmitted"), F("discharged", "Discharged", DateRange, "CASDischargeDate"), F("bordxDate", "Bordx date", DateRange, "BordxDate"),
            ],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("PatientName", "Patient"), T("PAYCode", "Payor"), T("HOSName", "Hospital"),
                D("CASDateAdmitted", "Admitted"), D("CASDischargeDate", "Discharged"), T("FinalDiag", "Final diagnosis"), M("CLMTotalActual", "Total actual"),
                M("CLMTotalApproved", "Total approved"), M("RecoveryAmt", "Recovery"), M("CLMPayableByMedix2M", "Payable to member"), T("BordxRefNo", "Bordx ref"), D("BordxDate", "Bordx date")]),
    ];

    private static List<TrackingFilter> ReceiptFilters() =>
    [
        F("caseNo", "Case No", Prefix, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("member", "Member name", Contains, "MBMName"), F("payor", "Payor", Exact, "PAYCode"),
        F("receiptNo", "Receipt No", Prefix, "ReceiptNo"), F("dcn", "DN/CN ref", Prefix, "DebitCreditRefNo"), F("receiptDate", "Receipt date", DateRange, "ReceiptDate"),
    ];

    public static TrackingDefinition? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}





