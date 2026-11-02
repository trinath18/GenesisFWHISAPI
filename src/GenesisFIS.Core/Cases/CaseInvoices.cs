using System.Text.Json.Serialization;

namespace GenesisFIS.Core.Cases;

/// <summary>Which worksheet-database table an invoice amount lives in (legacy procedures Insert_ClaimInvActual2010, Insert_ClaimInvoiceActOthers_New, Insert_ClaimInvoiceNotCov08).</summary>
// Sent by name ("NotCovered", "Computed"), as the UI's InvoiceField type expects.
[JsonConverter(typeof(JsonStringEnumConverter<InvoiceTable>))]
public enum InvoiceTable { Actual, ActualOthers, NotCovered }

/// <summary>Entered: a money amount. Days: a count (days, months). Computed: worked out from other fields, as the legacy form did on LostFocus.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvoiceFieldKind>))]
public enum InvoiceFieldKind { Amount, Days, Computed }

/// <summary>One amount box of the legacy Invoice Processing form (frmInvoice). <see cref="InTotal"/> boxes add up to the invoice amount (legacy getTotal).</summary>
public sealed record InvoiceField(string Key, InvoiceTable Table, string Column, string Label, string Group, InvoiceFieldKind Kind = InvoiceFieldKind.Amount, bool InTotal = false);

/// <summary>A "charged to member" tick box of the Others frame (ClaimInvoiceActualOthers, Y/N).</summary>
public sealed record InvoiceFlag(string Key, string Column, string Label);

public static class CaseInvoiceCatalog
{
    private const InvoiceTable A = InvoiceTable.Actual, O = InvoiceTable.ActualOthers, N = InvoiceTable.NotCovered;
    private const InvoiceFieldKind Days = InvoiceFieldKind.Days, Computed = InvoiceFieldKind.Computed;
    private const string Hosp = "Hospital charges", Hss = "Hospital supplies & services breakdown", Surg = "Surgical", NonSurg = "Non-surgical (medical)",
        Out = "Out-patient", Oth = "Others", NotPay = "Reimbursement not payable";

