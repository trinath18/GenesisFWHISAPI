using System.Globalization;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Worksheet Registration save (legacy frmNewWorksheet2010 cmdApprove_Click: SaveClaim, SaveClaimRemarks, saveLimit, saveActual, saveApprove, saveHos, saveMember,
/// savePayMBMStatus, saveSameLimit, UpdateInvoice, UpdateCasReserve, UpdateMBMBalance, UpdateCreditDebit) and Worksheet Adjustment (frmNewWSAdjust2010, which
/// rewrites a saved version). Not migrated: top-up, lifetime and take-over worksheets, the excess letter, the Excel export and the "increase approval limit"
/// prompt for rows without an actual amount.
/// </summary>
internal sealed partial class WorksheetRepository
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Grid = ["ClaimLimit", "ClaimActual", "ClaimApprove", "ClaimHos", "ClaimMember"];
    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static string DaysText(decimal d) => d.ToString("0.##", Inv);

    /// <summary>The case and member fields the save needs.</summary>
    private sealed record SaveCase(string Number, string? ClaimId, string LinkCase, string Patient, string Member, string Payor, string Plan, string Insurer, string? MemberType, DateTime Effective);

    private async Task<SaveCase> SaveCaseAsync(string no, CancellationToken ct)
    {
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.ClaimID, c.LINKCASNo, c.MBMPatientCovID, c.CASDateAdmitted, m.MBMNumber, m.PAYCode, m.PLNCode, m.INSCode, m.MBMMemberType, m.MBMPayorEffDate
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE c.CASNumber = @no
            """, new { no }, ct)).First();
        string T(string col) => S(c, col)?.Trim() ?? "";
        return new SaveCase(no, Blank(S(c, "ClaimID")), T("LINKCASNo").Length > 0 ? T("LINKCASNo") : no, T("MBMPatientCovID"), T("MBMNumber"), T("PAYCode"), T("PLNCode"), T("INSCode"),
            S(c, "MBMMemberType"), D(c, "MBMPayorEffDate") ?? D(c, "CASDateAdmitted") ?? DateTime.Today);
    }

    /// <summary>The legacy cmdApprove_Click checks shared by registration and adjustment.</summary>
    private async Task CheckAsync(WorksheetPreview preview, WorksheetSaveRequest r, CancellationToken ct)
    {
        var no = preview.Case.CaseNumber;
        var calc = preview.Calculation;
        if (preview.Invoices.Count == 0) throw new BusinessRuleException("Worksheet cannot be saved without any invoice!", field: "invoices");
        if ((await QueryAsync(GenesisDb.Main, "SELECT COUNT(*) AS N FROM DIAFinal WHERE CASNumber = @no", new { no }, ct)).Select(x => I(x, "N")).First() == 0)
            throw new BusinessRuleException("Worksheet cannot be saved without Final Diagnosis!", field: "diagnosis");
        var memberPaid = preview.Invoices.Any(i => i.PayTo == CaseInvoicePayTo.Member);
        if (WorksheetSaveRules.Validate(r, memberPaid) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        // Legacy check: on a cashless claim the hospital invoices must already show what the hospital is paid (invoice amount less the member's excess).
        var invoicePay = preview.Invoices.Where(i => i.PayTo == CaseInvoicePayTo.Hospital).Sum(i => i.PayAmount);
        if (!memberPaid && invoicePay != calc.PayableToHospital)
            throw new BusinessRuleException($"Please verify Payable to Hospital amount: the invoices pay {invoicePay:N2}, the worksheet {calc.PayableToHospital:N2}.", field: "payableToHospital");
        if (r.ExpectedFinalApproved is { } expected && expected != calc.FinalApproved)
            throw BusinessRuleException.Conflict($"The worksheet now approves {calc.FinalApproved:N2}, not {expected:N2}: recalculate and check it again.");
    }

    /// <summary>The Claim header values worked out from the calculation (legacy SaveClaim), as SQL parameters.</summary>
    private static Dictionary<string, object?> HeaderArgs(SaveCase c, int version, WorksheetPreview preview, WorksheetSaveRequest r, string user, DateTime? lastDoc, string? stayType)
    {
        var calc = preview.Calculation;
        var memberPaid = preview.Invoices.Any(i => i.PayTo == CaseInvoicePayTo.Member);
        var memberCells = calc.Lines.Select(l => WorksheetCalculator.Parse(l.Member) ?? 0).ToList();
        var status = r.WorksheetStatus!.Trim();
        var before = calc.AnnualBalance;
        return new Dictionary<string, object?>
        {
            ["no"] = c.Number, ["version"] = version, ["today"] = DateTime.Today, ["user"] = user, ["bordxStatus"] = status == "XR" ? 1 : 0, ["bordxRef"] = status == "XR" ? "VOID" : "",
            ["cashType"] = memberPaid ? "R" : "C", ["lastDoc"] = lastDoc, ["exGratia"] = r.ExGratia ? "Y" : "N", ["stayType"] = stayType?[..1] ?? "", ["actual"] = calc.TotalActual,
            ["approved"] = calc.TotalApproved, ["payH"] = calc.PayableToHospital, ["excessBy"] = memberPaid ? "M" : "H", ["excess"] = memberCells.Where(x => x < 0).Sum(),
            ["reimburse"] = memberCells.Where(x => x > 0).Sum(),
            // The legacy form stored previous balance + approved here (a sign slip); the balance left after this worksheet is stored instead.
            ["balanceAfter"] = before is { } b ? b - calc.FinalApproved : calc.FinalApproved, ["previous"] = before ?? calc.FinalApproved, ["previousNew"] = memberPaid ? 0m : before ?? 0m,
            ["exceeded"] = calc.LimitExceeded ? calc.TotalApproved - calc.FinalApproved : 0m, ["payM"] = calc.PayableToMember, ["status"] = status, ["final"] = calc.FinalApproved,
            ["payTo"] = memberPaid ? Blank(r.PayTo) ?? "" : "", ["accountNo"] = memberPaid ? Blank(r.AccountNo) ?? "" : "0", ["bank"] = memberPaid ? Blank(r.Bank) ?? "" : "0", ["claimId"] = c.ClaimId,
        };
    }

    /// <summary>
    /// Marks the version's invoices (void ones for a recovery/rejected worksheet), then returns the latest document date and the stay type. When
    /// <paramref name="onlyOpen"/> an invoice another user already put in a worksheet makes the save fail.
    /// </summary>
    private static async Task<(DateTime? LastDoc, string? StayType)> MarkInvoicesAsync(IDbSession ws, int[] invoiceIds, string status, bool onlyOpen, CancellationToken ct)
    {
        var open = onlyOpen ? " AND ISNULL(INVWSed, 0) = 0" : "";
        var marked = await ws.ExecuteAsync(status == "XR"
                ? $"UPDATE ClaimInvoice SET INVWSed = 1, INVBordxRefNo = 'VOID', INVBordxed = 1 WHERE INVIndex IN @invoiceIds{open}"
                : $"UPDATE ClaimInvoice SET INVWSed = 1 WHERE INVIndex IN @invoiceIds{open}", new { invoiceIds }, ct);
        if (marked != invoiceIds.Length) throw BusinessRuleException.Conflict("Some invoices were put into a worksheet by someone else meanwhile: show the case again.");
        var lastDoc = await ws.ScalarAsync<DateTime?>("SELECT MAX(ISNULL(CLMINVRecDate, InvDate)) FROM ClaimInvoice WHERE INVIndex IN @invoiceIds", new { invoiceIds }, ct);
        var stayType = Blank(await ws.ScalarAsync<string?>("SELECT TOP 1 INVStayType FROM ClaimInvoice WHERE INVIndex IN @invoiceIds ORDER BY INVIndex", new { invoiceIds }, ct));
        return (lastDoc, stayType);
    }

    /// <summary>The five grid tables (one text column per row, as the legacy grid showed them) and the pay-member flags. Replaces what the version had.</summary>
    private static async Task WriteGridAsync(IDbSession ws, string no, int version, WorksheetPreview preview, CancellationToken ct)
    {
        var calc = preview.Calculation;
        foreach (var t in Grid.Append("ClaimPayMBMStatus"))
            await ws.ExecuteAsync($"DELETE FROM {t} WHERE CASNumber = @no AND CLMVersion = @version", new { no, version }, ct);
        var cols = WorksheetCatalog.Lines.Select(l => preview.CombinedRows && l.CombinedColumn is not null ? l.CombinedColumn : l.Column).ToList();
        var cells = new Func<WorksheetLineValue, string?>[] { l => l.Limit, l => l.Actual, l => l.Approved, l => l.Hospital, l => l.Member };
        for (var t = 0; t < Grid.Length; t++)
        {
            var args = new Dictionary<string, object?> { ["no"] = no, ["version"] = version };
            for (var i = 0; i < cols.Count; i++) args["c" + i] = cells[t](calc.Lines[i]) ?? "-";
            var names = string.Join(", ", cols);
            var values = string.Join(", ", cols.Select((_, i) => "@c" + i));
            if (Grid[t] == "ClaimLimit")
            {
                var d = calc.DaysAllowed;
                (args["d0"], args["d1"], args["d2"], args["d3"], args["d4"], args["d5"]) = (DaysText(d.Board), DaysText(d.Icu), DaysText(d.Cash), DaysText(d.Guardian), "0", DaysText(d.KidneyMonths));
                names += ", CLMBoardDays, CLMICUDays, CLMCashDays, CLMGurdianDays, CLMINHOSPhyDays, CLMKIDDCDays";
                values += ", @d0, @d1, @d2, @d3, @d4, @d5";
            }
            await ws.ExecuteAsync($"INSERT INTO {Grid[t]} (CASNumber, CLMVersion, {names}) VALUES (@no, @version, {values})", args, ct);
        }
        // Legacy savePayMBMStatus: Y only on rows approved without an actual amount ("increase approval limit", not migrated), so every row is N.
        var payCols = (await ws.QueryAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('ClaimPayMBMStatus') AND name LIKE 'CLM%' AND name <> 'CLMVersion'", null, ct))
            .Select(x => S(x, "name")!).ToList();
        await ws.ExecuteAsync($"INSERT INTO ClaimPayMBMStatus (CASNumber, CLMVersion, {string.Join(", ", payCols)}) VALUES (@no, @version, {string.Join(", ", payCols.Select(_ => "'N'"))})",
            new { no, version }, ct);
    }

    /// <summary>Remarks, credit/debit note remarks and the member's address (legacy SaveClaimRemarks / ClaimOthers). Replaces what the version had.</summary>
    private static async Task WriteOthersAsync(IDbSession ws, string no, int version, WorksheetSaveRequest r, int[] invoiceIds, CancellationToken ct)
    {
        var grossRb = await ws.ScalarAsync<decimal?>("SELECT SUM(CLMGrossRB) FROM ClaimInvoiceActual WHERE INVIndex IN @invoiceIds", new { invoiceIds }, ct) ?? 0;
        var remarks = Blank(r.Remarks) ?? "";
        // The legacy form appended the gross room & board rate; on an amendment the earlier note is replaced rather than repeated.
        var marker = remarks.IndexOf(" GROSS RB RATE : RM", StringComparison.Ordinal);
        if (marker >= 0) remarks = remarks[..marker];
        if (grossRb > 0) remarks = (remarks + " GROSS RB RATE : RM" + grossRb.ToString("0.##", Inv)).Trim();
        await ws.ExecuteAsync("DELETE FROM ClaimOthers WHERE CASNumber = @no AND CLaimVersion = @version", new { no, version }, ct);
        await ws.ExecuteAsync("""
            INSERT INTO ClaimOthers (CASNumber, CLaimVersion, CLMWSRemarks, CLMDCNoteRemarks, CLMNote, MBMAdd1, MBMAdd2, MBMAdd3, MBMPostCode, MBMCity, MBMState, CLMPayMode, CLMAttention)
            VALUES (@no, @version, @remarks, @dc, @note, @a1, @a2, @a3, @pc, @city, @state, @mode, @attention)
            """, new
        {
            no, version, remarks, dc = Blank(r.DebitCreditRemarks) ?? "", note = Blank(r.Note) ?? "", a1 = Blank(r.Address1) ?? "", a2 = Blank(r.Address2) ?? "", a3 = Blank(r.Address3) ?? "",
            pc = Blank(r.PostCode) ?? "", city = Blank(r.City) ?? "", state = Blank(r.State) ?? "", mode = Blank(r.PaymentMode) ?? "", attention = Blank(r.Attention) ?? "",
        }, ct);
    }

    /// <summary>
    /// Legacy UpdateCreditDebit: the member's share gets a credit note (paid to the member) or a debit note (owed by the member), numbered per payor.
    /// A version that already has a note keeps it.
    /// </summary>
    private static async Task<string?> RaiseNoteAsync(IDbSession ws, string no, int version, string payor, decimal payableToMember, CancellationToken ct)
    {
        var existing = Blank(await ws.ScalarAsync<string?>("SELECT DebitCreditRefNo FROM Claim WHERE CASNumber = @no AND ClaimVersion = @version", new { no, version }, ct));
        if (existing is not null || payableToMember == 0) return existing;
        var (prefix, table) = payableToMember > 0 ? ("C", "CRSeq") : ("D", "DRSeq");
        var last = await ws.ScalarAsync<string?>($"SELECT TOP 1 {table} FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE Payor = @payor", new { payor }, ct);
        var seq = ((last is null ? 0 : int.Parse(last.Trim(), Inv)) + 1).ToString("00000", Inv);
        await ws.ExecuteAsync(last is null ? $"INSERT INTO {table} ({table}, Payor) VALUES (@seq, @payor)" : $"UPDATE {table} SET {table} = @seq WHERE Payor = @payor", new { seq, payor }, ct);
        var note = prefix + payor + seq;
        await ws.ExecuteAsync("UPDATE Claim SET DebitCreditRefNo = @note, DebitCreditDate = @today WHERE CASNumber = @no AND ClaimVersion = @version", new { note, today = DateTime.Today, no, version }, ct);
        return note;
    }

    /// <summary>
    /// The case and member are in the main database (the legacy form did not hold one transaction across both databases either): the case reserve is cleared and
    /// the patient's annual balance set to the plan limit less everything approved (legacy UpdateMBMBalance).
    /// </summary>
    private async Task<decimal?> UpdateCaseAndBalanceAsync(SaveCase c, bool annualLimit, CancellationToken ct)
    {
        decimal? balanceAfter = null;
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await main.ExecuteAsync("UPDATE CAS SET CASPending = 'N', CASReserveAmount = 0 WHERE CASNumber = @no", new { no = c.Number }, ct);
        if (annualLimit && await AnnualBalance.DetailAsync((db, sql, args) => QueryAsync(db, sql, args, ct), c.Member, c.Patient, c.Plan, c.Insurer, c.MemberType, c.Effective) is { } detail)
        {
            balanceAfter = Math.Max(0, detail.Balance);
            await main.ExecuteAsync(detail.CoveredPersonLimit
                ? "UPDATE MBMCoveredPersons SET MBMCAvailableLimit = @balance WHERE MBMCNumber = @member AND MBMCCoverID = @patient"
                : "UPDATE MBM SET MBMAvailableLimit = @balance WHERE MBMNumber = @member", new { balance = balanceAfter, member = c.Member, patient = c.Patient }, ct);
        }
        await main.CommitAsync(ct);
        return balanceAfter;
    }

    private WorksheetSaveResult Done(string action, SaveCase c, int version, WorksheetPreview preview, WorksheetSaveRequest r, string user, int[] invoiceIds, string? note, decimal? balanceAfter)
    {
        var calc = preview.Calculation;
        audit.Record(new AuditEntry(action, "Claim", $"{c.Number}-{version}", user, new Dictionary<string, object?>
        {
            ["status"] = r.WorksheetStatus, ["invoices"] = invoiceIds, ["final"] = calc.FinalApproved, ["payableToHospital"] = calc.PayableToHospital, ["payableToMember"] = calc.PayableToMember,
            ["limitOverrides"] = r.LimitOverrides, ["note"] = note, ["balanceAfter"] = balanceAfter,
        }));
        return new WorksheetSaveResult($"{c.Number}-{version}", version, calc.FinalApproved, calc.PayableToHospital, calc.PayableToMember, calc.Excess, note, balanceAfter, invoiceIds.Length);
    }

    public async Task<WorksheetSaveResult> SaveAsync(string caseNumber, WorksheetSaveRequest r, string user, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(caseNumber, r.LimitOverrides, ct) ?? throw BusinessRuleException.NotFound("No record found!");
        await CheckAsync(preview, r, ct);
        var c = await SaveCaseAsync(preview.Case.CaseNumber, ct);
        var input = (await InputAsync(c.Number, preview.Version, openOnly: true, ct))!;
        var version = preview.Version;
        var invoiceIds = preview.Invoices.Select(i => i.Index).ToArray();

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM Claim WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @no AND ClaimVersion = @version", new { no = c.Number, version }, ct) > 0)
            throw BusinessRuleException.Conflict($"Worksheet {c.Number}-{version} already exists.");
        var (lastDoc, stayType) = await MarkInvoicesAsync(ws, invoiceIds, r.WorksheetStatus!.Trim(), onlyOpen: true, ct);
        await ws.ExecuteAsync("""
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimOpenDate, ClaimOpenBy, ClaimLastUpdateUser, ClaimLastUpdateDate, ClaimBordxStatus, CLMBordxWSStatus, CLMBordxWSASCII,
                LimitCollectedBy, CLMTopMBMNumber, CLMCashType, CLMLastDocDate, CLMExGratia, CLMStayType, CLMTotalActual, CLMTotalApproved, CLMPayableByMedix2H, CLMCompExcess,
                ExcessCollectedBy, CLMTotalCompApp, CLMExcess, CLMReimburse, CLMBalanceLimit, ClaimCloseDate, CLMPreviousBalance, CLMPreviousNewLimit, CLMLifetimeBal, CLMPrevTOLmt,
                BordxRefNo, CLMPaidBYMedix2M, CLMPaidBYMedix2H, CLMLimitExceeded, CLMPayableByMedix2M, CLMWSStatus, CLMCoPayRB, CLMExGratiaAmt, CLMPrevCancerLmt, CLMPrevHomeNCLmt,
                ClaimStatus, CLMPrevPerDisLmt, CLMBordxAmt, CLMBTBGAmt, CLMFinalApprove, CLMPayTo, CLMAccountNo, CLMBank, MBMTopupInd, CLMMainTUAmt, CLMGPName, CLMCaseMgmtFees,
                CLMPrevMajorMed, CLMMajorMedFee, ClaimID)
            VALUES (@no, @version, @today, @user, @user, @today, @bordxStatus, 'N', 'N', 'M', '', @cashType, @lastDoc, @exGratia, @stayType, @actual, @approved, @payH, 0,
                @excessBy, 0, @excess, @reimburse, @balanceAfter, @today, @previous, @previousNew, 0, 0, @bordxRef, 0, 0, @exceeded, @payM, @status, 0, 0, 0, 0,
                'C', 0, @final, 0, @final, @payTo, @accountNo, @bank, '0', 0, '', 0, 0, 0, @claimId)
            """, HeaderArgs(c, version, preview, r, user, lastDoc, stayType), ct);
        await WriteGridAsync(ws, c.Number, version, preview, ct);
        await WriteOthersAsync(ws, c.Number, version, r, invoiceIds, ct);
        await SaveSameLimitAsync(ws, c.LinkCase, input, preview.Calculation, giveBack: null, ct);
        var note = await RaiseNoteAsync(ws, c.Number, version, c.Payor, preview.Calculation.PayableToMember, ct);
        await ws.CommitAsync(ct);

        var balanceAfter = await UpdateCaseAndBalanceAsync(c, preview.Calculation.AnnualBalance is not null, ct);
        return Done("Worksheet.Save", c, version, preview, r, user, invoiceIds, note, balanceAfter);
    }

    public async Task<WorksheetAdjustmentView?> AdjustmentAsync(string caseNumber, int? version, IReadOnlyDictionary<int, string>? limitOverrides = null, CancellationToken ct = default)
    {
        if (await EnquiryAsync(caseNumber, ct) is not { } enquiry || enquiry.Versions.Count == 0) return null;
        var saved = version is { } v ? enquiry.Versions.FirstOrDefault(x => x.Version == v) : enquiry.Versions[^1];
        if (saved is null) return null;
        var no = enquiry.Case.CaseNumber;
        var header = (await QueryAsync(GenesisDb.Worksheet, """
            SELECT c.ClaimBordxStatus, c.BordxRefNo, c.CLMWSStatus, c.CLMExGratia, c.CLMPayTo, c.CLMBank, c.CLMAccountNo, o.CLMWSRemarks, o.CLMDCNoteRemarks, o.CLMNote, o.MBMAdd1, o.MBMAdd2,
                   o.MBMAdd3, o.MBMPostCode, o.MBMCity, o.MBMState, o.CLMPayMode, o.CLMAttention
            FROM Claim c LEFT JOIN ClaimOthers o ON o.CASNumber = c.CASNumber AND o.CLaimVersion = c.ClaimVersion WHERE c.CASNumber = @no AND c.ClaimVersion = @version
            """, new { no, version = saved.Version }, ct)).First();
        string? H(string col) => Blank(S(header, col));
        string? Zero(string col) => H(col) is "0" ? null : H(col);
        // Legacy: only the last version can be amended, and only until it goes on a bordereau.
        var blocked = saved.Version != enquiry.Versions[^1].Version ? $"Only the latest version ({enquiry.Versions[^1].Version}) can be amended."
            : (I(header, "ClaimBordxStatus") ?? 0) != 0 || H("BordxRefNo") is { } refNo && refNo != "" ? "This worksheet is on a bordereau and cannot be amended." : null;

        var loaded = (await LoadAsync(no, saved.Version, openOnly: false, ct, amend: true))!.Value;
        if (limitOverrides is { Count: > 0 }) ValidateOverrides(limitOverrides, loaded.Input.Claimable);
        var calc = WorksheetCalculator.Calculate(loaded.Input with { LimitOverrides = limitOverrides });
        var recalculated = new WorksheetPreview(enquiry.Case, saved.Version, enquiry.CombinedRows, saved.Invoices, loaded.Claimability, loaded.Input.Claimable, loaded.Input.SameLimitDays is not null, calc);
        var details = new WorksheetSaveRequest(H("CLMWSStatus"), calc.FinalApproved, null, H("CLMWSRemarks"), H("CLMDCNoteRemarks"), H("CLMNote"), H("CLMExGratia") == "Y",
            H("CLMPayTo"), Zero("CLMBank"), Zero("CLMAccountNo"), H("MBMAdd1"), H("MBMAdd2"), H("MBMAdd3"), H("MBMPostCode"), H("MBMCity"), H("MBMState"), H("CLMPayMode"), H("CLMAttention"));
        return new WorksheetAdjustmentView(enquiry.Case, saved.Version, enquiry.Versions.Select(x => x.Version).ToList(), blocked is null, blocked, saved, recalculated, details);
    }

    public async Task<WorksheetSaveResult> AmendAsync(string caseNumber, int version, WorksheetSaveRequest r, string user, CancellationToken ct = default)
    {
        var view = await AdjustmentAsync(caseNumber, version, r.LimitOverrides, ct) ?? throw BusinessRuleException.NotFound("No record found!");
        if (!view.CanAmend) throw BusinessRuleException.Conflict(view.BlockedReason!);
        var preview = view.Recalculated;
        await CheckAsync(preview, r, ct);
        var c = await SaveCaseAsync(view.Case.CaseNumber, ct);
        var input = (await LoadAsync(c.Number, version, openOnly: false, ct, amend: true))!.Value.Input;
        var invoiceIds = preview.Invoices.Select(i => i.Index).ToArray();

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var locked = (await ws.QueryAsync("SELECT ClaimBordxStatus, BordxRefNo FROM Claim WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @no AND ClaimVersion = @version", new { no = c.Number, version }, ct)).FirstOrDefault();
        if (locked is null || (I(locked, "ClaimBordxStatus") ?? 0) != 0 || Blank(S(locked, "BordxRefNo")) is not null)
            throw BusinessRuleException.Conflict("This worksheet is on a bordereau and cannot be amended.");
        if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @no AND ClaimVersion > @version", new { no = c.Number, version }, ct) > 0)
            throw BusinessRuleException.Conflict("Only the latest version can be amended.");
        var oldDays = (await ws.QueryAsync("SELECT CLMBoardDays, CLMICUDays, CLMCashDays, CLMGurdianDays, CLMKIDDCDays FROM ClaimLimit WHERE CASNumber = @no AND CLMVersion = @version",
            new { no = c.Number, version }, ct)).FirstOrDefault();
        var (lastDoc, stayType) = await MarkInvoicesAsync(ws, invoiceIds, r.WorksheetStatus!.Trim(), onlyOpen: false, ct);
        await ws.ExecuteAsync("""
            UPDATE Claim SET ClaimLastUpdateUser = @user, ClaimLastUpdateDate = @today, ClaimBordxStatus = @bordxStatus, BordxRefNo = @bordxRef, CLMCashType = @cashType, CLMLastDocDate = @lastDoc,
                CLMExGratia = @exGratia, CLMStayType = @stayType, CLMTotalActual = @actual, CLMTotalApproved = @approved, CLMPayableByMedix2H = @payH, ExcessCollectedBy = @excessBy,
                CLMExcess = @excess, CLMReimburse = @reimburse, CLMBalanceLimit = @balanceAfter, CLMPreviousBalance = @previous, CLMPreviousNewLimit = @previousNew,
                CLMLimitExceeded = @exceeded, CLMPayableByMedix2M = @payM, CLMWSStatus = @status, CLMBordxAmt = @final, CLMFinalApprove = @final, CLMPayTo = @payTo,
                CLMAccountNo = @accountNo, CLMBank = @bank
            WHERE CASNumber = @no AND ClaimVersion = @version
            """, HeaderArgs(c, version, preview, r, user, lastDoc, stayType), ct);
        await WriteGridAsync(ws, c.Number, version, preview, ct);
        await WriteOthersAsync(ws, c.Number, version, r, invoiceIds, ct);
        var giveBack = oldDays is null ? WorksheetDays.None : new WorksheetDays(WorksheetCalculator.Parse(S(oldDays, "CLMBoardDays")) ?? 0, WorksheetCalculator.Parse(S(oldDays, "CLMICUDays")) ?? 0,
            WorksheetCalculator.Parse(S(oldDays, "CLMCashDays")) ?? 0, WorksheetCalculator.Parse(S(oldDays, "CLMGurdianDays")) ?? 0, WorksheetCalculator.Parse(S(oldDays, "CLMKIDDCDays")) ?? 0);
        await SaveSameLimitAsync(ws, c.LinkCase, input, preview.Calculation, giveBack, ct);
        var note = await RaiseNoteAsync(ws, c.Number, version, c.Payor, preview.Calculation.PayableToMember, ct);
        await ws.CommitAsync(ct);

        var balanceAfter = await UpdateCaseAndBalanceAsync(c, preview.Calculation.AnnualBalance is not null, ct);
        return Done("Worksheet.Amend", c, version, preview, r, user, invoiceIds, note, balanceAfter);
    }

    /// <summary>
    /// Legacy saveSameLimit: the linked case's (or this case's) ClaimSameLimit row keeps the days still allowed and this worksheet's limits, so a later version
    /// or a linked case only gets what is left. A new row starts from the plan's day caps, an existing one is reduced by the days entered on the invoices.
    /// An amendment first gives back the days the version had been allowed (<paramref name="giveBack"/>); a version that created no row leaves none.
    /// </summary>
    private static async Task SaveSameLimitAsync(IDbSession ws, string target, WorksheetInput input, WorksheetCalculation calc, WorksheetDays? giveBack, CancellationToken ct)
    {
        var existing = (await ws.QueryAsync("SELECT CLMBoardDays, CLMICUDays, CLMCashDays, CLMGurdianDays, CLMKIDDCDays FROM ClaimSameLimit WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @target",
            new { target }, ct)).FirstOrDefault();
        if (existing is null && giveBack is not null) return;
        var back = giveBack ?? WorksheetDays.None;
        decimal Cap(string code) => input.Benefits.FirstOrDefault(b => b.Code == code)?.Days ?? 0;
        decimal Before(string column, string code, decimal returned) => existing is null ? Cap(code) : (WorksheetCalculator.Parse(S(existing, column)) ?? 0) + returned;
        static string Left(decimal before, decimal used) => DaysText(Math.Max(0, before - used));
        var e = input.Days;

        var sameCols = (await ws.QueryAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('ClaimSameLimit')", null, ct)).Select(x => S(x, "name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var args = new Dictionary<string, object?>
        {
            ["target"] = target, ["d0"] = Left(Before("CLMBoardDays", "101", back.Board), e.Board), ["d1"] = Left(Before("CLMICUDays", "102", back.Icu), e.Icu),
            ["d2"] = Left(Before("CLMCashDays", "601", back.Cash), e.Cash), ["d3"] = Left(Before("CLMGurdianDays", "701", back.Guardian), e.Guardian),
            ["d4"] = Left(Before("CLMKIDDCDays", "404", back.KidneyMonths), e.KidneyMonths),
        };
        var cols = WorksheetCatalog.Lines.Select(l => l.Column).ToList();
        var limitCols = new List<string>();
        for (var i = 0; i < cols.Count; i++)
            if (sameCols.Contains(cols[i])) { limitCols.Add(cols[i]); args["c" + limitCols.Count] = calc.Lines[i].Limit ?? "-"; }
        string[] days = ["CLMBoardDays", "CLMICUDays", "CLMCashDays", "CLMGurdianDays", "CLMKIDDCDays"];
        if (existing is null)
            await ws.ExecuteAsync($"INSERT INTO ClaimSameLimit (CASNumber, {string.Join(", ", limitCols)}, {string.Join(", ", days)}, CLMINHOSPhyDays) " +
                $"VALUES (@target, {string.Join(", ", limitCols.Select((_, i) => "@c" + (i + 1)))}, @d0, @d1, @d2, @d3, @d4, '0')", args, ct);
        else
            await ws.ExecuteAsync($"UPDATE ClaimSameLimit SET {string.Join(", ", limitCols.Select((col, i) => $"{col} = @c{i + 1}"))}, " +
                $"{string.Join(", ", days.Select((d, i) => $"{d} = @d{i}"))} WHERE CASNumber = @target", args, ct);
    }
}
