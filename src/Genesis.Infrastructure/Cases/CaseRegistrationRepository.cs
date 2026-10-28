using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

internal sealed class CaseRegistrationRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseRegistrationRepository
{
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static IReadOnlyList<CaseOption> Codes(IEnumerable<IDictionary<string, object?>> rows, string code, string name) => rows.Select(r => new CaseOption(T(S(r, code)), T(S(r, name)))).ToList();

    public async Task<CaseRegistrationOptions> OptionsAsync(CancellationToken ct = default)
    {
        var payTypes = Codes(await QueryAsync(GenesisDb.Main, "SELECT PayTypeCode, PayTypeName FROM PayType ORDER BY PayTypeCode", null, ct), "PayTypeCode", "PayTypeName");
        return new CaseRegistrationOptions(
            CaseRegistrationRules.Claimability, [],
            payTypes.Where(p => p.Code is "C" or "R").ToList(), payTypes.Where(p => p.Code is not ("C" or "R")).ToList(),
            Codes(await QueryAsync(GenesisDb.Main, "SELECT ModalityCode, ModalityName FROM TreatmentModality ORDER BY ModalityCode", null, ct), "ModalityCode", "ModalityName"),
            Codes(await QueryAsync(GenesisDb.Main, "SELECT ClaimNatureCode, ClaimNatureName FROM ClaimNature ORDER BY ClaimNatureCode", null, ct), "ClaimNatureCode", "ClaimNatureName"),
            Codes(await QueryAsync(GenesisDb.Maintenance, "SELECT SpecAppCode, SpecAppDesc FROM SpecApprove ORDER BY SpecAppCode", null, ct), "SpecAppCode", "SpecAppDesc"),
            Codes(await QueryAsync(GenesisDb.Main, "SELECT ICDCode, ICDDescription FROM ICDOriginal ORDER BY ICDCode", null, ct), "ICDCode", "ICDDescription"),
            Codes(await QueryAsync(GenesisDb.Main, "SELECT RepCode, RepDescription FROM RepCode ORDER BY RepCode", null, ct), "RepCode", "RepDescription"),
            Codes(await QueryAsync(GenesisDb.Maintenance, "SELECT REPCode, REPDescription FROM REPSelection ORDER BY REPCode", null, ct), "REPCode", "REPDescription"));
    }

    public async Task<IReadOnlyList<CaseOption>> HospitalsAsync(string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        return Codes(await QueryAsync(GenesisDb.Main, "SELECT TOP (300) HOSCode, HOSName FROM HOS WHERE @like IS NULL OR HOSName LIKE @like OR HOSCode LIKE @like ORDER BY HOSName", new { like }, ct), "HOSCode", "HOSName");
    }

    private static readonly string MemberSql = """
        SELECT m.MBMNumber, m.MBMName, m.MBMIcBcPp, m.MBMPolicyNo, m.HLTCode, m.INSCode, m.PLNCode, m.PAYCode, m.MBMStatus, m.MBMDOB, m.MBMPayorEffDate,
               ISNULL(g.CoPolicyExpDate, m.MBMPayorExpDate) AS MBMPayorExpDate, m.MBMAvailableLimit, m.MBMTopupInd, m.MBMTerminateStatus
        FROM MBM m LEFT JOIN GroupCompanyFW g ON g.CoPolicyNo = m.MBMPolicyNo AND g.CoGRPPaycode = m.PAYCode
        WHERE m.MBMNumber = @n
        """;