    // Order and groups follow the frames of the legacy form. The legacy control name is noted where a formula refers to it.
    public static readonly IReadOnlyList<InvoiceField> Fields =
    [
        new("boardDays", O, "CLMBoardDay", "Room & board days", Hosp, Days),
        new("roomRate", O, "CLMRB", "Room & board rate per day", Hosp),
        new("roomAmount", A, "CLMBoardAmt", "Room & board amount", Hosp, Computed),          // A1 = days x rate
        new("roomTax", A, "CLMNewRBTax", "Room & board tax", Hosp),                           // ART
        new("roomTotal", A, "CLMTotalBoardAmt", "Room & board total", Hosp, Computed, true),  // A1B = A1 + ART
        new("grossRoomBoard", A, "CLMGrossRB", "Gross room & board", Hosp),
        new("icuDays", O, "CLMICUDay", "ICU days", Hosp, Days),
        new("icuRate", O, "CLMICU", "ICU rate per day", Hosp),
        new("icuAmount", A, "CLMICUAmt", "ICU amount", Hosp, Computed),                       // A2 = days x rate
        new("icuTax", A, "CLMNewICUTax", "ICU tax", Hosp),                                    // AIT
        new("icuTotal", A, "CLMTotalICUAmt", "ICU total", Hosp, Computed, true),              // A2B = A2 + AIT
        new("services", A, "CLMServices", "Hospital supplies & services (HSS)", Hosp, InTotal: true),  // A3
        new("theatre", A, "CLMTheatre", "Operating theatre (OT)", Hosp, InTotal: true),                // A4
        new("mri", A, "CLMMRI", "MRI", Hosp, InTotal: true),                                           // A23

        new("hssPharmacy", O, "CLMPharmacyHSS", "Pharmacy", Hss), new("hssMedSupplies", O, "CLMMedSuppliesHSS", "Medical supplies", Hss),
        new("hssNursingCare", O, "CLMNursingCareHSS", "Nursing care", Hss), new("hssNursingServices", O, "CLMNursingSvrHSS", "Nursing services", Hss),
        new("hssLab", O, "CLMLabHSS", "Laboratory", Hss), new("hssRadiology", O, "CLMRadiologiHSS", "Radiology", Hss),
        new("hssOtSupplies", O, "CLMOTSuppHSS", "OT supplies", Hss), new("hssEquipment", O, "CLMEquipHSS", "Equipment charges", Hss), new("hssMisc", O, "CLMMiscHSS", "Miscellaneous", Hss),

        new("preXray", O, "CLMPreXRay", "Pre-hospital X-ray", Surg), new("preLab", O, "CLMPreLab", "Pre-hospital lab", Surg), new("preOthers", O, "CLMPreoTHERS", "Pre-hospital others", Surg),
        new("preDiagnostic", A, "CLMPreHSAmt", "Pre-hospital diagnostics", Surg, Computed, true),      // A5 = X-ray + lab + others
        new("preSpecialist", A, "CLMPreHosp", "Pre-hospital specialist", Surg, InTotal: true),         // A24
        new("surgeonFee", O, "CLMSurgicalFee", "Surgeon fee", Surg), new("surgeonPhysician", O, "CLMSurgPhy", "Surgeon physician", Surg),
        new("surgeonPost", O, "CLMSurgPost", "Surgeon post-operative", Surg), new("surgeonWard", O, "CLMSurgicalWard", "Surgeon ward", Surg),
        new("surgical", A, "CLMSurgicalAmt", "Surgical total", Surg, Computed, true),                   // A6 = fee + physician + post + ward
        new("anaesFee", O, "CLMAnaeFees", "Anaesthetist fee", Surg), new("anaesConsultation", O, "CLMAnaeConsultation", "Anaesthetist consultation", Surg),
        new("anaesWard", O, "CLMAnaeWard", "Anaesthetist ward", Surg),
        new("anaesthetist", A, "CLMAna", "Anaesthetist total", Surg, Computed, true),                  // A7 = fee + consultation + ward

        new("nsPreXray", O, "CLMMXRay", "Pre-hospital X-ray", NonSurg), new("nsPreLab", O, "CLMMLab", "Pre-hospital lab", NonSurg), new("nsPreOthers", O, "CLMMPreOthers", "Pre-hospital others", NonSurg),
        new("nsPreDiagnostic", A, "CLMMPreHosAmt", "Pre-hospital diagnostics", NonSurg, Computed, true), // A5B
        new("nsPreSpecialist", A, "CLMNSPreHos", "Pre-hospital specialist", NonSurg, InTotal: true),     // A8
        new("physicianVisitDays", O, "CLMINHospHyVisitDay", "In-hospital physician visit days", NonSurg, Days),
        new("physicianFee", O, "CLMInHosFees", "In-hospital physician fee", NonSurg), new("physicianWard", O, "CLMInHosWard", "In-hospital physician ward", NonSurg),
        new("procedureFee", O, "CLMProceduresFee", "Procedure fee", NonSurg),
        new("inHospitalPhysician", A, "CLMPhysician", "In-hospital physician total", NonSurg, Computed, true), // A9 = fee + ward + procedure
        new("nsPostTreatment", A, "CLMPost", "Post-hospital treatment", NonSurg, InTotal: true),              // A10

        new("accidentEmergency", A, "CLMEmergency", "Accident (emergency)", Out), new("accidentPostTreatment", A, "CLMPostTreat", "Accident post-treatment", Out),
        new("accidentPostAmount", A, "CLMAccPostAmt", "Accident post-treatment amount", Out, Computed, true), // A11 = accident + post-treatment
        new("ambulance", A, "CLMAmbulance", "Ambulance fee", Out, InTotal: true),                          // A12
        new("physiotherapy", A, "CLMOutpatient", "Outpatient physiotherapy", Out, InTotal: true),          // A13
        new("kidneyMonths", O, "CLMKidneyMnth", "Kidney dialysis months", Out, Days),
        new("kidney", A, "CLMMonthly", "Kidney dialysis", Out, InTotal: true),                             // A14
        new("cancer", A, "CLMCancerAmt", "Cancer treatment", Out, InTotal: true),                          // A27
        new("homeNursing", A, "CLMHomeNCAmt", "Home nursing care", Out, InTotal: true),                    // A26

        new("medicalReport", A, "CLMMedical", "Medical report", Oth, InTotal: true),                       // A15
        new("phone", A, "CLMPhone", "Telephone charges", Oth, InTotal: true),                              // A16
        new("guardianDays", O, "CLMLodgerday", "Guardian allowance days", Oth, Days), new("guardianRate", O, "CLMLodger", "Guardian allowance per day", Oth),
        new("guardianAmount", A, "CLMGuardianAmt", "Guardian allowance amount", Oth, Computed),            // A17 = days x rate
        new("guardianTax", A, "CLMNewLodgerTax", "Guardian allowance tax", Oth),                           // ALT
        new("guardianTotal", A, "CLMTotalGAllowAmt", "Guardian allowance total", Oth, Computed, true),     // ALB = A17 + ALT
        new("admin", A, "CLMAdmin", "Admin / registration", Oth, InTotal: true),                           // A18
        new("rbTaxOld", A, "CLMRBTax", "Tax on room & board", Oth), new("icuTaxOld", A, "CLMICUTax", "Tax on ICU", Oth), new("lodgerTaxOld", A, "CLMLodgerTax", "Tax on lodger", Oth),
        new("tax", A, "CLMTax", "Tax total", Oth, Computed, true),                                         // A19 = the three taxes above
        new("others1", A, "CLMOthers1", "Others", Oth, InTotal: true),                                     // A20
        new("others2", A, "CLMOthers2", "Others 2", Oth, InTotal: true),                                   // A20B
        new("othersNoCoPay", A, "CLMOthersNotCOPay", "Others (no co-pay)", Oth, InTotal: true),            // A25
        new("cashDays", O, "CLMCashDay", "Government hospital cash days", Oth, Days), new("cashRate", O, "CLMCash", "Cash allowance per day", Oth),
        new("cashAllowance", A, "CLMCashAmt", "Cash allowance", Oth, Computed, true),                      // A21 = days x rate
        new("organ", A, "CLMOrgan", "Organ transplant", Oth, InTotal: true),                               // A22

        new("notPreOver60", N, "PreH60", "Pre-hospital > 60 days", NotPay), new("notGp", N, "PreGP", "GP fee", NotPay),
        new("notPreMedicine", N, "preMedic", "Pre-hospital medicine", NotPay), new("notMedicalReport", N, "PreMR", "Medical report", NotPay),
        new("notPreSpecialist", N, "PreSpec", "Pre-hospital specialist fee > 1", NotPay), new("notPostOver31", N, "PostHosp", "Post-hospital > 31 days", NotPay),
        new("notSamePhysician", N, "PostNotSame", "Not same physician", NotPay), new("notOthers", N, "Others", "Others", NotPay),
    ];

