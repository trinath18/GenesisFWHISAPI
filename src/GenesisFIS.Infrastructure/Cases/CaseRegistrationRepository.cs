using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

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
               ISNULL(g.CoPolicyExpDate, m.MBMPayorExpDate) AS MBMPayorExpDate, m.MBMAvailableLimit, m.MBMTopupInd, m.MBMTerminateStatus, m.MBMRenewFlag, m.MBMTakeover,
               m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.MBMPostCode, m.CTYCode, m.STACode
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
        // Legacy GetAnnualLimit: the plan's annual limit in force on the policy's effective date.
        var annual = (await QueryAsync(GenesisDb.Main, """
            SELECT TOP 1 AnnLimit FROM AnnualLimit WHERE PLNCode = @plan AND INSCode = @ins AND HLTCode = @health AND (@eff IS NULL OR AnnualEffDate <= @eff) ORDER BY AnnualEffDate DESC
            """, new { plan, ins = T(S(m, "INSCode")), health, eff = eff?.ToDateTime(TimeOnly.MinValue) }, ct)).Select(r => Dec(r, "AnnLimit")).FirstOrDefault();
        return new CaseMemberView(number, T(S(m, "MBMName")), Blank(S(m, "MBMIcBcPp")), Blank(S(m, "MBMPolicyNo")), health, T(S(m, "INSCode")), plan, payor, status, Day(m, "MBMDOB"), eff, Day(m, "MBMPayorExpDate"), limit, topUp, terminated, message, patients, blocked, warnings,
            Blank(S(m, "MBMRenewFlag")), Blank(S(m, "MBMTakeover")), annual, Blank(S(m, "MBMAddress1")), Blank(S(m, "MBMAddress2")), Blank(S(m, "MBMAddress3")), Blank(S(m, "MBMPostCode")),
            Blank(S(m, "CTYCode")), Blank(S(m, "STACode")));
    }

    public async Task<CaseMemberExtras?> ExtrasAsync(string memberNumber, CancellationToken ct = default)
    {
        var number = T(memberNumber);
        if (number.Length == 0 || number.Length > 15 || number.Any(char.IsControl)) throw new BusinessRuleException("Please key in Membership No.!", field: "memberNumber");
        var m = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PLNCode FROM MBM WHERE MBMNumber = @n", new { n = number }, ct)).FirstOrDefault();
        if (m is null) return null;
        var principal = (await QueryAsync(GenesisDb.Main, "SELECT MBMExclusion FROM MBMOthersTwo WHERE MBMNumber = @n", new { n = number }, ct)).Select(r => Blank(S(r, "MBMExclusion"))).FirstOrDefault();
        var covered = (await QueryAsync(GenesisDb.Main, "SELECT t.MBMCCoverID, c.MBMCName, t.MBMCExclusion FROM MBMCoveredPersonsTwo t LEFT JOIN MBMCoveredPersons c ON c.MBMCNumber = t.MBMCNumber AND c.MBMCCoverID = t.MBMCCoverID WHERE t.MBMCNumber = @n AND t.MBMCExclusion <> '' ORDER BY t.MBMCCoverID", new { n = number }, ct))
            .Select(r => new CaseCoveredExclusion(T(S(r, "MBMCCoverID")), Blank(S(r, "MBMCName")), Blank(S(r, "MBMCExclusion")))).ToList();
        var note = (await QueryAsync(GenesisDb.Main, "SELECT MBMMmgtNotes FROM MBMTwo WHERE MBMNumber = @n", new { n = number }, ct)).Select(r => Blank(S(r, "MBMMmgtNotes"))).FirstOrDefault();
        var surg = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PLNCode, SurgMainCat, SurgSubCat, SurgPerc FROM PLNSurgOperation WHERE PAYCode = @payor AND PLNCode = @plan ORDER BY SurgMainCat, SurgSubCat", new { payor = T(S(m, "PAYCode")), plan = T(S(m, "PLNCode")) }, ct))
            .Select(r => new CaseSurgicalSchedule(T(S(r, "PAYCode")), T(S(r, "PLNCode")), T(S(r, "SurgMainCat")), T(S(r, "SurgSubCat")), T(S(r, "SurgPerc")))).ToList();
        return new CaseMemberExtras(principal, covered, note, surg);
    }

    private static string CaseNo(string s) { var c = T(s); if (c.Length is 0 or > 15 || c.Any(ch => !char.IsLetterOrDigit(ch))) throw new BusinessRuleException("Invalid case number.", field: "caseNumber"); return c; }

    public async Task<CaseLgResult> IssueLgAsync(string caseNumber, CaseLgRequest r, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        if (CaseLgRules.Validate(r) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var amount = Math.Round(r.Amount!.Value, 2);
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASNumber, c.CASStatus, c.MBMNumber, c.MBMPatientCovID, m.PAYCode, m.PLNCode, m.INSCode, m.MBMMemberType, m.MBMTopupInd, m.MBMPayorEffDate
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Case '{no}' does not exist.");
        if (T(S(c, "CASStatus")) != "O") throw new BusinessRuleException("A guarantee letter can only be issued for an open case.");
        var (member, patient, plan, ins, payor) = (T(S(c, "MBMNumber")), T(S(c, "MBMPatientCovID")), T(S(c, "PLNCode")), T(S(c, "INSCode")), T(S(c, "PAYCode")));
        if (T(S(c, "MBMTopupInd")) == "Y") throw new BusinessRuleException("Guarantee letters for top-up policies are not migrated yet; use the legacy application.");
        var pln = (await QueryAsync(GenesisDb.Main, "SELECT PLNLGType, PLNLifeTimeStatus, PLNPerVisit, PLNRBType, PLNBTBG FROM PLN WHERE PLNCode = @plan AND PAYCode = @payor", new { plan, payor }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound($"Plan '{plan}' of payor '{payor}' does not exist.");
        if (T(S(pln, "PLNLifeTimeStatus")) == "Y") throw new BusinessRuleException("Guarantee letters for lifetime-limit plans are not migrated yet; use the legacy application.");

        var eff = D(c, "MBMPayorEffDate") ?? DateTime.Today;
        var balance = await AnnualBalance.ForAsync((db, sql, args) => QueryAsync(db, sql, args, ct), member, patient, plan, ins, S(c, "MBMMemberType"), eff)
            ?? throw BusinessRuleException.NotFound("No annual limit is defined for this plan.");
        if (amount > balance) throw new BusinessRuleException($"Please Check Balance Limit (available {balance:N2}).");

        var letter = T(S(pln, "PLNLGType")); if (letter.Length == 0) letter = "L";
        if (letter != "C" && T(S(pln, "PLNBTBG")) == "Y") letter = "B";
        int seq;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
        {
            var current = (await s.QueryAsync("SELECT TOP 1 LGSequence FROM LGSeq WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @no", new { no }, ct)).Select(x => I(x, "LGSequence") ?? 0).Cast<int?>().FirstOrDefault();
            if (current is null) { seq = 1; await s.ExecuteAsync("INSERT INTO LGSeq (CASNumber, LGSequence) VALUES (@no, 1)", new { no }, ct); }
            else { seq = current.Value + 1; await s.ExecuteAsync("UPDATE LGSeq SET LGSequence = @seq WHERE CASNumber = @no", new { seq, no }, ct); }
            await s.ExecuteAsync("""
                INSERT INTO CASLG (CASENo, LGNo, LGDate, LGAmt, LGIssuedBy, LGStatus, LGRemark, PLNPerVisit, PLNRBType, LGUserTime, LGSystemTime)
                VALUES (@no, @seq, @today, @amount, @user, @letter, @remark, @perVisit, @rb, @time, @sys)
                """, new { no, seq, today = DateTime.Today, amount, user, letter, remark = Blank(r.Remark) ?? "", perVisit = T(S(pln, "PLNPerVisit")) == "Y" ? "Y" : "", rb = T(S(pln, "PLNRBType")), time = r.Time!.Trim(), sys = DateTime.Now.ToString("HH:mm") }, ct);
            await s.CommitAsync(ct);
        }
        var number = (letter == "B" ? "B" : "L") + no + "-" + seq;
        audit.Record(new AuditEntry("Case.IssueLg", "CASLG", number, user, new Dictionary<string, object?> { ["amount"] = amount, ["balance"] = balance, ["letter"] = letter }));
        return new CaseLgResult(number, seq, letter, balance, amount);
    }

    public async Task<CaseLetterData?> LetterAsync(string caseNumber, string kind, string user, CancellationToken ct = default, int? lgNo = null)
    {
        var no = CaseNo(caseNumber);
        if (!CaseLetterKinds.All.Contains(kind)) throw new BusinessRuleException("Unknown letter.", field: "kind");
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASNumber, c.MBMNumber, c.MBMPatientCovID, c.CASDateAdmitted, c.CASDischargeDate, c.CASHOSCode, m.MBMName, m.MBMIcBcPp, m.MBMPolicyNo, m.PAYCode, m.MBMPayorExpDate, m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.MBMPostCode, m.CTYCode, m.STACode,
                   cp.MBMCName, cp.MBMCICBCPPNo, o2.CASAdmissionReason, o2.CASHOSName, h.HOSName, h.HOSContact, h.HOSFaxNo, y.PAYCompanyName, y.PAYAddress1, y.PAYAddress2, y.PAYAddress3, y.PAYAdmissionContact
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber
                 LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID
                 LEFT JOIN CASOthersTwo o2 ON o2.CASNumber = c.CASNumber LEFT JOIN HOS h ON h.HOSCode = c.CASHOSCode LEFT JOIN PAY y ON y.PAYCode = m.PAYCode
            WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (c is null) return null;
        var principal = T(S(c, "MBMName")); var covId = T(S(c, "MBMPatientCovID"));
        var patient = covId == "00" ? principal : T(S(c, "MBMCName")); var ic = covId == "00" ? Blank(S(c, "MBMIcBcPp")) : Blank(S(c, "MBMCICBCPPNo"));
        var hospital = Blank(S(c, "CASHOSName")) ?? Blank(S(c, "HOSName"));

        string? lgNumber = null; decimal? lgAmount = null; string? rb = null, remark = null;
        if (kind is CaseLetterKinds.Lg or CaseLetterKinds.Btbg)
        {
            var lg = (await QueryAsync(GenesisDb.Maintenance, "SELECT TOP 1 LGNo, LGAmt, LGStatus, LGRemark, PLNRBType FROM CASLG WHERE CASENo = @no AND (@lgNo IS NULL OR LGNo = @lgNo) ORDER BY LGNo DESC", new { no, lgNo }, ct)).FirstOrDefault()
                ?? throw new BusinessRuleException("No guarantee letter has been issued for this case.");
            lgNumber = (T(S(lg, "LGStatus")) == "B" ? "B" : "L") + no + "-" + T(S(lg, "LGNo")); lgAmount = Dec(lg, "LGAmt"); rb = Blank(S(lg, "PLNRBType")); remark = Blank(S(lg, "LGRemark"));
        }
        var reps = new List<CaseRepudiationText>(); string? doc = null, per = null, att = null, und = null;
        if (kind == CaseLetterKinds.Repudiation)
        {
            var rep = (await QueryAsync(GenesisDb.Main, "SELECT CASREPCode1, CASREPCode2, CASREPCode3, CASDocument, CASPersonnel, CASAttention, CASUnderlying FROM CASRepudiation WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault()
                ?? throw new BusinessRuleException("This case has no repudiation.");
            foreach (var code in new[] { T(S(rep, "CASREPCode1")), T(S(rep, "CASREPCode2")), T(S(rep, "CASREPCode3")) }.Where(x => x.Length > 0))
            {
                var rc = (await QueryAsync(GenesisDb.Main, "SELECT r.RepDescription, g.RepCatName, g.RepCatDescription FROM RepCode r LEFT JOIN RepCodeCategory g ON g.RepCatCode = r.RepCatCode WHERE r.RepCode = @code", new { code }, ct)).FirstOrDefault();
                reps.Add(new CaseRepudiationText(code, rc is null ? null : Blank(S(rc, "RepCatName")), rc is null ? null : Blank(S(rc, "RepCatDescription")), rc is null ? null : Blank(S(rc, "RepDescription"))));
            }
            async Task<string?> Sel(string code) => string.IsNullOrWhiteSpace(code) ? null : (await QueryAsync(GenesisDb.Maintenance, "SELECT REPDescription FROM REPSelection WHERE REPCode = @code", new { code = T(code) }, ct)).Select(x => Blank(S(x, "REPDescription"))).FirstOrDefault();
            (doc, per, att) = (await Sel(S(rep, "CASDocument") ?? ""), await Sel(S(rep, "CASPersonnel") ?? ""), await Sel(S(rep, "CASAttention") ?? ""));
            if (T(S(rep, "CASUnderlying")) == "Y")
                und = string.Join("; ", (await QueryAsync(GenesisDb.Main, "SELECT CASUDReasons1, CASUDReasons2, CASUDReasons3 FROM CAS_UD WHERE CASNumber = @no", new { no }, ct)).SelectMany(x => new[] { S(x, "CASUDReasons1"), S(x, "CASUDReasons2"), S(x, "CASUDReasons3") }).Select(T).Where(x => x.Length > 0));
        }
        DateOnly? Dd(string col) => D(c, col) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
        return new CaseLetterData(kind, no, T(S(c, "MBMNumber")), Blank(S(c, "MBMPolicyNo")), patient, ic, principal, Blank(S(c, "MBMAddress1")), Blank(S(c, "MBMAddress2")), Blank(S(c, "MBMPostCode")), Blank(S(c, "CTYCode")), Blank(S(c, "STACode")),
            hospital, Blank(S(c, "HOSContact")), Blank(S(c, "HOSFaxNo")), Dd("CASDateAdmitted"), Dd("CASDischargeDate"), Dd("MBMPayorExpDate"), Blank(S(c, "PAYCompanyName")), Blank(S(c, "PAYAddress1")), Blank(S(c, "PAYAddress2")), Blank(S(c, "PAYAddress3")), Blank(S(c, "PAYAdmissionContact")),
            Blank(S(c, "CASAdmissionReason")), lgNumber, lgAmount, rb, remark, user, DateOnly.FromDateTime(DateTime.Today), reps, doc, per, att, und);
    }

    private static string Nz(string? s) => s ?? "";

    public async Task<CaseLetterData> DraftLetterAsync(string kind, CaseDraftLetterRequest r, string user, CancellationToken ct = default)
    {
        if (!CaseLetterKinds.Drafts.Contains(kind)) throw new BusinessRuleException("Unknown letter.", field: "kind");
        // Legacy cmdDocReqAdm_Click / cmdDocReqDis_Click / cmdTHDrugs_Click checks, in order.
        if (string.IsNullOrWhiteSpace(r.MemberNumber)) throw new BusinessRuleException("Please Enter Membership Information", field: "memberNumber");
        if (string.IsNullOrWhiteSpace(r.PatientId)) throw new BusinessRuleException("Please Enter Patient Information", field: "patientId");
        if (string.IsNullOrWhiteSpace(r.HospitalCode)) throw new BusinessRuleException("Please Enter Hospital Information", field: "hospitalCode");
        if (r.Admitted is null) throw new BusinessRuleException("Please Enter Admission Date", field: "dateAdmitted");
        var c = (await QueryAsync(GenesisDb.Main, """

            SELECT m.MBMNumber, m.MBMName, m.MBMIcBcPp, m.MBMPolicyNo, m.MBMPayorExpDate, m.MBMAddress1, m.MBMAddress2, m.MBMPostCode, m.CTYCode, m.STACode, cp.MBMCName, cp.MBMCICBCPPNo,
                   y.PAYCompanyName, y.PAYAddress1, y.PAYAddress2, y.PAYAddress3, y.PAYAdmissionContact
            FROM MBM m LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = m.MBMNumber AND cp.MBMCCoverID = @patient LEFT JOIN PAY y ON y.PAYCode = m.PAYCode
            WHERE m.MBMNumber = @member
            """, new { member = r.MemberNumber.Trim(), patient = r.PatientId.Trim() }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound("No record found!");
        var h = (await QueryAsync(GenesisDb.Main, "SELECT HOSName, HOSContact, HOSFaxNo FROM HOS WHERE HOSCode = @code", new { code = r.HospitalCode.Trim() }, ct)).FirstOrDefault();
        var principal = S(c, "MBMName")?.Trim() ?? "";
        var own = r.PatientId.Trim() == "00";
        var patient = own ? principal : Blank(S(c, "MBMCName")) ?? principal;
        var hospital = Blank(r.HospitalName) ?? (h is null ? r.HospitalCode.Trim() : Blank(S(h, "HOSName")));
        DateOnly? expiry = D(c, "MBMPayorExpDate") is { } e ? DateOnly.FromDateTime(e) : null;
        return new CaseLetterData(kind, Blank(r.CaseNumber) ?? "-", S(c, "MBMNumber")!.Trim(), Blank(S(c, "MBMPolicyNo")), patient, own ? Blank(S(c, "MBMIcBcPp")) : Blank(S(c, "MBMCICBCPPNo")), principal,
            Blank(S(c, "MBMAddress1")), Blank(S(c, "MBMAddress2")), Blank(S(c, "MBMPostCode")), Blank(S(c, "CTYCode")), Blank(S(c, "STACode")), hospital, h is null ? null : Blank(S(h, "HOSContact")),
            h is null ? null : Blank(S(h, "HOSFaxNo")), r.Admitted, r.Discharged, expiry, Blank(S(c, "PAYCompanyName")), Blank(S(c, "PAYAddress1")), Blank(S(c, "PAYAddress2")),
            Blank(S(c, "PAYAddress3")), Blank(S(c, "PAYAdmissionContact")), null, null, null, null, null, user, DateOnly.FromDateTime(DateTime.Today), [], null, null, null, null);
    }

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

            var procs = Fill(r.Procedures, 6); var nons = Fill(r.NonSurgicalProcedures, 6); var invs = FillInv(r.Investigations); var med = Fill(r.MedicalManagement, 3); var docs = Fill(r.AttendingDoctors, 5); var anae = Fill(r.Anaesthetists, 3);
            await s.ExecuteAsync("""
                INSERT INTO CASOthers (CasNumber, CASOCondWillRecur, CASOFollowUpRequired, CASFirstCall, CASFirstTreated, CASRedAmt, CASFolUp, CASPayPending, CASPatientAge, CASPatientDayYr,
                    CASOPROCRequired1, CASOPROCRequired2, CASOPROCRequired3, CASOPROCRequired4, CASOPROCRequired5, CASOPROCRequired6, CASCptCode1, CASCptCode2, CASCptCode3, CASCptCode4, CASCptCode5, CASCptCode6,
                    CASONonSurgPROCRequired1, CASONonSurgPROCRequired2, CASONonSurgPROCRequired3, CASONonSurgPROCRequired4, CASONonSurgPROCRequired5, CASONonSurgPROCRequired6,
                    CASNonSurgCptCode1, CASNonSurgCptCode2, CASNonSurgCptCode3, CASNonSurgCptCode4, CASNonSurgCptCode5, CASNonSurgCptCode6,
                    CASOProcInvestigationDone1, CASOProcInvestigationDone2, CASOProcInvestigationDone3, CASInvCode1, CASInvCode2, CASInvCode3, CASOMedSurMgmt1, CASOMedSurMgmt2, CASOMedSurMgmt3)
                VALUES (@n, @recur, @followReq, @call, @treated, 0, @fol, @payPend, @age, @unit,
                    @p1, @p2, @p3, @p4, @p5, @p6, @pc1, @pc2, @pc3, @pc4, @pc5, @pc6, @s1, @s2, @s3, @s4, @s5, @s6, @sc1, @sc2, @sc3, @sc4, @sc5, @sc6, @i1, @i2, @i3, @ic1, @ic2, @ic3, @m1, @m2, @m3)
                """, new
            {
                n = caseNumber, recur = Nz(r.ConditionWillRecur), followReq = Nz(r.FollowUpRequired), call = Blank(r.FirstCall), treated = Blank(r.FirstTreated), fol = Nz(r.FollowUp), payPend = Nz(r.PayPending), age = r.PatientAge!.Trim(), unit = r.AgeUnit,
                p1 = procs[0].T, p2 = procs[1].T, p3 = procs[2].T, p4 = procs[3].T, p5 = procs[4].T, p6 = procs[5].T, pc1 = procs[0].C, pc2 = procs[1].C, pc3 = procs[2].C, pc4 = procs[3].C, pc5 = procs[4].C, pc6 = procs[5].C,
                s1 = nons[0].T, s2 = nons[1].T, s3 = nons[2].T, s4 = nons[3].T, s5 = nons[4].T, s6 = nons[5].T, sc1 = nons[0].C, sc2 = nons[1].C, sc3 = nons[2].C, sc4 = nons[3].C, sc5 = nons[4].C, sc6 = nons[5].C,
                i1 = invs[0].T, i2 = invs[1].T, i3 = invs[2].T, ic1 = invs[0].C, ic2 = invs[1].C, ic3 = invs[2].C, m1 = med[0].T, m2 = med[1].T, m3 = med[2].T
            }, ct);
            await s.ExecuteAsync("""
                INSERT INTO CASOthersTwo (CasNumber, CASAttendDoctor1, CASAttendDoctor2, CASAttendDoctor3, CASAttendDoctor4, CASAttendDoctor5, CASAnaesName1, CASAnaesName2, CASAnaesName3, CASTotalDoctors, CASTotalAnaes, CASTotalFinalDiag, CASTotalProc,
                                         CASAdmissionReason, ReferedBy, CASFirstDocDate, CASSubSeqDocDate, CASIntExtTO, CASHOSName)
                VALUES (@n, @d1, @d2, @d3, @d4, @d5, @a1, @a2, @a3, @totalDocs, @totalAnae, @finals, @totalProc, @reason, @by, @firstDoc, @subDoc, @io, @hosName)
                """, new
            {
                n = caseNumber, d1 = docs[0].T, d2 = docs[1].T, d3 = docs[2].T, d4 = docs[3].T, d5 = docs[4].T, a1 = anae[0].T, a2 = anae[1].T, a3 = anae[2].T, totalDocs = docs.Count(x => x.T != ""), totalAnae = anae.Count(x => x.T != ""),
                finals = fin.Count(d => !string.IsNullOrWhiteSpace(d.Code)), totalProc = procs.Count(x => x.T != ""), reason = Blank(r.AdmissionReason), by = Blank(r.ReferredBy), firstDoc = At(r.FirstDocDate), subDoc = At(r.SubsequentDocDate), io = Nz(r.IntExtTakeOver), hosName = Blank(r.HospitalName)
            }, ct);
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
        return new CaseRegistrationResult(caseNumber, member.MemberNumber, patientId, $"Record Saved, Your Case No is : {caseNumber}", CaseLetterKinds.AfterRegistration(today, member.ExpiryDate, repudiating));
    }

    private static List<(string T, string C)> Fill(IReadOnlyList<CaseProcedureInput>? l, int n) { var x = (l ?? []).Select(p => (T(p.Text), T(p.CptCode))).ToList(); while (x.Count < n) x.Add(("", "")); return x; }
    private static List<(string T, string C)> Fill(IReadOnlyList<string>? l, int n) { var x = (l ?? []).Select(p => (T(p), "")).ToList(); while (x.Count < n) x.Add(("", "")); return x; }
    private static List<(string T, string C)> FillInv(IReadOnlyList<CaseInvestigationInput>? l) { var x = (l ?? []).Select(p => (T(p.Done), T(p.Code))).ToList(); while (x.Count < 3) x.Add(("", "")); return x; }
    private static List<CaseDiagnosisInput> Pad(IReadOnlyList<CaseDiagnosisInput>? l) { var x = (l ?? []).ToList(); while (x.Count < 3) x.Add(new(null, null, null)); return x; }
    private static object Diag(string caseNumber, List<CaseDiagnosisInput> d) => new
    {
        n = caseNumber, c1 = Nz(Blank(d[0].Code)), c2 = Nz(Blank(d[1].Code)), c3 = Nz(Blank(d[2].Code)), t1 = Nz(Blank(d[0].Text)), t2 = Nz(Blank(d[1].Text)), t3 = Nz(Blank(d[2].Text)), d1 = At(d[0].Date), d2 = At(d[1].Date), d3 = At(d[2].Date)
    };
}