    public async Task<CaseMemberView?> MemberAsync(string memberNumber, CancellationToken ct = default)
    {
        var number = T(memberNumber);
        if (number.Length == 0 || number.Length > 15 || number.Any(char.IsControl)) throw new BusinessRuleException("Please key in Membership No.!", field: "memberNumber");
        var m = (await QueryAsync(GenesisDb.Main, MemberSql, new { n = number }, ct)).FirstOrDefault();
        if (m is null) return null;
        var plan = T(S(m, "PLNCode")); var health = T(S(m, "HLTCode")); var payor = T(S(m, "PAYCode"));
        var message = (await QueryAsync(GenesisDb.Main, health == "S" ? "SELECT MessagePromt FROM PLN WHERE PLNCode = @plan" : "SELECT MessagePromt FROM PLN WHERE PLNCode = @plan AND PAYCode = @payor", new { plan, payor }, ct))
            .Select(r => Blank(S(r, "MessagePromt"))).FirstOrDefault(x => x is not null);

        var patients = new List<CasePatient> { new("00", T(S(m, "MBMName")), Blank(S(m, "MBMIcBcPp")), "P", Day(m, "MBMDOB")) };
        if (T(S(m, "INSCode")) is "F" or "H")
            patients.AddRange((await QueryAsync(GenesisDb.Main, "SELECT MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCRELCode, MBMCDOB FROM MBMCoveredPersons WHERE MBMCNumber = @n ORDER BY MBMCCoverID", new { n = number }, ct))
                .Select(r => new CasePatient(T(S(r, "MBMCCoverID")), T(S(r, "MBMCName")), Blank(S(r, "MBMCICBCPPNo")), T(S(r, "MBMCRELCode")), Day(r, "MBMCDOB"))));

        var (eff, status, limit) = (Day(m, "MBMPayorEffDate"), T(S(m, "MBMStatus")), Dec(m, "MBMAvailableLimit"));
        var topUp = T(S(m, "MBMTopupInd")) == "Y"; var terminated = T(S(m, "MBMTerminateStatus")) == "Y";
        var today = DateOnly.FromDateTime(DateTime.Today);
        string? blocked = null; var warnings = new List<string>();
        if (status == "C") blocked = "Policy has been cancelled!";
        else if (eff is { } e && e > today) blocked = "Policy not yet activated!";
        else if (topUp && (limit ?? 0) <= 0) blocked = "Limit Exceeded. Case Registration is not allowed";
        if (eff is { } e2 && e2 <= today && today.DayNumber - e2.DayNumber <= 30) warnings.Add("Policy has just been activated less than 30 days ago.");
        if (limit is <= 2000) warnings.Add("The available annual limit is 2,000 or less.");
        if (terminated) warnings.Add("The policy has a termination status.");
        return new CaseMemberView(number, T(S(m, "MBMName")), Blank(S(m, "MBMIcBcPp")), Blank(S(m, "MBMPolicyNo")), health, T(S(m, "INSCode")), plan, payor, status, Day(m, "MBMDOB"), eff, Day(m, "MBMPayorExpDate"), limit, topUp, terminated, message, patients, blocked, warnings);
    }

    private static string Nz(string? s) => s ?? "";