    public static readonly IReadOnlyList<InvoiceFlag> Flags =
    [
        new("medicalReportByMember", "CLMMedRptMBM", "Medical report charged to member"), new("phoneByMember", "CLMTelChargesMBM", "Telephone charges charged to member"),
        new("guardianByMember", "CLMGuaAllowMBM", "Guardian allowance charged to member"), new("adminByMember", "CLMAdminRegMBM", "Admin / registration charged to member"),
        new("othersByMember", "CLMOthersMBM", "Others charged to member"),
    ];

    /// <summary>Hospital names that stand for any clinic of a kind: the legacy form then required the real hospital or clinic name.</summary>
    public static readonly IReadOnlyList<string> GenericHospitals = ["DT", "DL", "FH", "GP", "GOVNT", "N00001", "SP"];

    public const string MemberPayee = "Member";
    public const decimal MaxAmount = 922337203685477m;
    public const decimal MaxDays = 9999m;

    /// <summary>
    /// Works out the computed boxes from the entered ones, as the legacy LostFocus handlers did, and the invoice amount (legacy getTotal: every "A" box
    /// minus the room, ICU and guardian parts already inside their totals). Missing values count as zero.
    /// </summary>
    public static (Dictionary<string, decimal> Amounts, decimal Total) Compute(IReadOnlyDictionary<string, decimal> entered)
    {
        var v = Fields.Where(f => f.Kind != InvoiceFieldKind.Computed).ToDictionary(f => f.Key, f => entered.GetValueOrDefault(f.Key));
        decimal G(string k) => v[k];
        v["roomAmount"] = G("boardDays") * G("roomRate");
        v["roomTotal"] = G("roomAmount") + G("roomTax");
        v["icuAmount"] = G("icuDays") * G("icuRate");
        v["icuTotal"] = G("icuAmount") + G("icuTax");
        v["preDiagnostic"] = G("preXray") + G("preLab") + G("preOthers");
        v["surgical"] = G("surgeonFee") + G("surgeonPhysician") + G("surgeonPost") + G("surgeonWard");
        v["anaesthetist"] = G("anaesFee") + G("anaesConsultation") + G("anaesWard");
        v["nsPreDiagnostic"] = G("nsPreXray") + G("nsPreLab") + G("nsPreOthers");
        v["inHospitalPhysician"] = G("physicianFee") + G("physicianWard") + G("procedureFee");
        v["accidentPostAmount"] = G("accidentEmergency") + G("accidentPostTreatment");
        v["guardianAmount"] = G("guardianDays") * G("guardianRate");
        v["guardianTotal"] = G("guardianAmount") + G("guardianTax");
        v["tax"] = G("rbTaxOld") + G("icuTaxOld") + G("lodgerTaxOld");
        v["cashAllowance"] = G("cashDays") * G("cashRate");
        foreach (var k in v.Keys.ToList()) v[k] = Math.Round(v[k], 2);
        return (v, Fields.Where(f => f.InTotal).Sum(f => v[f.Key]));
    }

