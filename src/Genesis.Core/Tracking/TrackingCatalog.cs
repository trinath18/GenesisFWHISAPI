using Genesis.Core.Data;
using static Genesis.Core.Tracking.TrackingFilterKind;

namespace Genesis.Core.Tracking;

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

        new("payment-tracking", "Payment Tracking (Hospital)", "Payment", 58, GenesisDb.Payment, "VIEW_PaymentHos_Tracking", "PVDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("memberNo", "Member No", Prefix, "MBMNumber"), F("hospital", "Hospital", Contains, "HOSName"), F("payor", "Payor", Exact, "PAYCode"),
                F("pvNo", "PV No", Prefix, "PVNo"), F("bordxRef", "Bordx ref", Exact, "BordxRefNo"), F("pvDate", "PV date", DateRange, "PVDate"), F("status", "Payment status", Exact, "PVPayHOSStatus")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("MBMNumber", "Member No"), T("MBMName", "Member"), T("HOSName", "Hospital"), T("PAYCode", "Payor"),
                T("PVNo", "PV No"), D("PVDate", "PV date"), M("PVPaidAmt", "Paid amount"), T("PVPayHOSStatus", "Status"), T("BordxRefNo", "Bordx ref"),
                N("AccPeriodYr", "Acc year"), N("AccPeriodMth", "Acc month"), T("Remark", "Remark")]),

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

        new("payment-hospital", "Payment Enquiry (To Hospital)", "Payment", 145, GenesisDb.Payment, "Payment2HOS", "PVHOSDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("pvNo", "PV No", Prefix, "PVHOSNo"), F("cheque", "Cheque No", Exact, "PVHOSChequeNo"), F("pvDate", "PV date", DateRange, "PVHOSDate"),
                F("chequeDate", "Cheque date", DateRange, "PVHOSChequeDate"), F("status", "Status", Exact, "PVStatus")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PVHOSNo", "PV No"), D("PVHOSDate", "PV date"), T("PVHOSChequeNo", "Cheque No"), D("PVHOSChequeDate", "Cheque date"),
                M("HOSPaidAmt", "Paid amount"), T("PVStatus", "Status"), T("PVPayHOSStatus", "Payment status"), T("HOSRemark", "Remark")]),

        new("payment-member", "Payment Enquiry (To Member)", "Payment", 146, GenesisDb.Payment, "Payment2MBM", "PVDate DESC, CASNumber",
            [F("caseNo", "Case No", Prefix, "CASNumber"), F("pvNo", "PV No", Prefix, "PVNo"), F("cheque", "Cheque No", Exact, "PVChequeNo"), F("pvDate", "PV date", DateRange, "PVDate"),
                F("chequeDate", "Cheque date", DateRange, "PVChequeDate"), F("status", "Status", Exact, "PVStatus")],
            [T("CASNumber", "Case No"), N("ClaimVersion", "Ver"), T("PVNo", "PV No"), D("PVDate", "PV date"), T("PVChequeNo", "Cheque No"), D("PVChequeDate", "Cheque date"),
                M("MBMPaidAmt", "Paid amount"), T("PVStatus", "Status"), T("PVPayMBMStatus", "Payment status"), T("Remark", "Remark")]),

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



