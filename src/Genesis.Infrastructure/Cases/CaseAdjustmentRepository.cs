using System.Globalization;
using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

internal sealed class CaseAdjustmentRepository(IDbConnectionFactory connections, ICaseRegistrationRepository registration, IAuditLog audit) : SqlRepository(connections), ICaseAdjustmentRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Nz(string? s) => s ?? "";
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateOnly? ParseText(string? s) => DateOnly.TryParseExact(T(s), ["dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Please Enter Case No!", field: "caseNumber"); return c; }

    public async Task<string?> NeighbourAsync(string caseNumber, bool next, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var sql = next ? "SELECT TOP 1 CASNumber FROM CASCrossReference WHERE CASNumber > @no ORDER BY CASNumber" : "SELECT TOP 1 CASNumber FROM CASCrossReference WHERE CASNumber < @no ORDER BY CASNumber DESC";
        return (await QueryAsync(GenesisDb.Maintenance, sql, new { no }, ct)).Select(r => T(S(r, "CASNumber"))).FirstOrDefault();
    }

    public async Task<CaseAdjustmentView?> GetAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var c = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CAS WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        if (c is null) return null;
        var member = T(S(c, "MBMNumber")); var patient = T(S(c, "MBMPatientCovID"));
        var view = await registration.MemberAsync(member, ct);
        var name = view?.Patients.FirstOrDefault(p => p.CoverId == patient)?.Name ?? "";
        async Task<IDictionary<string, object?>?> One(string table) => (await QueryAsync(GenesisDb.Main, $"SELECT * FROM {table} WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var o = await One("CASOthers"); var o2 = await One("CASOthersTwo"); var rm1 = await One("CASRemarks"); var rm2 = await One("CASRemarksTwo"); var rm3 = await One("CASRemarkThree");
        var first = await One("DIAFirst"); var fin = await One("DIAFinal"); var ud = await One("CAS_UD"); var rep = await One("CASRepudiation");
        var hos = (await QueryAsync(GenesisDb.Main, "SELECT HOSName FROM HOS WHERE HOSCode = @c", new { c = T(S(c, "CASHOSCode")) }, ct)).Select(r => Blank(S(r, "HOSName"))).FirstOrDefault();

        string Get(IDictionary<string, object?>? r, string col) => r is null ? "" : T(S(r, col));
        string Rem(IDictionary<string, object?>? r, params string[] cols) => string.Concat(cols.Select(x => r is null ? "" : S(r, x) ?? "")).Replace("~", "\n");
        List<CaseDiagnosisInput> Diag(IDictionary<string, object?>? r, string code, string text, string dos) => Enumerable.Range(1, 3).Select(i => new CaseDiagnosisInput(Blank(Get(r, code + i)), Blank(Get(r, text + i)), r is null ? null : Day(r, dos + i))).ToList();
        List<CaseProcedureInput> Procs(string text, string cpt) => Enumerable.Range(1, 6).Select(i => new CaseProcedureInput(Blank(Get(o, text + i)), Blank(Get(o, cpt + i)))).Where(p => p.Text is not null || p.CptCode is not null).ToList();
        var reps = new[] { Get(rep, "CASREPCode1"), Get(rep, "CASREPCode2"), Get(rep, "CASREPCode3") }.Where(x => x.Length > 0).ToList();
        var form = new CaseRegistrationRequest(member, patient, T(S(c, "CASClaimability")), T(S(c, "CASHOSCode")), Blank(Get(o2, "CASHOSName")), Day(c, "CASDateAdmitted"), Day(c, "CASDischargeDate"), Blank(Get(c, "CASPending")), Dec(c, "CASReserveAmount"), Dec(c, "CASLGAmt"),
            Blank(Get(c, "CASLGTime")), T(S(c, "CASPAYType")), Blank(Get(c, "CASStayType")), Blank(Get(c, "ModalityCode")), Blank(Get(c, "ClaimNatureCode")), Blank(Get(c, "CASInd")), Blank(Get(c, "CASTopupLmt")), Blank(Get(c, "CASQuery")), Blank(Get(c, "LINKCASNo")),
            Blank(Get(o2, "ReferedBy")), Blank(Get(o2, "CASAdmissionReason")), Day(c, "CASDRD"), I(c, "CASPolYear"), Blank(Get(c, "CASESPRefNo")), Blank(Get(c, "CASTakeOverInd")), Dec(c, "CASTakeOverAmt"), Blank(Get(c, "CASTakeOverPlan")), Blank(Get(o2, "CASIntExtTO")),
            Blank(Get(o, "CASPatientAge")), Blank(Get(o, "CASPatientDayYr")), ParseText(Get(o, "CASFirstCall"))?.ToString("dd/MM/yyyy"), ParseText(Get(o, "CASFirstTreated"))?.ToString("dd/MM/yyyy"), Blank(Get(o, "CASPayPending")), Blank(Get(o, "CASFolUp")),
            Blank(Get(o, "CASOCondWillRecur")), Blank(Get(o, "CASOFollowUpRequired")), [Rem(rm1, "CASORemarks", "CASORemarksTwo"), Rem(rm2, "CASORemarks", "CASORemarksTwo"), Rem(rm3, "CASRemarks"), Rem(rm3, "CASRemarksFour"), Rem(rm3, "CASRemarksFive")],
            Diag(first, "ICDCode", "DIAPrelimDiag", "DIADOS"), Diag(fin, "ICDCode", "DIAFinalDiag", "DIADOS"), false, false, false,
            reps, Blank(Get(rep, "CASDocument")), Blank(Get(rep, "CASPersonnel")), Blank(Get(rep, "CASAttention")), Get(rep, "CASUnderlying") == "Y", Diag(ud, "CASUDCode", "CASUDReasons", "CASUDDOS"), Dec(o ?? new Dictionary<string, object?>(), "CASRedAmt"),
            Enumerable.Range(1, 5).Select(i => Get(o2, "CASAttendDoctor" + i)).Where(x => x.Length > 0).ToList(), Enumerable.Range(1, 3).Select(i => Get(o2, "CASAnaesName" + i)).Where(x => x.Length > 0).ToList(),
            Procs("CASOPROCRequired", "CASCptCode"), Procs("CASONonSurgPROCRequired", "CASNonSurgCptCode"),
            Enumerable.Range(1, 3).Select(i => new CaseInvestigationInput(Blank(Get(o, "CASOProcInvestigationDone" + i)), Blank(Get(o, "CASInvCode" + i)))).Where(x => x.Done is not null || x.Code is not null).ToList(),
            Enumerable.Range(1, 3).Select(i => Get(o, "CASOMedSurMgmt" + i)).Where(x => x.Length > 0).ToList(), o2 is null ? null : Day(o2, "CASFirstDocDate"), o2 is null ? null : Day(o2, "CASSubSeqDocDate"));
        return new CaseAdjustmentView(no, T(S(c, "CASStatus")), view, name, form, hos, Blank(Get(c, "CASLastUpdateUser")), Day(c, "CASLastUpdateDate"), Day(c, "CaseRegDate"), Blank(Get(c, "CaseOpenBy")));
    }

    public async Task UpdateAsync(string caseNumber, CaseAdjustmentRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var before = await GetAsync(no, ct) ?? throw BusinessRuleException.NotFound($"Case '{no}' does not exist.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (CaseAdjustmentRules.Validate(r, before.Form.MemberNumber!, before.Form.PatientId!, today) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var f = r.Form! with { MemberNumber = before.Form.MemberNumber, PatientId = before.Form.PatientId };
        var claim = f.Claimability!.Trim(); var repudiating = CaseRegistrationRules.Repudiating.Contains(claim);

        var hospital = (await QueryAsync(GenesisDb.Main, "SELECT HOSCode, HOSName FROM HOS WHERE HOSCode = @c", new { c = T(f.HospitalCode) }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("The hospital does not exist.");
        if (CaseRegistrationRules.GenericHospitals.Contains(T(S(hospital, "HOSName")).ToUpperInvariant()) && string.IsNullOrWhiteSpace(f.HospitalName)) throw new BusinessRuleException("Please Enter Hospital Name or Clinic Name!", field: "hospitalName");
        foreach (var (code, table, col) in new[] { (f.Modality, "TreatmentModality", "ModalityCode"), (f.Nature, "ClaimNature", "ClaimNatureCode"), (f.StayType, "PayType", "PayTypeCode") })
            if (Blank(code) is { } c && await ScalarAsync<int>(GenesisDb.Main, $"SELECT COUNT(*) FROM {table} WHERE {col} = @c", new { c }, ct) == 0) throw BusinessRuleException.NotFound($"'{c}' is not a valid {col}.");
        foreach (var icd in (f.FirstDiagnosis ?? []).Concat(f.FinalDiagnosis ?? []).Concat(f.UnderlyingDiagnosis ?? []).Select(d => Blank(d.Code)).Where(c => c is not null).Distinct())
            if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM ICD WHERE ICDCode = @c", new { c = icd }, ct) == 0) throw BusinessRuleException.NotFound($"ICD code '{icd}' does not exist.");
        var repCodes = repudiating ? f.RepudiationCodes!.Select(T).Where(c => c.Length > 0).ToList() : [];
        foreach (var rc in repCodes)
            if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM RepCode WHERE RepCode = @c", new { c = rc }, ct) == 0) throw BusinessRuleException.NotFound($"Repudiation code '{rc}' does not exist.");
        if (repudiating)
            foreach (var (code, prefix) in new[] { (T(f.Document), "D"), (T(f.Personnel), "P"), (T(f.Attention), "A") })
                if (!code.StartsWith(prefix) || await ScalarAsync<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM REPSelection WHERE REPCode = @c", new { c = code }, ct) == 0) throw BusinessRuleException.NotFound($"The repudiation selection '{code}' does not exist.");
        var stayDays = f.DateDischarge is { } dd ? dd.DayNumber - f.DateAdmitted!.Value.DayNumber : 0;
        if (stayDays > 5 && !f.AcknowledgeLongStay) throw BusinessRuleException.Conflict("Admission Period Is More Than 5 Days. Confirm to continue.");

        var status = r.Status!;
        var oldRef = (await QueryAsync(GenesisDb.Maintenance, "SELECT CASStatus, CASClaimability, CASDateAdmitted, CASDischargeDate, CASPending, LINKCASNo, CASTopupLmt, CASPayPending FROM CASCrossReference WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var refArgs = new { no, status, claim = claim[..1], admitted = At(f.DateAdmitted), discharged = At(f.DateDischarge), pending = Nz(f.Pending), link = Nz(Blank(f.LinkCaseNo)), top = Nz(f.TopUpLimit), payPending = Nz(f.PayPending) };
        const string refUpdate = "UPDATE CASCrossReference SET CASStatus = @status, CASClaimability = @claim, CASDateAdmitted = @admitted, CASDischargeDate = @discharged, CASPending = @pending, LINKCASNo = @link, CASTopupLmt = @top, CASPayPending = @payPending WHERE CASNumber = @no";
        await using (var m = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct)) { await m.ExecuteAsync(refUpdate, refArgs, ct); await m.CommitAsync(ct); }

        try
        {
            await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
            var now = DateTime.Today; var reimbursement = f.PayType!.Trim() == "R";
            await s.ExecuteAsync("""
                UPDATE CAS SET CASStatus = @status, CASClaimability = @claim, CASDateAdmitted = @admitted, CASDischargeDate = @discharged, ModalityCode = @modality, CASStayType = @stay, CASPAYType = @payType, ClaimNatureCode = @nature, CASPending = @pending,
                    CASReserveAmount = @reserve, LINKCASNo = @link, CASInd = @ind, CASLastUpdateUser = @user, CASLastUpdateDate = @now, CASLGAmt = @lg, CASTopupLmt = @top, CASDRD = @drd, CASQuery = @query, CASTakeOverAmt = @toAmt,
                    CASTakeOverInd = @toInd, CASTakeOverPlan = @toPlan, CASLGTime = @lgTime, CASESPRefNo = @esp, CASHOSCode = @hos
                WHERE CASNumber = @no
                """, new
            {
                no, status, claim, admitted = At(f.DateAdmitted), discharged = At(f.DateDischarge), modality = Nz(Blank(f.Modality)), stay = Nz(Blank(f.StayType)), payType = f.PayType!.Trim(), nature = Nz(Blank(f.Nature)), pending = Nz(f.Pending), reserve = f.ReserveAmount, link = Nz(Blank(f.LinkCaseNo)),
                ind = Nz(Blank(f.SpecialApproval)), user, now, lg = f.LgAmount, top = Nz(f.TopUpLimit), drd = At(f.DocReceivedDate), query = Nz(f.Query), toAmt = f.TakeOverAmount ?? 0, toInd = Nz(f.TakeOverInd), toPlan = Blank(f.TakeOverPlan), lgTime = Blank(f.LgTime), esp = Blank(f.EspRefNo), hos = T(f.HospitalCode)
            }, ct);
            if (reimbursement) await s.ExecuteAsync("UPDATE CAS SET CASClaimAdmin = '1', CASTokenDate = ISNULL(CASTokenDate, @now), CASUserToken = ISNULL(CASUserToken, @user) WHERE CASNumber = @no", new { no, now, user }, ct);

            var rem = (f.Remarks ?? []).Select(x => (x ?? "").Replace("\r\n", "~").Replace("\r", "~").Replace("\n", "~")).ToList(); while (rem.Count < 5) rem.Add("");
            string Cut(string x) => x[..Math.Min(1900, x.Length)]; string Rest(string x) => x.Length > 1900 ? x[1900..] : "";
            foreach (var (table, text) in new[] { ("CASRemarks", rem[0]), ("CASRemarksTwo", rem[1]) })
                if (await s.ExecuteAsync($"UPDATE {table} SET CASORemarks = @a, CASORemarksTwo = @b WHERE CASNumber = @no", new { no, a = Cut(text), b = Rest(text) }, ct) == 0)
                    await s.ExecuteAsync($"INSERT INTO {table} (CasNumber, CASORemarks, CASORemarksTwo) VALUES (@no, @a, @b)", new { no, a = Cut(text), b = Rest(text) }, ct);
            if (await s.ExecuteAsync("UPDATE CASRemarkThree SET CASRemarks = @a, CASRemarksFour = @b, CASRemarksFive = @c WHERE CASNumber = @no", new { no, a = rem[2], b = rem[3], c = rem[4] }, ct) == 0)
                await s.ExecuteAsync("INSERT INTO CASRemarkThree (CasNumber, CASRemarks, CASRemarksFour, CASRemarksFive) VALUES (@no, @a, @b, @c)", new { no, a = rem[2], b = rem[3], c = rem[4] }, ct);

            var first = Pad(f.FirstDiagnosis); var fin = Pad(f.FinalDiagnosis); var ud = Pad(f.UnderlyingDiagnosis);
            if (await s.ExecuteAsync("UPDATE DIAFirst SET ICDCode1 = @c1, ICDCode2 = @c2, ICDCode3 = @c3, DIAPrelimDiag1 = @t1, DIAPrelimDiag2 = @t2, DIAPrelimDiag3 = @t3, DIADOS1 = @d1, DIADOS2 = @d2, DIADOS3 = @d3 WHERE CASNumber = @n", Diag(no, first), ct) == 0)
                await s.ExecuteAsync("INSERT INTO DIAFirst (CasNumber, ICDCode1, ICDCode2, ICDCode3, DIAPrelimDiag1, DIAPrelimDiag2, DIAPrelimDiag3, DIADOS1, DIADOS2, DIADOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(no, first), ct);
            if (await s.ExecuteAsync("UPDATE DIAFinal SET ICDCode1 = @c1, ICDCode2 = @c2, ICDCode3 = @c3, DIAFinalDiag1 = @t1, DIAFinalDiag2 = @t2, DIAFinalDiag3 = @t3, DIADOS1 = @d1, DIADOS2 = @d2, DIADOS3 = @d3, DIALastAdjustmentUser = @user, DIALastAdjustmentDate = @now WHERE CASNumber = @n", Diag(no, fin, user, now), ct) == 0)
                await s.ExecuteAsync("INSERT INTO DIAFinal (CasNumber, ICDCode1, ICDCode2, ICDCode3, DIAFinalDiag1, DIAFinalDiag2, DIAFinalDiag3, DIADOS1, DIADOS2, DIADOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(no, fin), ct);
            if (await s.ExecuteAsync("UPDATE CAS_UD SET CASUDCode1 = @c1, CASUDCode2 = @c2, CASUDCode3 = @c3, CASUDReasons1 = @t1, CASUDReasons2 = @t2, CASUDReasons3 = @t3, CASUDDOS1 = @d1, CASUDDOS2 = @d2, CASUDDOS3 = @d3 WHERE CASNumber = @n", Diag(no, ud), ct) == 0)
                await s.ExecuteAsync("INSERT INTO CAS_UD (CasNumber, CASUDCode1, CASUDCode2, CASUDCode3, CASUDReasons1, CASUDReasons2, CASUDReasons3, CASUDDOS1, CASUDDOS2, CASUDDOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(no, ud), ct);

            var procs = Fill(f.Procedures, 6); var nons = Fill(f.NonSurgicalProcedures, 6); var invs = FillInv(f.Investigations); var med = Fill(f.MedicalManagement, 3); var docs = Fill(f.AttendingDoctors, 5); var anae = Fill(f.Anaesthetists, 3);
            var oArgs = new
            {
                no, recur = Nz(f.ConditionWillRecur), followReq = Nz(f.FollowUpRequired), call = Blank(f.FirstCall), treated = Blank(f.FirstTreated), red = f.DeductionAmount ?? 0, fol = Nz(f.FollowUp), payPend = Nz(f.PayPending), age = f.PatientAge!.Trim(), unit = f.AgeUnit,
                p1 = procs[0].T, p2 = procs[1].T, p3 = procs[2].T, p4 = procs[3].T, p5 = procs[4].T, p6 = procs[5].T, pc1 = procs[0].C, pc2 = procs[1].C, pc3 = procs[2].C, pc4 = procs[3].C, pc5 = procs[4].C, pc6 = procs[5].C,
                s1 = nons[0].T, s2 = nons[1].T, s3 = nons[2].T, s4 = nons[3].T, s5 = nons[4].T, s6 = nons[5].T, sc1 = nons[0].C, sc2 = nons[1].C, sc3 = nons[2].C, sc4 = nons[3].C, sc5 = nons[4].C, sc6 = nons[5].C,
                i1 = invs[0].T, i2 = invs[1].T, i3 = invs[2].T, ic1 = invs[0].C, ic2 = invs[1].C, ic3 = invs[2].C, m1 = med[0].T, m2 = med[1].T, m3 = med[2].T
            };
            if (await s.ExecuteAsync("""
                UPDATE CASOthers SET CASOCondWillRecur = @recur, CASOFollowUpRequired = @followReq, CASFirstCall = @call, CASFirstTreated = @treated, CASRedAmt = @red, CASFolUp = @fol, CASPayPending = @payPend, CASPatientAge = @age, CASPatientDayYr = @unit,
                    CASOPROCRequired1 = @p1, CASOPROCRequired2 = @p2, CASOPROCRequired3 = @p3, CASOPROCRequired4 = @p4, CASOPROCRequired5 = @p5, CASOPROCRequired6 = @p6, CASCptCode1 = @pc1, CASCptCode2 = @pc2, CASCptCode3 = @pc3, CASCptCode4 = @pc4, CASCptCode5 = @pc5, CASCptCode6 = @pc6,
                    CASONonSurgPROCRequired1 = @s1, CASONonSurgPROCRequired2 = @s2, CASONonSurgPROCRequired3 = @s3, CASONonSurgPROCRequired4 = @s4, CASONonSurgPROCRequired5 = @s5, CASONonSurgPROCRequired6 = @s6,
                    CASNonSurgCptCode1 = @sc1, CASNonSurgCptCode2 = @sc2, CASNonSurgCptCode3 = @sc3, CASNonSurgCptCode4 = @sc4, CASNonSurgCptCode5 = @sc5, CASNonSurgCptCode6 = @sc6,
                    CASOProcInvestigationDone1 = @i1, CASOProcInvestigationDone2 = @i2, CASOProcInvestigationDone3 = @i3, CASInvCode1 = @ic1, CASInvCode2 = @ic2, CASInvCode3 = @ic3, CASOMedSurMgmt1 = @m1, CASOMedSurMgmt2 = @m2, CASOMedSurMgmt3 = @m3
                WHERE CASNumber = @no
                """, oArgs, ct) == 0)
                await s.ExecuteAsync("""
                    INSERT INTO CASOthers (CasNumber, CASOCondWillRecur, CASOFollowUpRequired, CASFirstCall, CASFirstTreated, CASRedAmt, CASFolUp, CASPayPending, CASPatientAge, CASPatientDayYr, CASOPROCRequired1, CASOPROCRequired2, CASOPROCRequired3, CASOPROCRequired4, CASOPROCRequired5, CASOPROCRequired6,
                        CASCptCode1, CASCptCode2, CASCptCode3, CASCptCode4, CASCptCode5, CASCptCode6, CASONonSurgPROCRequired1, CASONonSurgPROCRequired2, CASONonSurgPROCRequired3, CASONonSurgPROCRequired4, CASONonSurgPROCRequired5, CASONonSurgPROCRequired6,
                        CASNonSurgCptCode1, CASNonSurgCptCode2, CASNonSurgCptCode3, CASNonSurgCptCode4, CASNonSurgCptCode5, CASNonSurgCptCode6, CASOProcInvestigationDone1, CASOProcInvestigationDone2, CASOProcInvestigationDone3, CASInvCode1, CASInvCode2, CASInvCode3, CASOMedSurMgmt1, CASOMedSurMgmt2, CASOMedSurMgmt3)
                    VALUES (@no, @recur, @followReq, @call, @treated, @red, @fol, @payPend, @age, @unit, @p1, @p2, @p3, @p4, @p5, @p6, @pc1, @pc2, @pc3, @pc4, @pc5, @pc6, @s1, @s2, @s3, @s4, @s5, @s6, @sc1, @sc2, @sc3, @sc4, @sc5, @sc6, @i1, @i2, @i3, @ic1, @ic2, @ic3, @m1, @m2, @m3)
                    """, oArgs, ct);
            var o2Args = new
            {
                no, d1 = docs[0].T, d2 = docs[1].T, d3 = docs[2].T, d4 = docs[3].T, d5 = docs[4].T, a1 = anae[0].T, a2 = anae[1].T, a3 = anae[2].T, totalDocs = docs.Count(x => x.T != ""), totalAnae = anae.Count(x => x.T != ""), finals = fin.Count(d => !string.IsNullOrWhiteSpace(d.Code)),
                totalProc = procs.Count(x => x.T != ""), reason = Blank(f.AdmissionReason), by = Blank(f.ReferredBy), firstDoc = At(f.FirstDocDate), subDoc = At(f.SubsequentDocDate), io = Nz(f.IntExtTakeOver), hosName = Blank(f.HospitalName)
            };
            if (await s.ExecuteAsync("""
                UPDATE CASOthersTwo SET CASAttendDoctor1 = @d1, CASAttendDoctor2 = @d2, CASAttendDoctor3 = @d3, CASAttendDoctor4 = @d4, CASAttendDoctor5 = @d5, CASAnaesName1 = @a1, CASAnaesName2 = @a2, CASAnaesName3 = @a3, CASTotalDoctors = @totalDocs, CASTotalAnaes = @totalAnae,
                    CASTotalFinalDiag = @finals, CASTotalProc = @totalProc, CASAdmissionReason = @reason, ReferedBy = @by, CASFirstDocDate = @firstDoc, CASSubSeqDocDate = @subDoc, CASIntExtTO = @io, CASHOSName = @hosName
                WHERE CASNumber = @no
                """, o2Args, ct) == 0)
                await s.ExecuteAsync("""
                    INSERT INTO CASOthersTwo (CasNumber, CASAttendDoctor1, CASAttendDoctor2, CASAttendDoctor3, CASAttendDoctor4, CASAttendDoctor5, CASAnaesName1, CASAnaesName2, CASAnaesName3, CASTotalDoctors, CASTotalAnaes, CASTotalFinalDiag, CASTotalProc, CASAdmissionReason, ReferedBy, CASFirstDocDate, CASSubSeqDocDate, CASIntExtTO, CASHOSName)
                    VALUES (@no, @d1, @d2, @d3, @d4, @d5, @a1, @a2, @a3, @totalDocs, @totalAnae, @finals, @totalProc, @reason, @by, @firstDoc, @subDoc, @io, @hosName)
                    """, o2Args, ct);

            if (repudiating)
            {
                var repArgs = new { no, c1 = repCodes[0], c2 = repCodes.Count > 1 ? repCodes[1] : "", c3 = repCodes.Count > 2 ? repCodes[2] : "", user, now, doc = T(f.Document), per = T(f.Personnel), att = T(f.Attention), und = f.Underlying ? "Y" : "N" };
                if (await s.ExecuteAsync("UPDATE CASRepudiation SET CASREPCode1 = @c1, CASREPCode2 = @c2, CASREPCode3 = @c3, CASREPIssuedBy = @user, CASREPDate = @now, CASDocument = @doc, CASPersonnel = @per, CASAttention = @att, CASUnderlying = @und WHERE CASNumber = @no", repArgs, ct) == 0)
                    await s.ExecuteAsync("INSERT INTO CASRepudiation (CasNumber, CASREPCode1, CASREPCode2, CASREPCode3, CASREPIssuedBy, CASREPDate, CASDocument, CASPersonnel, CASAttention, CASUnderlying) VALUES (@no, @c1, @c2, @c3, @user, @now, @doc, @per, @att, @und)", repArgs, ct);
            }
            await s.CommitAsync(ct);
        }
        catch
        {
            if (oldRef is not null)
                await using (var undo = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
                {
                    await undo.ExecuteAsync(refUpdate, new { no, status = T(S(oldRef, "CASStatus")), claim = T(S(oldRef, "CASClaimability")), admitted = D(oldRef, "CASDateAdmitted"), discharged = D(oldRef, "CASDischargeDate"), pending = Nz(S(oldRef, "CASPending")), link = Nz(S(oldRef, "LINKCASNo")), top = Nz(S(oldRef, "CASTopupLmt")), payPending = Nz(S(oldRef, "CASPayPending")) }, ct);
                    await undo.CommitAsync(ct);
                }
            throw;
        }
        audit.Record(new AuditEntry("Case.Adjust", "CAS", no, user, new Dictionary<string, object?> { ["before"] = before.Form with { Remarks = null }, ["beforeStatus"] = before.Status, ["after"] = f with { Remarks = null }, ["afterStatus"] = status }));
    }

    private static List<(string T, string C)> Fill(IReadOnlyList<CaseProcedureInput>? l, int n) { var x = (l ?? []).Select(p => (T(p.Text), T(p.CptCode))).ToList(); while (x.Count < n) x.Add(("", "")); return x; }
    private static List<(string T, string C)> Fill(IReadOnlyList<string>? l, int n) { var x = (l ?? []).Select(p => (T(p), "")).ToList(); while (x.Count < n) x.Add(("", "")); return x; }
    private static List<(string T, string C)> FillInv(IReadOnlyList<CaseInvestigationInput>? l) { var x = (l ?? []).Select(p => (T(p.Done), T(p.Code))).ToList(); while (x.Count < 3) x.Add(("", "")); return x; }
    private static List<CaseDiagnosisInput> Pad(IReadOnlyList<CaseDiagnosisInput>? l) { var x = (l ?? []).ToList(); while (x.Count < 3) x.Add(new(null, null, null)); return x; }
    private static object Diag(string no, List<CaseDiagnosisInput> d, string? user = null, DateTime? now = null) => new
    {
        n = no, c1 = Nz(Blank(d[0].Code)), c2 = Nz(Blank(d[1].Code)), c3 = Nz(Blank(d[2].Code)), t1 = Nz(Blank(d[0].Text)), t2 = Nz(Blank(d[1].Text)), t3 = Nz(Blank(d[2].Text)), d1 = At(d[0].Date), d2 = At(d[1].Date), d3 = At(d[2].Date), user, now
    };
}