    /// <summary>
    /// Legacy saveActual: the surgical and medical hospital sums (CLMCHospSum, CLMMHospSum) are kept only for plans whose code starts with 2-5 or that are
    /// marked as a new SM plan; otherwise they are zero.
    /// </summary>
    public static (decimal Surgical, decimal Medical) HospitalSums(IReadOnlyDictionary<string, decimal> a, string? planCode, bool newSmPlan) =>
        newSmPlan || planCode?.Trim() is { Length: > 0 } p && p[0] is >= '2' and <= '5'
            ? (a["preDiagnostic"] + a["preSpecialist"] + a["surgical"] + a["anaesthetist"], a["nsPreDiagnostic"] + a["nsPreSpecialist"] + a["inHospitalPhysician"] + a["nsPostTreatment"])
            : (0m, 0m);
}

public static class CaseInvoicePayTo
{
    public const string Hospital = "H", Member = "M";
}

/// <summary>Invoice header and amounts as the Invoice Processing form saves them. Amount keys are <see cref="CaseInvoiceCatalog.Fields"/> keys; computed ones are ignored.</summary>
public sealed record CaseInvoiceRequest(
    string? InvoiceNo, DateOnly? InvoiceDate, string? PayTo, string? StayType, string? Hospital, string? HospitalName,
    string? ReceiptMode, DateOnly? ReceiptDate, DateOnly? FollowUpDate, decimal? Excess, decimal? InitialAmount, string? UhcNo,
    IReadOnlyDictionary<string, decimal?>? Amounts, IReadOnlyList<string>? ChargedToMember,
    string? OthersDescription, string? Others2Description, string? OthersNoCoPayDescription, string? NotPayableRemark,
    // The procedure entry (legacy frmMMAEntry): when sent, the case's doctors and procedures are saved with the invoice, as legacy SaveCASOthers did.
    InvoiceCaseProcedures? Procedures = null);

