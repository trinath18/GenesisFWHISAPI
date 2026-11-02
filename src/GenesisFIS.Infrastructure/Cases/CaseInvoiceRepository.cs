using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Invoice > Invoice Processing (legacy frmInvoice). An invoice is one row in ClaimInvoice plus its amounts in ClaimInvoiceActual,
/// ClaimInvoiceActualOthers and ClaimInvoiceNotCover, all in the worksheet database. The procedure entry (legacy frmMMAEntry) edits the case's doctors and
/// procedures in CASOthers / CASOthersTwo (main database), saved with the invoice as legacy SaveCASOthers did. Changing an
/// invoice that is already in a worksheet leaves the worksheet as it was; Worksheet Adjustment recalculates it (the legacy form's partial ClaimLimit update is not copied).
/// </summary>
internal sealed class CaseInvoiceRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseInvoiceRepository
{
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static bool Flag(IDictionary<string, object?> r, string c) => Get(r, c) is bool b ? b : S(r, c) is "1" or "True";

    private static string CaseNo(string s)
    {
        if (!CaseNumber.IsValid(s)) throw new BusinessRuleException("Please Enter Case No!", field: "caseNumber");
        return s.Trim();
    }

    private static IEnumerable<InvoiceField> In(InvoiceTable t) => CaseInvoiceCatalog.Fields.Where(f => f.Table == t);
    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    /// <summary>The case's doctors, medical management, investigations and procedures, every slot included (blank ones as null).</summary>
    private async Task<InvoiceCaseProcedures> ProceduresAsync(string no, CancellationToken ct)
    {
        var o = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CASOthers WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var o2 = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CASOthersTwo WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        string? Get(IDictionary<string, object?>? r, string c) => r is null ? null : Blank(S(r, c));
        decimal? Fee(string c) => o is null ? null : Dec(o, c) is { } d && d != 0 ? d : null;
        List<string?> Slots(IDictionary<string, object?>? r, string c, int n) => Enumerable.Range(1, n).Select(i => Get(r, c + i)).ToList();
        return new InvoiceCaseProcedures(Slots(o2, "CASAttendDoctor", 5), Slots(o2, "CASAnaesName", 3), Slots(o, "CASOMedSurMgmt", 3),
            Enumerable.Range(1, 3).Select(i => new CaseInvestigationInput(Get(o, "CASOProcInvestigationDone" + i), Get(o, "CASInvCode" + i))).ToList(),
            Enumerable.Range(1, 6).Select(i => new InvoiceProcedure(Get(o, "CASOPROCRequired" + i), Get(o, "CASCptCode" + i), Fee("CASProcActSurgFees" + i), Fee("CASProcActAnaeFees" + i))).ToList(),
            Enumerable.Range(1, 6).Select(i => new InvoiceProcedure(Get(o, "CASONonSurgPROCRequired" + i), Get(o, "CASNonSurgCptCode" + i), Fee("CASProcActNonSurgFees" + i))).ToList());
    }