    public async Task<CaseRegistrationResult> RegisterAsync(CaseRegistrationRequest r, string user, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (CaseRegistrationRules.Validate(r, today) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var member = await MemberAsync(r.MemberNumber!, ct) ?? throw BusinessRuleException.NotFound("No record found!");
        if (member.BlockedReason is { } blocked) throw new BusinessRuleException(blocked);
        var patientId = T(r.PatientId);
        if (member.Patients.All(x => x.CoverId != patientId)) throw new BusinessRuleException("Please Enter Patient Information", field: "patientId");

        var hospital = (await QueryAsync(GenesisDb.Main, "SELECT HOSCode, HOSName FROM HOS WHERE HOSCode = @c", new { c = T(r.HospitalCode) }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("The hospital does not exist.");
        var hospitalName = T(S(hospital, "HOSName"));
        if (CaseRegistrationRules.GenericHospitals.Contains(hospitalName.ToUpperInvariant()) && string.IsNullOrWhiteSpace(r.HospitalName)) throw new BusinessRuleException("Please Enter Hospital Name or Clinic Name!", field: "hospitalName");
        foreach (var (code, table, col) in new[] { (r.Modality, "TreatmentModality", "ModalityCode"), (r.Nature, "ClaimNature", "ClaimNatureCode") })
            if (Blank(code) is { } c && await ScalarAsync<int>(GenesisDb.Main, $"SELECT COUNT(*) FROM {table} WHERE {col} = @c", new { c }, ct) == 0) throw BusinessRuleException.NotFound($"'{c}' is not a valid {col}.");
        if (Blank(r.StayType) is { } stay && await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM PayType WHERE PayTypeCode = @c", new { c = stay }, ct) == 0) throw BusinessRuleException.NotFound("The stay type does not exist.");
        foreach (var icd in (r.FirstDiagnosis ?? []).Concat(r.FinalDiagnosis ?? []).Select(d => Blank(d.Code)).Where(c => c is not null).Distinct())
            if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM ICD WHERE ICDCode = @c", new { c = icd }, ct) == 0) throw BusinessRuleException.NotFound($"ICD code '{icd}' does not exist.");

        var repudiating = CaseRegistrationRules.Repudiating.Contains(r.Claimability!.Trim());
        var repCodes = repudiating ? r.RepudiationCodes!.Select(T).Where(c => c.Length > 0).ToList() : [];
        foreach (var rc in repCodes)
            if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM RepCode WHERE RepCode = @c", new { c = rc }, ct) == 0) throw BusinessRuleException.NotFound($"Repudiation code '{rc}' does not exist.");
        if (repudiating)
            foreach (var (code, prefix, label) in new[] { (T(r.Document), "D", "document"), (T(r.Personnel), "P", "personnel"), (T(r.Attention), "A", "attention") })
                if (!code.StartsWith(prefix) || await ScalarAsync<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM REPSelection WHERE REPCode = @c", new { c = code }, ct) == 0) throw BusinessRuleException.NotFound($"The repudiation {label} selection '{code}' does not exist.");
        foreach (var icd in (r.UnderlyingDiagnosis ?? []).Select(d => Blank(d.Code)).Where(c => c is not null).Distinct())
            if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM ICD WHERE ICDCode = @c", new { c = icd }, ct) == 0) throw BusinessRuleException.NotFound($"ICD code '{icd}' does not exist.");

        var stayDays = r.DateDischarge is { } dd ? dd.DayNumber - r.DateAdmitted!.Value.DayNumber : 0;
        if (stayDays > 5 && !r.AcknowledgeLongStay) throw BusinessRuleException.Conflict("Admission Period Is More Than 5 Days. Confirm to continue.");
        if (member.EffectiveDate is { } eff && today.DayNumber - eff.DayNumber is >= 0 and <= 30 && !r.AcknowledgeRecentPolicy) throw BusinessRuleException.Conflict("Policy has just been activated less than 30 days ago. Confirm to continue.");
        var open = await QueryAsync(GenesisDb.Maintenance, "SELECT CASNumber FROM CASCrossReference WHERE MBMNumber = @n AND CASDateAdmitted = @d AND MBMPatientCovID = @c", new { n = member.MemberNumber, d = At(r.DateAdmitted), c = patientId }, ct);
        if (open.Any() && !r.AcknowledgeOpenCase) throw BusinessRuleException.Conflict($"The patient already has case {T(S(open.First(), "CASNumber"))} admitted on this date. Confirm to continue.");

        var payor = member.Payor!;
        var reimbursement = r.PayType!.Trim() == "R";
        var claimability = r.Claimability!.Trim();

        string caseNumber;
        await using (var q = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
        {
            var current = (await q.QueryAsync("SELECT CAQNumber FROM CAQ WITH (UPDLOCK, HOLDLOCK) WHERE PAYCode = @payor", new { payor }, ct)).Select(x => T(S(x, "CAQNumber"))).FirstOrDefault();
            string next;
            if (current is null) { next = "00001"; await q.ExecuteAsync("INSERT INTO CAQ (PAYCode, CAQNumber) VALUES (@payor, @next)", new { payor, next }, ct); }
            else { next = (int.Parse(current) + 1).ToString("00000"); await q.ExecuteAsync("UPDATE CAQ SET CAQNumber = @next WHERE PAYCode = @payor", new { payor, next }, ct); }
            await q.CommitAsync(ct);
            caseNumber = payor + next;
        }

        var crossRef = new { caseNumber, n = member.MemberNumber, c = patientId, d = At(r.DateAdmitted), hos = T(r.HospitalCode), claim = claimability[..1], link = Nz(Blank(r.LinkCaseNo)), top = Nz(r.TopUpLimit), pay = Nz(r.PayPending), pend = Nz(r.Pending) };
        await using (var x = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
        {
            await x.ExecuteAsync("INSERT INTO CASCrossReference (CasNumber, MBMNumber, MBMPatientCovID, CASStatus, CASPending, CASHOSCode, CASDateAdmitted, CASClaimability, LINKCASNo, CASTopupLmt, CASPayPending) VALUES (@caseNumber, @n, @c, 'O', @pend, @hos, @d, @claim, @link, @top, @pay)", crossRef, ct);
            await x.CommitAsync(ct);
        }

        decimal? limitBefore = null, limitAfter = null;
        try
        {
            await using var s = await Db.BeginSessionAsync(GenesisDb.Main, ct);
            var now = DateTime.Today;
            await s.ExecuteAsync("""
                INSERT INTO CAS (CasNumber, MBMNumber, MBMPatientCovID, CASStatus, CASPending, CASReserveAmount, CASLGAmt, CASDateAdmitted, CASDischargeDate, CASClaimability, CASInd, ModalityCode, CASStayType, CASPAYType, CASClaimAdmin,
                                 CASTokenDate, CASUserToken, LINKCASNo, ClaimNatureCode, CaseOpenBy, CaseRegDate, CASTopupLmt, CASHOSCode, CASDRD, CASQuery, CASTakeOverAmt, CASTakeOverInd, CASTakeOverPlan, CASLGTime, CASPolYear, CASESPRefNo)
                VALUES (@caseNumber, @n, @c, 'O', @pending, @reserve, @lg, @admitted, @discharged, @claim, @ind, @modality, @stay, @payType, @admin, @tokenDate, @tokenUser, @link, @nature, @user, @now, @top, @hos, @drd, @query, @toAmt, @toInd, @toPlan, @lgTime, @polYear, @esp)
                """, new
            {
                caseNumber, n = member.MemberNumber, c = patientId, pending = Nz(r.Pending), reserve = r.ReserveAmount, lg = r.LgAmount, admitted = At(r.DateAdmitted), discharged = At(r.DateDischarge), claim = claimability, ind = Nz(Blank(r.SpecialApproval)),
                modality = Nz(Blank(r.Modality)), stay = Nz(Blank(r.StayType)), payType = r.PayType!.Trim(), admin = reimbursement ? "1" : "0", tokenDate = reimbursement ? (DateTime?)now : null, tokenUser = reimbursement ? user : null,
                link = Nz(Blank(r.LinkCaseNo)), nature = Nz(Blank(r.Nature)), user, now, top = Nz(r.TopUpLimit), hos = T(r.HospitalCode), drd = At(r.DocReceivedDate), query = Nz(r.Query), toAmt = r.TakeOverAmount ?? 0, toInd = Nz(r.TakeOverInd),
                toPlan = Blank(r.TakeOverPlan), lgTime = Blank(r.LgTime), polYear = r.PolicyYear, esp = Blank(r.EspRefNo)
            }, ct);

            var remarks = (r.Remarks ?? []).Select(x => (x ?? "").Replace("\r\n", "~").Replace("\r", "~").Replace("\n", "~")).ToList();
            while (remarks.Count < 5) remarks.Add("");
            await s.ExecuteAsync("INSERT INTO CASRemarks (CasNumber, CASORemarks, CASORemarksTwo) VALUES (@n, @a, @b)", new { n = caseNumber, a = remarks[0][..Math.Min(1900, remarks[0].Length)], b = remarks[0].Length > 1900 ? remarks[0][1900..] : "" }, ct);
            await s.ExecuteAsync("INSERT INTO CASRemarksTwo (CasNumber, CASORemarks, CASORemarksTwo) VALUES (@n, @a, @b)", new { n = caseNumber, a = remarks[1][..Math.Min(1900, remarks[1].Length)], b = remarks[1].Length > 1900 ? remarks[1][1900..] : "" }, ct);
            await s.ExecuteAsync("INSERT INTO CASRemarkThree (CasNumber, CASRemarks, CASRemarksFour, CASRemarksFive) VALUES (@n, @a, @b, @c)", new { n = caseNumber, a = remarks[2], b = remarks[3], c = remarks[4] }, ct);

            var first = Pad(r.FirstDiagnosis); var fin = Pad(r.FinalDiagnosis);
            await s.ExecuteAsync("INSERT INTO DIAFirst (CasNumber, ICDCode1, ICDCode2, ICDCode3, DIAPrelimDiag1, DIAPrelimDiag2, DIAPrelimDiag3, DIADOS1, DIADOS2, DIADOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(caseNumber, first), ct);
            await s.ExecuteAsync("INSERT INTO DIAFinal (CasNumber, ICDCode1, ICDCode2, ICDCode3, DIAFinalDiag1, DIAFinalDiag2, DIAFinalDiag3, DIADOS1, DIADOS2, DIADOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(caseNumber, fin), ct);

            await s.ExecuteAsync("""
                INSERT INTO CASOthers (CasNumber, CASOCondWillRecur, CASOFollowUpRequired, CASFirstCall, CASFirstTreated, CASRedAmt, CASFolUp, CASPayPending, CASPatientAge, CASPatientDayYr)
                VALUES (@n, @recur, @followReq, @call, @treated, 0, @fol, @payPend, @age, @unit)
                """, new { n = caseNumber, recur = Nz(r.ConditionWillRecur), followReq = Nz(r.FollowUpRequired), call = Blank(r.FirstCall), treated = Blank(r.FirstTreated), fol = Nz(r.FollowUp), payPend = Nz(r.PayPending), age = r.PatientAge!.Trim(), unit = r.AgeUnit }, ct);
            await s.ExecuteAsync("""
                INSERT INTO CASOthersTwo (CasNumber, CASTotalDoctors, CASTotalAnaes, CASTotalFinalDiag, CASTotalProc, CASAdmissionReason, ReferedBy, CASIntExtTO, CASHOSName)
                VALUES (@n, 0, 0, @finals, 0, @reason, @by, @io, @hosName)
                """, new { n = caseNumber, finals = fin.Count(d => !string.IsNullOrWhiteSpace(d.Code)), reason = Blank(r.AdmissionReason), by = Blank(r.ReferredBy), io = Nz(r.IntExtTakeOver), hosName = Blank(r.HospitalName) }, ct);
            var ud = Pad(r.UnderlyingDiagnosis);
            await s.ExecuteAsync("INSERT INTO CAS_UD (CasNumber, CASUDCode1, CASUDCode2, CASUDCode3, CASUDReasons1, CASUDReasons2, CASUDReasons3, CASUDDOS1, CASUDDOS2, CASUDDOS3) VALUES (@n, @c1, @c2, @c3, @t1, @t2, @t3, @d1, @d2, @d3)", Diag(caseNumber, ud), ct);

            if (repudiating)
                await s.ExecuteAsync("INSERT INTO CASRepudiation (CasNumber, CASREPCode1, CASREPCode2, CASREPCode3, CASREPIssuedBy, CASREPDate, CASDocument, CASPersonnel, CASAttention, CASUnderlying) VALUES (@n, @c1, @c2, @c3, @user, @now, @doc, @per, @att, @und)",
                    new { n = caseNumber, c1 = repCodes[0], c2 = repCodes.Count > 1 ? repCodes[1] : "", c3 = repCodes.Count > 2 ? repCodes[2] : "", user, now, doc = T(r.Document), per = T(r.Personnel), att = T(r.Attention), und = r.Underlying ? "Y" : "N" }, ct);

            if (r.DeductionAmount is { } red)
            {
                var available = await s.ScalarAsync<decimal?>("SELECT MBMAvailableLimit FROM MBM WITH (UPDLOCK) WHERE MBMNumber = @n", new { n = member.MemberNumber }, ct) ?? 0m;
                limitBefore = available; limitAfter = available > red ? available - red : 0m;
                await s.ExecuteAsync("UPDATE MBM SET MBMAvailableLimit = @limit WHERE MBMNumber = @n", new { limit = limitAfter, n = member.MemberNumber }, ct);
                await s.ExecuteAsync("UPDATE CASOthers SET CASRedAmt = @red WHERE CASNumber = @caseNumber", new { red, caseNumber }, ct);
            }
            await s.CommitAsync(ct);
        }
        catch
        {
            await using var undo = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await undo.ExecuteAsync("DELETE FROM CASCrossReference WHERE CASNumber = @caseNumber", new { caseNumber }, ct);
            await undo.CommitAsync(ct);
            throw;
        }

        audit.Record(new AuditEntry("Case.Register", "CAS", caseNumber, user, new Dictionary<string, object?> { ["request"] = r with { Remarks = null }, ["hospital"] = hospitalName, ["limitBefore"] = limitBefore, ["limitAfter"] = limitAfter }));
        return new CaseRegistrationResult(caseNumber, member.MemberNumber, patientId, $"Record Saved, Your Case No is : {caseNumber}");
    }

    private static List<CaseDiagnosisInput> Pad(IReadOnlyList<CaseDiagnosisInput>? l) { var x = (l ?? []).ToList(); while (x.Count < 3) x.Add(new(null, null, null)); return x; }
    private static object Diag(string caseNumber, List<CaseDiagnosisInput> d) => new
    {
        n = caseNumber, c1 = Nz(Blank(d[0].Code)), c2 = Nz(Blank(d[1].Code)), c3 = Nz(Blank(d[2].Code)), t1 = Nz(Blank(d[0].Text)), t2 = Nz(Blank(d[1].Text)), t3 = Nz(Blank(d[2].Text)), d1 = At(d[0].Date), d2 = At(d[1].Date), d3 = At(d[2].Date)
    };
}