/// <summary>One line of the legacy procedure entry: the procedure, its OPCS code and the actual fees charged (anaesthetist fee for surgical lines only).</summary>
public sealed record InvoiceProcedure(string? Text, string? Code, decimal? ActualFee, decimal? ActualAnaesFee = null);

/// <summary>
/// What the legacy procedure entry (frmMMAEntry) edits for the case, in CASOthers and CASOthersTwo: attending doctors (5), anaesthetists (3), medical
/// management (3), investigations (3), surgical procedures (6) and non-surgical procedures (6).
/// </summary>
public sealed record InvoiceCaseProcedures(
    IReadOnlyList<string?>? AttendingDoctors, IReadOnlyList<string?>? Anaesthetists, IReadOnlyList<string?>? MedicalManagement,
    IReadOnlyList<CaseInvestigationInput>? Investigations, IReadOnlyList<InvoiceProcedure>? Surgical, IReadOnlyList<InvoiceProcedure>? NonSurgical);

/// <summary>A VIEW_MMA_Schedule row: the MMA rates and the 13th schedule rates of an OPCS code, with the details legacy frmMMA13thSchDesc showed.</summary>
public sealed record MmaRate(string Code, string? Narrative, string? ScheduleDescription, string? BodyPartMma, string? PageMma, string? BodyPart13th, string? Page13th,
    decimal Surgeon, decimal Anaesthetist, decimal Surgeon13thMin, decimal Surgeon13th, decimal Anaesthetist13th);

/// <summary>An MMAProcedures row for the OPCS and investigation searches (legacy frmSearchCPT, frmSearchInv).</summary>
public sealed record MmaCode(string Code, string? Narrative);

public sealed record InvoiceEntry(
    int Index, string InvoiceNo, DateOnly? InvoiceDate, string? PayTo, string? StayType, string? Payee, string? PayeeName, string? HospitalName,
    string? ReceiptMode, DateOnly? ReceiptDate, DateOnly? FollowUpDate, decimal Amount, decimal Excess, decimal PayAmount, decimal? InitialAmount, string? UhcNo,
    int? Version, bool InWorksheet, bool InBordx, string? EnteredBy, DateOnly? EnteredDate,
    IReadOnlyDictionary<string, decimal> Amounts, IReadOnlyList<string> ChargedToMember,
    string? OthersDescription, string? Others2Description, string? OthersNoCoPayDescription, string? NotPayableRemark,
    // Legacy "Exc Paid" (INVPaidByMem): what the invoice saved as paid by the member (its excess).
    decimal PaidByMember = 0);

public sealed record CaseInvoiceCase(
    string CaseNumber, string? MemberNumber, string? MemberName, string? PolicyNo, string? PatientId, string? PlanCode, string? HospitalCode, string? HospitalName,
    DateOnly? Admitted, DateOnly? Discharged, string? StayType, string? PatientName = null, string? Modality = null,
    // The General Info tab: the employer policy's dates (GroupCompanyFW), the policy status and the member's date of birth.
    DateOnly? PolicyEffective = null, DateOnly? PolicyExpiry = null, string? PolicyStatus = null, DateOnly? MemberDob = null);

/// <summary>Everything the Invoice Processing screen shows for one case: the case, the worksheet version new invoices go to, the case remark and the invoices.</summary>
public sealed record CaseInvoiceScreen(CaseInvoiceCase Case, int NextVersion, string? Remarks, IReadOnlyList<InvoiceEntry> Invoices,
    InvoiceCaseProcedures? Procedures = null, IReadOnlyDictionary<string, MmaRate>? Rates = null);

public sealed record CaseInvoiceOptions(IReadOnlyList<CaseOption> StayTypes, IReadOnlyList<CaseOption> ReceiptModes, IReadOnlyList<string> GenericHospitals,
    IReadOnlyList<InvoiceField> Fields, IReadOnlyList<InvoiceFlag> Flags);

public sealed record CaseInvoiceRemarksRequest(string? Remarks);