    /// <summary>
    /// VIEW_MMA_Schedule rates by OPCS code. A code can have more than one schedule row; the legacy form took whichever came last, here the one with the
    /// highest 13th schedule surgeon rate is used so the result does not depend on row order.
    /// </summary>
    private async Task<Dictionary<string, MmaRate>> RatesAsync(IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        var rows = await QueryAsync(GenesisDb.Maintenance, """
            SELECT OPCSCode, OPCSNarrative, ProcedureDescription, ProcMainBPCode, BodyParts, ProcPageNo, Page, SurgeonsFees, AnaesthetistsFees, Surgeon13thMin, Surgeon13th, Anesthetist13th
            FROM VIEW_MMA_Schedule WHERE OPCSCode IN @codes
            """, new { codes }, ct);
        return rows.GroupBy(r => S(r, "OPCSCode")!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => Dec(r, "Surgeon13th") ?? 0).ThenByDescending(r => Dec(r, "SurgeonsFees") ?? 0).First())
            .Select(r => new MmaRate(S(r, "OPCSCode")!.Trim(), Blank(S(r, "OPCSNarrative")), Blank(S(r, "ProcedureDescription")), Blank(S(r, "ProcMainBPCode")), Blank(S(r, "ProcPageNo")),
                Blank(S(r, "BodyParts")), Blank(S(r, "Page")), Dec(r, "SurgeonsFees") ?? 0, Dec(r, "AnaesthetistsFees") ?? 0, Dec(r, "Surgeon13thMin") ?? 0, Dec(r, "Surgeon13th") ?? 0,
                Dec(r, "Anesthetist13th") ?? 0))
            .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<MmaRate?> MmaRateAsync(string code, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(code) ? null : (await RatesAsync([code.Trim()], ct)).Values.FirstOrDefault();

    public async Task<IReadOnlyList<MmaCode>> MmaCodesAsync(string? code, string? narrative, bool investigations, CancellationToken ct = default)
    {
        var c = Blank(code); var n = Blank(narrative);
        var sql = "SELECT TOP 200 OPCSCode, OPCSNarrative FROM MMAProcedures WHERE ISNULL(OPCSCode, '') <> ''"
            + (investigations ? " AND ProcPart = 'A'" : "") + (c is null ? "" : " AND OPCSCode LIKE @c") + (n is null ? "" : " AND OPCSNarrative LIKE @n") + " ORDER BY OPCSCode";
        return (await QueryAsync(GenesisDb.Maintenance, sql, new { c = c is null ? null : Like(c) + "%", n = n is null ? null : "%" + Like(n) + "%" }, ct))
            .Select(r => new MmaCode(S(r, "OPCSCode")!.Trim(), Blank(S(r, "OPCSNarrative")))).ToList();
    }

    /// <summary>
    /// Legacy SaveCASOthers (Case_AdjInvCASOthers, Case_AdjInvCASOthersTwo): every slot is written, blank text as '' and blank fees as 0. CASTotalProc is
    /// left as it is (legacy always wrote 0).
    /// </summary>
    private static async Task SaveProceduresAsync(IDbSession s, string no, InvoiceCaseProcedures p, CancellationToken ct)
    {
        static string T(string? v) => v?.Trim() ?? "";
        static TItem At<TItem>(IReadOnlyList<TItem>? xs, int i, TItem none) => xs is not null && i < xs.Count ? xs[i] : none;
        var none = new InvoiceProcedure(null, null, null);
        var a = new Dictionary<string, object?> { ["no"] = no };
        for (var i = 1; i <= 6; i++)
        {
            var sp = At(p.Surgical, i - 1, none); var np = At(p.NonSurgical, i - 1, none);
            a[$"p{i}"] = T(sp.Text); a[$"c{i}"] = T(sp.Code); a[$"sf{i}"] = sp.ActualFee ?? 0; a[$"af{i}"] = sp.ActualAnaesFee ?? 0;
            a[$"np{i}"] = T(np.Text); a[$"nc{i}"] = T(np.Code); a[$"nf{i}"] = np.ActualFee ?? 0;
        }
        for (var i = 1; i <= 3; i++)
        {
            var inv = At(p.Investigations, i - 1, new CaseInvestigationInput(null, null));
            a[$"m{i}"] = T(At(p.MedicalManagement, i - 1, null)); a[$"id{i}"] = T(inv.Done); a[$"ic{i}"] = T(inv.Code); a[$"an{i}"] = T(At(p.Anaesthetists, i - 1, null));
        }
        for (var i = 1; i <= 5; i++) a[$"d{i}"] = T(At(p.AttendingDoctors, i - 1, null));
        static string Set(string col, string param, int n) => string.Join(", ", Enumerable.Range(1, n).Select(i => $"{col}{i} = @{param}{i}"));
        await s.ExecuteAsync(
            $"UPDATE CASOthers SET {Set("CASOMedSurMgmt", "m", 3)}, {Set("CASOProcInvestigationDone", "id", 3)}, {Set("CASInvCode", "ic", 3)}, "
            + $"{Set("CASOProcRequired", "p", 6)}, {Set("CASCPTCode", "c", 6)}, {Set("CASProcActSurgFees", "sf", 6)}, {Set("CASProcActAnaeFees", "af", 6)}, "
            + $"{Set("CASONonSurgProcRequired", "np", 6)}, {Set("CASNonSurgCPTCode", "nc", 6)}, {Set("CASProcActNonSurgFees", "nf", 6)} WHERE CASNumber = @no; "
            + $"UPDATE CASOthersTwo SET {Set("CASAttendDoctor", "d", 5)}, {Set("CASAnaesName", "an", 3)} WHERE CASNumber = @no;", a, ct);
    }

    public async Task<CaseInvoiceOptions> OptionsAsync(CancellationToken ct = default)
    {
        var stay = (await QueryAsync(GenesisDb.Main, "SELECT PayTypeCode, PayTypeName FROM PayType WHERE SUBSTRING(PayTypeCode, 1, 1) NOT IN ('C', 'R') ORDER BY PayTypeCode", null, ct))
            .Select(r => new CaseOption(S(r, "PayTypeCode")!, S(r, "PayTypeName") ?? "")).ToList();
        var modes = (await QueryAsync(GenesisDb.Maintenance, "SELECT RecModeCode, RecModeDesc FROM InvRecMode ORDER BY RecModeCode", null, ct))
            .Select(r => new CaseOption(S(r, "RecModeCode")!, S(r, "RecModeDesc") ?? "")).ToList();
        return new(stay, modes, CaseInvoiceCatalog.GenericHospitals, CaseInvoiceCatalog.Fields, CaseInvoiceCatalog.Flags);
    }

    private async Task<(CaseInvoiceCase Case, bool NewSmPlan)?> CaseAsync(string no, CancellationToken ct)
    {
        var r = (await QueryAsync(GenesisDb.Main, """
            SELECT c.CASNumber, c.MBMNumber, c.MBMPatientCovID, c.CASHOSCode, c.CASDateAdmitted, c.CASDischargeDate, c.CASStayType, c.ModalityCode, m.MBMName, m.MBMPolicyNo, m.PLNCode, h.HOSName, cp.MBMCName,
                   m.MBMDOB, ps.PStatusDesc, g.CoPolicyEffDate, g.CoPolicyExpDate,
                   (SELECT TOP 1 p.NewSMPlan FROM PLN p WHERE p.PLNCode = m.PLNCode AND p.HLTCode = m.HLTCode ORDER BY p.PLNEffDate DESC) AS NewSMPlan
            FROM CAS c LEFT JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN HOS h ON h.HOSCode = c.CASHOSCode
                 LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID
                 LEFT JOIN PolicyStatus ps ON ps.PStatusCode = m.MBMStatus
                 OUTER APPLY (SELECT TOP 1 CoPolicyEffDate, CoPolicyExpDate FROM GroupCompanyFW WHERE CoGRPPaycode = m.PAYCode AND CoInsurerRefNo = m.MBMInsurerUniqueNo) g
            WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (r is null) return null;
        return (new CaseInvoiceCase(S(r, "CASNumber")!, S(r, "MBMNumber"), S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMPatientCovID"), S(r, "PLNCode"), S(r, "CASHOSCode"), S(r, "HOSName"),
            Day(r, "CASDateAdmitted"), Day(r, "CASDischargeDate"), S(r, "CASStayType"),
            // Legacy txtPatientName: the principal, or the covered person the case is for. Legacy txtModal: S surgical, N non-surgical, B both.
            (S(r, "MBMPatientCovID")?.Trim() is null or "00" ? null : S(r, "MBMCName")?.Trim()) ?? S(r, "MBMName")?.Trim(), S(r, "ModalityCode")?.Trim(),
            Day(r, "CoPolicyEffDate"), Day(r, "CoPolicyExpDate"), S(r, "PStatusDesc")?.Trim(), Day(r, "MBMDOB")),
            S(r, "NewSMPlan") == "Y");
    }

    /// <summary>Legacy getVersion: new invoices belong to the version after the last completed (status C) worksheet.</summary>
    private static async Task<int> NextVersionAsync(IDbSession s, string no, CancellationToken ct) =>
        (await s.ScalarAsync<int?>("SELECT MAX(ClaimVersion) FROM Claim WHERE CASNumber = @no AND ClaimStatus = 'C'", new { no }, ct) ?? 0) + 1;

    private static string Select(string where) => $"""
        SELECT i.INVIndex, i.InvNo, i.InvDate, i.INVPayTo, i.INVStayType, i.INVPayee, i.CLMINVRecMode, i.CLMINVRecDate, i.INVFolUpDate, i.INVAmount, i.InvExcessAmt, i.InvPayAmt,
               i.INVInitialActAmt, i.CLMUHCNo, i.INVPaidByMem, i.INVWSversion, i.INVWSed, i.INVBordxed, i.INVEnteredBy, i.INVEnteredDate,
               a.CLMOther1Desc, a.CLMOthers2Desc, a.CLMOthersDescNoCOPay, n.ReimOtherRemarks, n.INVHospName,
               {string.Join(", ", CaseInvoiceCatalog.Fields.Select(f => $"{(f.Table switch { InvoiceTable.Actual => "a", InvoiceTable.ActualOthers => "o", _ => "n" })}.{f.Column} AS [{f.Key}]"))},
               {string.Join(", ", CaseInvoiceCatalog.Flags.Select(f => $"o.{f.Column} AS [{f.Key}]"))}
        FROM ClaimInvoice i LEFT JOIN ClaimInvoiceActual a ON a.INVIndex = i.INVIndex LEFT JOIN ClaimInvoiceActualOthers o ON o.INVIndex = i.INVIndex
             LEFT JOIN ClaimInvoiceNotCover n ON n.INVIndex = i.INVIndex
        WHERE {where}
        ORDER BY i.INVIndex
        """;

    private async Task<List<InvoiceEntry>> InvoicesAsync(Func<string, object, Task<IReadOnlyList<IDictionary<string, object?>>>> query, string where, object args, CancellationToken ct)
    {
        var rows = await query(Select(where), args);
        var payees = rows.Select(r => S(r, "INVPayee")).OfType<string>().Distinct().ToArray();
        var names = payees.Length == 0 ? new Dictionary<string, string?>()
            : (await QueryAsync(GenesisDb.Main, "SELECT HOSCode, HOSName FROM HOS WHERE HOSCode IN @payees", new { payees }, ct)).ToDictionary(r => S(r, "HOSCode")!, r => S(r, "HOSName"), StringComparer.OrdinalIgnoreCase);
        return rows.Select(r =>
        {
            var payee = S(r, "INVPayee");
            return new InvoiceEntry(I(r, "INVIndex") ?? 0, S(r, "InvNo") ?? "", Day(r, "InvDate"), S(r, "INVPayTo"), S(r, "INVStayType"), payee,
                payee is null ? null : names.GetValueOrDefault(payee) ?? (S(r, "INVPayTo") == CaseInvoicePayTo.Member ? CaseInvoiceCatalog.MemberPayee : payee), S(r, "INVHospName"),
                S(r, "CLMINVRecMode"), Day(r, "CLMINVRecDate"), Day(r, "INVFolUpDate"), Dec(r, "INVAmount") ?? 0, Dec(r, "InvExcessAmt") ?? 0, Dec(r, "InvPayAmt") ?? 0, Dec(r, "INVInitialActAmt"), S(r, "CLMUHCNo"),
                int.TryParse(S(r, "INVWSversion"), out var v) ? v : null, Flag(r, "INVWSed"), Flag(r, "INVBordxed"), S(r, "INVEnteredBy"), Day(r, "INVEnteredDate"),
                CaseInvoiceCatalog.Fields.ToDictionary(f => f.Key, f => Dec(r, f.Key) ?? 0), CaseInvoiceCatalog.Flags.Where(f => S(r, f.Key) == "Y").Select(f => f.Key).ToList(),
                S(r, "CLMOther1Desc"), S(r, "CLMOthers2Desc"), S(r, "CLMOthersDescNoCOPay"), S(r, "ReimOtherRemarks"), Dec(r, "INVPaidByMem") ?? 0);
        }).ToList();
    }

    public async Task<CaseInvoiceScreen?> GetAsync(string caseNumber, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        if (await CaseAsync(no, ct) is not { } c) return null;
        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var next = await NextVersionAsync(s, no, ct);
        var remarks = await s.ScalarAsync<string?>("SELECT Remarks FROM ClaimInvoiceRemarks WHERE CASNumber = @no", new { no }, ct);
        var invoices = await InvoicesAsync((sql, a) => s.QueryAsync(sql, a, ct), "i.CASNumber = @no", new { no }, ct);
        var procedures = await ProceduresAsync(no, ct);
        var codes = (procedures.Surgical ?? []).Concat(procedures.NonSurgical ?? []).Select(x => x.Code).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new CaseInvoiceScreen(c.Case, next, Blank(remarks), invoices, procedures, await RatesAsync(codes, ct));
    }

    /// <summary>Validates the request against the case and the code tables; returns the cleaned header values and every amount (computed ones worked out).</summary>
    private async Task<(CaseInvoiceCase Case, Dictionary<string, object?> Header, Dictionary<string, decimal> Amounts, decimal Total)> PrepareAsync(string no, CaseInvoiceRequest r, CancellationToken ct)
    {
        var (c, newSm) = await CaseAsync(no, ct) ?? throw BusinessRuleException.NotFound("No record found!");
        if (CaseInvoiceRules.Validate(r, c.Discharged) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);

        var stay = r.StayType!.Trim();
        if (!(await OptionsAsync(ct)).StayTypes.Any(o => o.Code == stay)) throw new BusinessRuleException($"'{stay}' is not a valid stay type.", field: "stayType");
        var mode = Blank(r.ReceiptMode);
        if (mode is not null && (await QueryAsync(GenesisDb.Maintenance, "SELECT COUNT(*) AS N FROM InvRecMode WHERE RecModeCode = @mode", new { mode }, ct)).Select(x => I(x, "N")).First() == 0)
            throw new BusinessRuleException($"'{mode}' is not a valid receipt mode.", field: "receiptMode");
        var payee = CaseInvoiceCatalog.MemberPayee;
        if (r.PayTo == CaseInvoicePayTo.Hospital)
        {
            payee = r.Hospital!.Trim();
            if ((await QueryAsync(GenesisDb.Main, "SELECT COUNT(*) AS N FROM HOS WHERE HOSCode = @payee", new { payee }, ct)).Select(x => I(x, "N")).First() == 0)
                throw new BusinessRuleException($"'{payee}' is not a known hospital.", field: "hospital");
        }

        var entered = (r.Amounts ?? new Dictionary<string, decimal?>()).Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value!.Value);
        var (amounts, total) = CaseInvoiceCatalog.Compute(entered);
        var (surgicalSum, medicalSum) = CaseInvoiceCatalog.HospitalSums(amounts, c.PlanCode, newSm);
        // A member is paid the whole invoice; for a hospital the excess the member pays is taken off (legacy lblPayAmt).
        var excess = r.PayTo == CaseInvoicePayTo.Member ? 0m : r.Excess!.Value;
        var header = new Dictionary<string, object?>
        {
            ["no"] = no, ["invNo"] = r.InvoiceNo!.Trim(), ["invDate"] = At(r.InvoiceDate), ["payee"] = payee, ["stay"] = stay,
            ["payType"] = r.PayTo == CaseInvoicePayTo.Member ? "R" : "C", ["payTo"] = r.PayTo, ["mode"] = mode, ["folUp"] = At(r.FollowUpDate), ["recDate"] = At(r.ReceiptDate),
            ["amount"] = total, ["paidByMem"] = excess, ["uhc"] = Blank(r.UhcNo) ?? "", ["excess"] = excess, ["payAmt"] = total - excess, ["initial"] = r.InitialAmount,
            ["surgicalSum"] = surgicalSum, ["medicalSum"] = medicalSum,
            ["desc1"] = Blank(r.OthersDescription) ?? "", ["desc2"] = Blank(r.Others2Description) ?? "", ["descNoCoPay"] = Blank(r.OthersNoCoPayDescription) ?? "",
            ["notPayRemark"] = Blank(r.NotPayableRemark) ?? "", ["hospName"] = Blank(r.HospitalName) ?? "",
        };
        foreach (var f in CaseInvoiceCatalog.Flags) header["f_" + f.Key] = (r.ChargedToMember ?? []).Contains(f.Key) ? "Y" : "N";
        return (c, header, amounts, total);
    }

    /// <summary>Writes the three amount tables of one invoice: updates the row when it exists (older invoices may lack some), inserts it otherwise.</summary>
    private static async Task SaveDetailsAsync(IDbSession s, int index, Dictionary<string, object?> h, Dictionary<string, decimal> amounts, CancellationToken ct)
    {
        var tables = new (string Table, InvoiceTable Kind, (string Column, string Param)[] Extra)[]
        {
            ("ClaimInvoiceActual", InvoiceTable.Actual, [("CLMCHospSum", "surgicalSum"), ("CLMMHospSum", "medicalSum"), ("CLMOther1Desc", "desc1"), ("CLMOthers2Desc", "desc2"), ("CLMOthersDescNoCOPay", "descNoCoPay")]),
            ("ClaimInvoiceActualOthers", InvoiceTable.ActualOthers, CaseInvoiceCatalog.Flags.Select(f => (f.Column, "f_" + f.Key)).ToArray()),
            ("ClaimInvoiceNotCover", InvoiceTable.NotCovered, [("ReimOtherRemarks", "notPayRemark"), ("INVHospName", "hospName")]),
        };
        foreach (var (table, kind, extra) in tables)
        {
            var args = new Dictionary<string, object?>(h) { ["index"] = index };
            var cols = In(kind).Select(f => (f.Column, Param: "a_" + f.Key)).Concat(extra).ToList();
            foreach (var f in In(kind)) args["a_" + f.Key] = amounts[f.Key];
            var updated = await s.ExecuteAsync($"UPDATE {table} SET {string.Join(", ", cols.Select(c => $"{c.Column} = @{c.Param}"))} WHERE INVIndex = @index", args, ct);
            if (updated == 0)
                await s.ExecuteAsync($"INSERT INTO {table} (INVIndex, {string.Join(", ", cols.Select(c => c.Column))}) VALUES (@index, {string.Join(", ", cols.Select(c => "@" + c.Param))})", args, ct);
        }
    }

    private static async Task<InvoiceEntry> ReadBackAsync(CaseInvoiceRepository repo, IDbSession s, int index, CancellationToken ct) =>
        (await repo.InvoicesAsync((sql, a) => s.QueryAsync(sql, a, ct), "i.INVIndex = @index", new { index }, ct)).Single();

    public async Task<InvoiceEntry> CreateAsync(string caseNumber, CaseInvoiceRequest request, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var (_, h, amounts, _) = await PrepareAsync(no, request, ct);

        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        if (await s.ScalarAsync<int>("SELECT COUNT(*) FROM ClaimInvoice WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @no AND InvNo = @invNo", h, ct) > 0)
            throw BusinessRuleException.Conflict("Current Invoice No has been used before! Please re-verify the invoice.");
        h["version"] = (await NextVersionAsync(s, no, ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        h["user"] = user;
        var index = await s.ScalarAsync<int>("""
            INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, InvNo, CASNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVStayType, INVPAYType, INVPayTo,
                                      CLMINVRecMode, INVFolUpDate, CLMINVRecDate, INVAmount, INVPaidByMem, CLMUHCNo, InvExcessAmt, InvPayAmt, INVInitialActAmt)
            VALUES (@version, 0, 0, @invNo, @no, @invDate, CAST(GETDATE() AS date), @user, @payee, @stay, @payType, @payTo, @mode, @folUp, @recDate, @amount, @paidByMem, @uhc, @excess, @payAmt, @initial);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, h, ct);
        await SaveDetailsAsync(s, index, h, amounts, ct);
        var created = await ReadBackAsync(this, s, index, ct);
        await using var main = request.Procedures is null ? null : await Db.BeginSessionAsync(GenesisDb.Main, ct);
        if (main is not null) await SaveProceduresAsync(main, no, request.Procedures!, ct);
        await s.CommitAsync(ct);
        if (main is not null) await main.CommitAsync(ct);

        audit.Record(new AuditEntry("Case.CreateInvoice", "ClaimInvoice", index.ToString(System.Globalization.CultureInfo.InvariantCulture), user,
            new Dictionary<string, object?> { ["case"] = no, ["invoice"] = created, ["procedures"] = request.Procedures }));
        return created;
    }

    /// <summary>
    /// Locks and returns the invoice, refusing one of another case. An invoice already used in a worksheet cannot be removed; it can be changed (as the
    /// legacy form allowed) while that worksheet has not gone to a bordereau, after which Worksheet Adjustment brings the worksheet up to date.
    /// </summary>
    private async Task<InvoiceEntry> EditableAsync(IDbSession s, string no, int index, bool removing, CancellationToken ct)
    {
        var row = (await s.QueryAsync("SELECT CASNumber, INVWSed, INVWSversion, INVBordxed FROM ClaimInvoice WITH (UPDLOCK, HOLDLOCK) WHERE INVIndex = @index", new { index }, ct)).FirstOrDefault();
        if (row is null || !string.Equals(S(row, "CASNumber"), no, StringComparison.OrdinalIgnoreCase)) throw BusinessRuleException.NotFound("The invoice does not exist.");
        if (Flag(row, "INVWSed"))
        {
            if (removing) throw BusinessRuleException.Conflict("Selected Invoice cannot be deleted! It has been used in Worksheet!");
            var version = S(row, "INVWSversion");
            var inBordx = Flag(row, "INVBordxed") || await s.ScalarAsync<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @no AND CAST(ClaimVersion AS nvarchar) = @version AND ISNULL(ClaimBordxStatus, 0) <> 0",
                new { no, version }, ct) > 0;
            if (inBordx) throw BusinessRuleException.Conflict("Selected Invoice cannot be changed! Its worksheet has gone to a bordereau.");
        }
        return await ReadBackAsync(this, s, index, ct);
    }

    public async Task<InvoiceEntry> UpdateAsync(string caseNumber, int index, CaseInvoiceRequest request, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var (_, h, amounts, _) = await PrepareAsync(no, request, ct);

        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var before = await EditableAsync(s, no, index, removing: false, ct);
        h["index"] = index;
        if (await s.ScalarAsync<int>("SELECT COUNT(*) FROM ClaimInvoice WHERE CASNumber = @no AND InvNo = @invNo AND INVIndex <> @index", h, ct) > 0)
            throw BusinessRuleException.Conflict("Current Invoice No has been used before! Please re-verify the invoice.");
        await s.ExecuteAsync("""
            UPDATE ClaimInvoice SET InvNo = @invNo, InvDate = @invDate, INVPayee = @payee, INVStayType = @stay, INVPAYType = @payType, INVPayTo = @payTo, CLMINVRecMode = @mode,
                   INVFolUpDate = @folUp, CLMINVRecDate = @recDate, INVAmount = @amount, INVPaidByMem = @paidByMem, CLMUHCNo = @uhc, InvExcessAmt = @excess, InvPayAmt = @payAmt,
                   INVInitialActAmt = @initial
            WHERE INVIndex = @index
            """, h, ct);
        await SaveDetailsAsync(s, index, h, amounts, ct);
        var after = await ReadBackAsync(this, s, index, ct);
        await using var main = request.Procedures is null ? null : await Db.BeginSessionAsync(GenesisDb.Main, ct);
        if (main is not null) await SaveProceduresAsync(main, no, request.Procedures!, ct);
        await s.CommitAsync(ct);
        if (main is not null) await main.CommitAsync(ct);

        audit.Record(new AuditEntry("Case.UpdateInvoice", "ClaimInvoice", index.ToString(System.Globalization.CultureInfo.InvariantCulture), user,
            new Dictionary<string, object?> { ["case"] = no, ["before"] = before, ["after"] = after, ["procedures"] = request.Procedures }));
        return after;
    }