public static class CaseInvoiceRules
{
    private static bool Bad(string? s, int max) => s is not null && (s.Trim().Length > max || s.Any(char.IsControl));

    /// <summary>The legacy SaveClaim checks, in the legacy order, plus length and range checks the database needs. Returns the first problem.</summary>
    public static (string Field, string Message)? Validate(CaseInvoiceRequest r, DateOnly? discharged)
    {
        if (discharged is null) return ("discharged", "Please Enter Date of Discharge!");
        if (r.PayTo is not (CaseInvoicePayTo.Hospital or CaseInvoicePayTo.Member)) return ("payTo", "Please select who to be paid (Member / Hospital )!");
        if (string.IsNullOrWhiteSpace(r.StayType)) return ("stayType", "Please select a Stay Type!");
        if (r.PayTo == CaseInvoicePayTo.Hospital && string.IsNullOrWhiteSpace(r.Hospital)) return ("hospital", "Please key in the person/hospital to be paid!");
        if (string.IsNullOrWhiteSpace(r.InvoiceNo)) return ("invoiceNo", "Please key in Invoice No.!");
        if (Bad(r.InvoiceNo, 20)) return ("invoiceNo", "The invoice number can be at most 20 characters.");
        if (r.InvoiceDate is null) return ("invoiceDate", "Please key in Invoice date!");
        if (r.InvoiceDate.Value.Year < 1990 || r.InvoiceDate > DateOnly.FromDateTime(DateTime.Today).AddYears(1)) return ("invoiceDate", "Please key in correct Invoice date!");
        if (r.PayTo == CaseInvoicePayTo.Member && r.ReceiptDate is null) return ("receiptDate", "Please key in Invoice Receipt date!");
        if (r.PayTo == CaseInvoicePayTo.Member && string.IsNullOrWhiteSpace(r.ReceiptMode)) return ("receiptMode", "Please key in Invoice Receipt Mode!");
        if (r.PayTo == CaseInvoicePayTo.Hospital && r.Excess is null) return ("excess", "Please key in Excess Amount!");
        if (r.InitialAmount is null) return ("initialAmount", "Please key in Initial Invoice Amount!");
        if (r.PayTo == CaseInvoicePayTo.Hospital && CaseInvoiceCatalog.GenericHospitals.Contains(r.Hospital!.Trim()) && string.IsNullOrWhiteSpace(r.HospitalName))
            return ("hospitalName", "Please Enter Hospital Name or Clinic Name!");
        if (Bad(r.HospitalName, 100)) return ("hospitalName", "The hospital name can be at most 100 characters.");
        if (Bad(r.UhcNo, 10)) return ("uhcNo", "The IRM number can be at most 10 characters.");
        if (Bad(r.ReceiptMode, 2) || Bad(r.StayType, 1)) return ("receiptMode", "Unknown receipt mode or stay type.");
        if (Bad(r.OthersDescription, 100) || Bad(r.Others2Description, 100)) return ("othersDescription", "An others description can be at most 100 characters.");
        if (Bad(r.OthersNoCoPayDescription, 50)) return ("othersNoCoPayDescription", "The others (no co-pay) description can be at most 50 characters.");
        if (Bad(r.NotPayableRemark, 80)) return ("notPayableRemark", "The not payable remark can be at most 80 characters.");
        foreach (var (name, value) in new[] { ("excess", r.Excess), ("initialAmount", r.InitialAmount) })
            if (value is < 0 or > CaseInvoiceCatalog.MaxAmount) return (name, "Amounts must be zero or more.");

        var fields = CaseInvoiceCatalog.Fields.ToDictionary(f => f.Key);
        foreach (var (key, value) in r.Amounts ?? new Dictionary<string, decimal?>())
        {
            if (!fields.TryGetValue(key, out var f)) return (key, $"Unknown amount '{key}'.");
            if (value is null || f.Kind == InvoiceFieldKind.Computed) continue;
            if (value < 0 || value > (f.Kind == InvoiceFieldKind.Days ? CaseInvoiceCatalog.MaxDays : CaseInvoiceCatalog.MaxAmount)) return (key, $"{f.Label} must be zero or more.");
        }
        foreach (var flag in r.ChargedToMember ?? [])
            if (CaseInvoiceCatalog.Flags.All(x => x.Key != flag)) return ("chargedToMember", $"Unknown option '{flag}'.");
        return r.Procedures is null ? null : ValidateProcedures(r.Procedures);
    }