    public async Task DeleteAsync(string caseNumber, int index, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var before = await EditableAsync(s, no, index, removing: true, ct);
        // The legacy Remove left the ClaimInvoiceActualOthers row behind; it is removed with the rest here.
        await s.ExecuteAsync("""
            DELETE FROM ClaimInvoiceNotCover WHERE INVIndex = @index; DELETE FROM ClaimInvoiceActualOthers WHERE INVIndex = @index;
            DELETE FROM ClaimInvoiceActual WHERE INVIndex = @index; DELETE FROM ClaimInvoice WHERE INVIndex = @index;
            """, new { index }, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.DeleteInvoice", "ClaimInvoice", index.ToString(System.Globalization.CultureInfo.InvariantCulture), user,
            new Dictionary<string, object?> { ["case"] = no, ["before"] = before }));
    }

    public async Task SaveRemarksAsync(string caseNumber, string? remarks, string user, CancellationToken ct = default)
    {
        var no = CaseNo(caseNumber);
        var text = Blank(remarks) ?? "";
        if (text.Length > 500 || text.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n'))) throw new BusinessRuleException("The remarks can be at most 500 characters.", field: "remarks");
        if (await CaseAsync(no, ct) is null) throw BusinessRuleException.NotFound("No record found!");
        await using var s = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var before = await s.ScalarAsync<string?>("SELECT Remarks FROM ClaimInvoiceRemarks WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @no", new { no }, ct);
        if (await s.ExecuteAsync("UPDATE ClaimInvoiceRemarks SET Remarks = @text WHERE CASNumber = @no", new { no, text }, ct) == 0)
            await s.ExecuteAsync("INSERT INTO ClaimInvoiceRemarks (CASNumber, Remarks) VALUES (@no, @text)", new { no, text }, ct);
        await s.CommitAsync(ct);
        audit.Record(new AuditEntry("Case.SaveInvoiceRemarks", "ClaimInvoiceRemarks", no, user, new Dictionary<string, object?> { ["before"] = before, ["after"] = text }));
    }
}