    /// <summary>The CASOthers / CASOthersTwo column sizes (the legacy stored procedures Case_AdjInvCASOthers and Case_AdjInvCASOthersTwo).</summary>
    public static (string Field, string Message)? ValidateProcedures(InvoiceCaseProcedures p)
    {
        static bool Many<T>(IReadOnlyList<T>? xs, int max) => xs is not null && xs.Count > max;
        if (Many(p.AttendingDoctors, 5) || Many(p.Anaesthetists, 3) || Many(p.MedicalManagement, 3) || Many(p.Investigations, 3) || Many(p.Surgical, 6) || Many(p.NonSurgical, 6))
            return ("procedures", "Too many lines in the procedure entry.");
        var doctors = p.AttendingDoctors ?? [];
        for (var i = 0; i < doctors.Count; i++)
            if (Bad(doctors[i], i == 0 ? 100 : 30)) return ("attendingDoctors", $"Attending doctor {i + 1} can be at most {(i == 0 ? 100 : 30)} characters.");
        if ((p.Anaesthetists ?? []).Any(x => Bad(x, 30))) return ("anaesthetists", "An anaesthetist name can be at most 30 characters.");
        if ((p.MedicalManagement ?? []).Any(x => Bad(x, 500))) return ("medicalManagement", "Medical management can be at most 500 characters.");
        if ((p.Investigations ?? []).Any(x => Bad(x.Done, 500) || Bad(x.Code, 10))) return ("investigations", "An investigation can be at most 500 characters and its code 10.");
        foreach (var (lines, max, name) in new[] { (p.Surgical, 500, "surgical"), (p.NonSurgical, 200, "nonSurgical") })
            foreach (var x in lines ?? [])
            {
                if (Bad(x.Text, max) || Bad(x.Code, 6)) return (name, $"A procedure can be at most {max} characters and its OPCS code 6.");
                if (x.ActualFee is < 0 or > CaseInvoiceCatalog.MaxAmount || x.ActualAnaesFee is < 0 or > CaseInvoiceCatalog.MaxAmount) return (name, "Actual fees must be zero or more.");
            }
        return null;
    }
}

public interface ICaseInvoiceRepository
{
    Task<CaseInvoiceOptions> OptionsAsync(CancellationToken ct = default);
    Task<CaseInvoiceScreen?> GetAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Saves a new invoice against the case's next worksheet version (all its tables in one transaction). An invoice number may be used once per case.</summary>
    Task<InvoiceEntry> CreateAsync(string caseNumber, CaseInvoiceRequest request, string user, CancellationToken ct = default);

    /// <summary>Changes an invoice that is not yet in a worksheet.</summary>
    Task<InvoiceEntry> UpdateAsync(string caseNumber, int index, CaseInvoiceRequest request, string user, CancellationToken ct = default);

    /// <summary>Removes an invoice that is not yet in a worksheet (legacy "Remove").</summary>
    Task DeleteAsync(string caseNumber, int index, string user, CancellationToken ct = default);

    Task SaveRemarksAsync(string caseNumber, string? remarks, string user, CancellationToken ct = default);

    /// <summary>The MMA and 13th schedule rates of an OPCS code (legacy SearchMMARate / SearchMMADesc), or null when the schedule has none.</summary>
    Task<MmaRate?> MmaRateAsync(string code, CancellationToken ct = default);

    /// <summary>Legacy frmSearchCPT (all MMA procedures) and frmSearchInv (investigations: procedure part A), by code prefix and narrative.</summary>
    Task<IReadOnlyList<MmaCode>> MmaCodesAsync(string? code, string? narrative, bool investigations, CancellationToken ct = default);
}
