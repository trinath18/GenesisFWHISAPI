using System.Data;
using Dapper;
using Genesis.Core.Cases;
using Genesis.Core.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Cases;

/// <summary>
/// Read-only port of the legacy frmCaseInquiry. The VB6 form passed raw SQL fragments to Case_Inquiry / Case_Adjustment /
/// Case_SearchIllness; those calls are replaced by parameterised queries on the same tables and views. The case record itself
/// still comes from SearchCasAdjustment (parameterised by case number).
/// </summary>
internal sealed class CaseInquiryRepository(IDbConnectionFactory db) : ICaseInquiryRepository
{
    private const int Timeout = 120;

    private async Task<List<IDictionary<string, object?>>> QueryAsync(GenesisDb database, string sql, object args, CancellationToken ct,
        CommandType type = CommandType.Text)
    {
        await using var conn = await db.OpenAsync(database, ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, args, commandType: type, commandTimeout: Timeout, cancellationToken: ct));
        return rows.Select(r => (IDictionary<string, object?>)r).ToList();
    }

    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    public async Task<CaseSearchPage> SearchAsync(CaseSearchBy by, string value, string? planCode, CancellationToken ct = default)
    {
        var v = value.Trim();
        var (predicate, arg) = by switch
        {
            CaseSearchBy.MemberNo => ("MBMNumber LIKE @v", Like(v) + "%"),
            CaseSearchBy.PolicyNo => ("MBMPolicyNo = @v", v),
            CaseSearchBy.Name => ("MBMName LIKE @v", "%" + Like(v) + "%"),
            CaseSearchBy.Ic => ("MBMIcBcPp = @v", v),
            CaseSearchBy.CaseNo => ("CASNumber LIKE @v", Like(v) + "%"),
            CaseSearchBy.EspRefNo => ("CASESPRefNo = @v", v),
            _ => ("ClaimID = @v", v),
        };
        var sql = $"""
            SELECT TOP (@take) CASNumber, MBMNumber, MBMPatientCovID, MBMPolicyNo, MBMName, INSCode, PLNCode, HLTCode, CASHOSCode,
                   CASDateAdmitted, CASDischargeDate, CASReserveAmount, CASStatus, PAYCode, ClaimID, CASESPRefNo
            FROM View_FWCase_Search
            WHERE MBMName IS NOT NULL AND {predicate} {(string.IsNullOrWhiteSpace(planCode) ? "" : "AND PLNCode = @plan")}
            ORDER BY CASNumber DESC
            """;
        var rows = await QueryAsync(GenesisDb.Main, sql, new { take = CaseNumber.SearchLimit + 1, v = arg, plan = planCode?.Trim() }, ct);
        var items = rows.Take(CaseNumber.SearchLimit).Select(r => new CaseSearchResult(
            S(r, "CASNumber") ?? "", S(r, "MBMNumber") ?? "", S(r, "MBMPatientCovID"), S(r, "MBMPolicyNo"), S(r, "MBMName"), S(r, "INSCode"),
            S(r, "PLNCode"), S(r, "HLTCode"), S(r, "CASHOSCode"), D(r, "CASDateAdmitted"), D(r, "CASDischargeDate"), Dec(r, "CASReserveAmount"),
            S(r, "CASStatus"), S(r, "PAYCode"), S(r, "ClaimID"), S(r, "CASESPRefNo"))).ToList();
        return new CaseSearchPage(items, rows.Count > CaseNumber.SearchLimit);
    }

    public async Task<string?> GetMemberNumberAsync(string caseNumber, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT MBMNumber FROM CAS WHERE CASNumber = @c", new { c = caseNumber.Trim() }, ct))
            .Select(r => S(r, "MBMNumber")).FirstOrDefault();

    private static List<string> Seq(IDictionary<string, object?> r, string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => S(r, prefix + i)).Where(s => s is not null).Select(s => s!).ToList();

    private static string? Text(string? s) => s?.Replace("~", Environment.NewLine);

    public async Task<CaseDetail?> GetAsync(string caseNumber, CancellationToken ct = default)
    {
        var c = caseNumber.Trim();
        var r = (await QueryAsync(GenesisDb.Main, "SearchCasAdjustment", new { CASNumber = c }, ct, CommandType.StoredProcedure)).FirstOrDefault();
        if (r is null) return null;

        var covered = S(r, "MBMPatientCovID") != "00";
        var coveredName = covered
            ? (await QueryAsync(GenesisDb.Main, "SELECT MBMCName FROM MBMCoveredPersons WHERE MBMCNumber = @m AND MBMCCoverID = @id",
                new { m = S(r, "MBMNumber"), id = S(r, "MBMPatientCovID") }, ct)).Select(x => S(x, "MBMCName")).FirstOrDefault()
            : null;

        var header = new CaseHeader(
            S(r, "CASNumber") ?? c, S(r, "MBMNumber") ?? "", S(r, "MBMPatientCovID"), covered ? coveredName ?? S(r, "MBMName") : S(r, "MBMName"),
            D(r, "CASDateAdmitted"), D(r, "CASDischargeDate"), D(r, "CaseRegDate"), S(r, "HOSName") ?? S(r, "CASHOSName"), S(r, "CASStatus"),
            S(r, "CASClaimability"), S(r, "CASPending"), S(r, "CASStayType"), S(r, "CASPAYType"), S(r, "ModalityCode"), S(r, "ClaimNatureCode"),
            S(r, "CASAdmissionReason"), S(r, "ReferedBy"), Dec(r, "CASLGAmt"), S(r, "CASLGTime"), Dec(r, "CASReserveAmount"), S(r, "CASQuery"),
            S(r, "LINKCASNo"), S(r, "CASTopupLmt"), S(r, "CASInd"), S(r, "CASESPRefNo"), S(r, "ClaimID"), I(r, "CASPolYear"), I(r, "RoomBoardDay"),
            Dec(r, "CASTakeOverAmt"), S(r, "CASTakeOverInd"), S(r, "CASTakeOverPlan"), S(r, "CASIntExtTO"));

        var member = new CaseMember(
            S(r, "MBMPolicyNo"), S(r, "MBMName"), S(r, "MBMIcBcPp"), D(r, "MBMDOB"), S(r, "PAYCode"), S(r, "INSCode"), S(r, "PLNCode"), S(r, "HLTCode"),
            S(r, "ProductName"), S(r, "MBMStatus"), S(r, "MBMExpiredStatus"), Dec(r, "MBMAvailableLimit"), D(r, "MBMPayorEffDate"), D(r, "MBMPayorExpDate"),
            S(r, "GRPCompany"), S(r, "MBMMemberType"), S(r, "MBMAdultChild"),
            string.Join(", ", new[] { S(r, "CoGRPAdd1") ?? S(r, "MBMAddress1"), S(r, "CoGRPAdd2") ?? S(r, "MBMAddress2"), S(r, "CoGRPAdd3") ?? S(r, "MBMAddress3"),
                S(r, "CoGRPAdd4"), S(r, "CoGRPCity") ?? S(r, "CTYCode"), S(r, "CoGRPPCode") ?? S(r, "MBMPostCode") }.Where(x => x is not null)));

        var procedures = new List<CaseProcedure>();
        for (var i = 1; i <= 6; i++)
        {
            var surg = new CaseProcedure(S(r, $"CASOPROCRequired{i}"), S(r, $"CASCptCode{i}"), Dec(r, $"CASProcActSurgFees{i}"), Dec(r, $"CASProcActAnaeFees{i}"), null, false);
            if (surg.Procedure is not null || surg.CptCode is not null) procedures.Add(surg);
        }
        for (var i = 1; i <= 6; i++)
        {
            var non = new CaseProcedure(S(r, $"CASONonSurgPROCRequired{i}"), S(r, $"CASNonSurgCptCode{i}"), null, null, Dec(r, $"CASProcActNonSurgFees{i}"), true);
            if (non.Procedure is not null || non.CptCode is not null) procedures.Add(non);
        }
        var clinical = new CaseClinical(
            procedures, Seq(r, "CASOProcInvestigationDone", 3), Seq(r, "CASOMedSurMgmt", 3), Seq(r, "CASOMedOnDis", 3), Seq(r, "CASInvCode", 3),
            S(r, "CASOCondWillRecur"), S(r, "CASOFollowUpRequired"), S(r, "CASFolUp"), S(r, "CASFirstCall"), S(r, "CASFirstTreated"), Dec(r, "CASRedAmt"),
            S(r, "CASPayPending"), Seq(r, "CASAttendDoctor", 5), Seq(r, "CASAnaesName", 3), D(r, "CASFirstDocDate"), D(r, "CASSubSeqDocDate"),
            S(r, "CASPatientAge"), S(r, "CASPatientDayYr"));

        var underlying = Enumerable.Range(1, 3).Select(i => new CaseUnderlying(S(r, $"CASUDCode{i}"), S(r, $"CASUDReasons{i}"), D(r, $"CASUDDOS{i}")))
            .Where(u => u.Code is not null || u.Reason is not null).ToList();
        var complaint = D(r, "CASComplaintDate") is null && S(r, "CASComplaintIssue") is null
            ? null
            : new CaseComplaint(D(r, "CASComplaintDate"), S(r, "CASComplaintIssue"), S(r, "CASComplainant"), S(r, "CASCompAction"));
        var remarks = new CaseRemarks(
            Text(string.Concat(S(r, "CASORemarks"), S(r, "CASORemarksTwo")) is { Length: > 0 } m1 ? m1 : null),
            Text(string.Concat(S(r, "CASOTwoRemarks"), S(r, "CASOTwoRemarksTwo")) is { Length: > 0 } m2 ? m2 : null),
            Text(S(r, "CASRemarks")), Text(S(r, "CASRemarksFour")), Text(S(r, "CASRemarksFive")), Text(S(r, "CASORemarksInt")));

        return new CaseDetail(header, member, clinical, await DiagnosesAsync("DIAFirst", "DIAPrelimDiag", c, ct), await DiagnosesAsync("DIAFinal", "DIAFinalDiag", c, ct),
            underlying, complaint, remarks, false);
    }

    private async Task<IReadOnlyList<CaseDiagnosis>> DiagnosesAsync(string table, string textPrefix, string caseNumber, CancellationToken ct)
    {
        var r = (await QueryAsync(GenesisDb.Main, $"SELECT * FROM {table} WHERE CASNumber = @c", new { c = caseNumber }, ct)).FirstOrDefault();
        return r is null ? [] : Enumerable.Range(1, 3).Select(i => new CaseDiagnosis(S(r, $"ICDCode{i}"), S(r, $"{textPrefix}{i}"), D(r, $"DIADOS{i}")))
            .Where(d => d.IcdCode is not null || d.Description is not null).ToList();
    }

    public async Task<CaseGuarantees> GetGuaranteesAsync(string caseNumber, CancellationToken ct = default)
    {
        var c = caseNumber.Trim();
        var lg = (await QueryAsync(GenesisDb.Maintenance, "SELECT LGNo, LGDate, LGAmt, LGIssuedBy, LGRemark FROM CASLG WHERE CASeNo = @c ORDER BY LGDate, LGNo", new { c }, ct))
            .Select(r => new CaseLetterOfGuarantee(S(r, "LGNo"), D(r, "LGDate"), Dec(r, "LGAmt"), S(r, "LGIssuedBy"), S(r, "LGRemark"))).ToList();
        var fu = (await QueryAsync(GenesisDb.Maintenance, "SearchFollowUpLG", new { CASNumber = c }, ct, CommandType.StoredProcedure))
            .Select(r => new CaseFollowUp(S(r, "FollowUpNo"), Dec(r, "LGAmt"), S(r, "FollowUpIssuedBy"), D(r, "FollowUpIssuedDate"), D(r, "FollowUpDate"))).ToList();
        return new CaseGuarantees(lg, fu);
    }

    public async Task<IReadOnlyList<CaseWorksheet>> GetWorksheetsAsync(string caseNumber, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ClaimVersion, CLMTotalApproved, CLMTotalActual, CLMPending, CLMPayableByMedix2H, CLMPayableByMedix2M, BordxRefNo, ClaimCloseDate,
                   BordxDate, CLMBordxAmt, CLMPayorAppStatus, CLMPayorAppDate, CLMPayorAppCode, CLMBordxRevInd, DOSend, CRTCartonNo,
                   DebitCreditRefNo, DebitCreditDate
            FROM VIEW_WS_Listing WHERE CASNumber = @c ORDER BY ClaimVersion
            """;
        return (await QueryAsync(GenesisDb.Worksheet, sql, new { c = caseNumber.Trim() }, ct)).Select(r => new CaseWorksheet(
            I(r, "ClaimVersion") ?? 0, Dec(r, "CLMTotalApproved"), Dec(r, "CLMTotalActual"), S(r, "CLMPending"), Dec(r, "CLMPayableByMedix2H"),
            Dec(r, "CLMPayableByMedix2M"), S(r, "BordxRefNo"), D(r, "ClaimCloseDate"), D(r, "BordxDate"), Dec(r, "CLMBordxAmt"), S(r, "CLMPayorAppStatus"),
            D(r, "CLMPayorAppDate"), S(r, "CLMPayorAppCode"), S(r, "CLMBordxRevInd"), D(r, "DOSend"), S(r, "CRTCartonNo"), S(r, "DebitCreditRefNo"),
            D(r, "DebitCreditDate"))).ToList();
    }

    public async Task<IReadOnlyList<CaseInvoice>> GetInvoicesAsync(string caseNumber, CancellationToken ct = default)
    {
        const string sql = """
            SELECT InvNo, InvDate, INVPayee, INVPayTo, INVWSversion, INVAmount, BordxRefNo, BordxDate, CLMInvPending
            FROM VIEW_INV_Listing WHERE CASNumber = @c ORDER BY InvDate, InvNo
            """;
        return (await QueryAsync(GenesisDb.Worksheet, sql, new { c = caseNumber.Trim() }, ct)).Select(r => new CaseInvoice(
            S(r, "InvNo"), D(r, "InvDate"), S(r, "INVPayee"), S(r, "INVPayTo"), S(r, "INVWSversion"), Dec(r, "INVAmount"), S(r, "BordxRefNo"),
            D(r, "BordxDate"), S(r, "CLMInvPending"))).ToList();
    }

    public async Task<CasePayments> GetPaymentsAsync(string caseNumber, CancellationToken ct = default)
    {
        var c = caseNumber.Trim();
        async Task<IReadOnlyList<CasePayment>> Load(string sql, Func<IDictionary<string, object?>, CasePayment> map) =>
            (await QueryAsync(GenesisDb.Payment, sql, new { c }, ct)).Select(map).ToList();

        return new CasePayments(
            await Load("SELECT ClaimVersion, PVHOSNo, PVHOSDate, PVStatus, PVHOSChequeNo, PVHOSChequeDate, HOSPaidAmt, HOSRemark FROM Payment2HOS WHERE CASNumber = @c ORDER BY ClaimVersion, PVHOSDate",
                r => new CasePayment(S(r, "PVHOSNo"), I(r, "ClaimVersion"), D(r, "PVHOSDate"), S(r, "PVHOSChequeNo"), D(r, "PVHOSChequeDate"), Dec(r, "HOSPaidAmt"), S(r, "PVStatus"), S(r, "HOSRemark"))),
            await Load("SELECT ClaimVersion, PVNo, PVDate, PVChequeNo, PVChequeDate, MBMPaidAmt, PVStatus, Remark FROM Payment2MBM WHERE CASNumber = @c ORDER BY ClaimVersion, PVDate",
                r => new CasePayment(S(r, "PVNo"), I(r, "ClaimVersion"), D(r, "PVDate"), S(r, "PVChequeNo"), D(r, "PVChequeDate"), Dec(r, "MBMPaidAmt"), S(r, "PVStatus"), S(r, "Remark"))),
            await Load("SELECT ClaimVersion, PVNo, PVDate, PVChequeNo, PVChequeDate, MBMPaidAmt, Remark FROM Payment2MBMReissue WHERE CASNumber = @c ORDER BY ClaimVersion, PVDate",
                r => new CasePayment(S(r, "PVNo"), I(r, "ClaimVersion"), D(r, "PVDate"), S(r, "PVChequeNo"), D(r, "PVChequeDate"), Dec(r, "MBMPaidAmt"), null, S(r, "Remark"))),
            await Load("SELECT ClaimVersion, ReceiptNo, ReceiptDate, ReceiptChequeNo, ReceiptAmt, MBMRemarks FROM ReceiptFrMBM WHERE CASNumber = @c ORDER BY ClaimVersion, ReceiptDate",
                r => new CasePayment(S(r, "ReceiptNo"), I(r, "ClaimVersion"), D(r, "ReceiptDate"), S(r, "ReceiptChequeNo"), null, Dec(r, "ReceiptAmt"), null, S(r, "MBMRemarks"))),
            await Load("SELECT ClaimVersion, ReceiptNo, ReceiptDate, ChequeNo, CheckAmt, Remark FROM ReceiptFrPAY WHERE CASNumber = @c ORDER BY ClaimVersion, ReceiptDate",
                r => new CasePayment(S(r, "ReceiptNo"), I(r, "ClaimVersion"), D(r, "ReceiptDate"), S(r, "ChequeNo"), null, Dec(r, "CheckAmt"), null, S(r, "Remark"))),
            await Load("SELECT ClaimVersion, ReceiptNo, ReceiptDate, ChequeNo, CheckAmt, Remark FROM ReceiptFrMNRB WHERE CASNumber = @c ORDER BY ClaimVersion, ReceiptDate",
                r => new CasePayment(S(r, "ReceiptNo"), I(r, "ClaimVersion"), D(r, "ReceiptDate"), S(r, "ChequeNo"), null, Dec(r, "CheckAmt"), null, S(r, "Remark"))));
    }

    public async Task<CaseAccountSummary?> GetAccountSummaryAsync(string caseNumber, CancellationToken ct = default)
    {
        const string sql = """
            SELECT totalApprove, totalReimburse, totalExcess, totalPayable2Mbm, totalPayable2Hos, totalPaidByMBM, CLMFinalApprove, totalPaid2MBM, totalPaid2Hos
            FROM view_account_summary WHERE CASNumber = @c
            """;
        var r = (await QueryAsync(GenesisDb.Worksheet, sql, new { c = caseNumber.Trim() }, ct)).FirstOrDefault();
        return r is null ? null : new CaseAccountSummary(Dec(r, "totalApprove") ?? 0, Dec(r, "totalReimburse") ?? 0, Dec(r, "totalExcess") ?? 0,
            Dec(r, "totalPayable2Mbm") ?? 0, Dec(r, "totalPayable2Hos") ?? 0, Dec(r, "totalPaidByMBM") ?? 0, Dec(r, "CLMFinalApprove") ?? 0,
            Dec(r, "totalPaid2MBM") ?? 0, Dec(r, "totalPaid2Hos") ?? 0);
    }

    public async Task<IReadOnlyList<CaseReminder>> GetRemindersAsync(string caseNumber, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Worksheet, "SearchCLMReminder", new { CASNumber = caseNumber.Trim() }, ct, CommandType.StoredProcedure))
            .Select(r => new CaseReminder(S(r, "REMNumber"), S(r, "REMVersion"), D(r, "REMDate"), S(r, "REMRMF"), S(r, "REMOrinInv"), S(r, "REMRpt"),
                S(r, "REMProposalForm"), S(r, "REMRefLetter"), S(r, "REMSubroForm"), S(r, "REMReceipt"), S(r, "REMStudent"), S(r, "REMPolice"),
                S(r, "REMOthers1"), S(r, "REMOthers2"), S(r, "REMRegUser"), D(r, "REMRegDate"))).ToList();

    public async Task<CaseRepudiation?> GetRepudiationAsync(string caseNumber, CancellationToken ct = default)
    {
        var r = (await QueryAsync(GenesisDb.Main, "SELECT * FROM CASRepudiation WHERE CASNumber = @c", new { c = caseNumber.Trim() }, ct)).FirstOrDefault();
        if (r is null) return null;
        var reasons = new List<CaseRepudiationReason>();
        for (var i = 1; i <= 3; i++)
        {
            var code = S(r, $"CASREPCode{i}");
            if (code is null && S(r, $"CASREPReasons{i}") is null) continue;
            var desc = code is null ? null : (await QueryAsync(GenesisDb.Main, "SELECT RepDescription FROM RepCode WHERE RepCode = @code", new { code }, ct))
                .Select(x => S(x, "RepDescription")).FirstOrDefault();
            reasons.Add(new CaseRepudiationReason(code, desc, S(r, $"CASREPReasons{i}")));
        }
        return new CaseRepudiation(reasons, S(r, "CASREPIssuedBy"), D(r, "CASREPDate"), S(r, "CASDocument"), S(r, "CASPersonnel"), S(r, "CASAttention"));
    }

    public async Task<IReadOnlyList<CaseCarton>> GetCartonsAsync(string caseNumber, CancellationToken ct = default)
    {
        var c = caseNumber.Trim();
        var claim = (await QueryAsync(GenesisDb.Worksheet,
            "SELECT ClaimVersion, STRCartonNo, CRTCartonStatus, STRFileStatus, STRRequestBy, DORequest, DODelivery, DOReturn, STRRemarks, LastUpdateBy, LastUpdateDate FROM VIEW_Storage_CLM_Search WHERE CASNumber = @c ORDER BY ClaimVersion",
            new { c }, ct)).Select(r => new CaseCarton("Claim", I(r, "ClaimVersion"), S(r, "STRCartonNo"), S(r, "CRTCartonStatus"), S(r, "STRFileStatus"),
            S(r, "STRRequestBy"), D(r, "DORequest"), D(r, "DODelivery"), D(r, "DOReturn"), S(r, "STRRemarks"), S(r, "LastUpdateBy"), D(r, "LastUpdateDate")));
        var rep = (await QueryAsync(GenesisDb.Worksheet,
            "SELECT STRCartonNo, CRTCartonStatus, STRFileStatus, STRRequestBy, DORequest, DODelivery, DOReturn, STRRemarks, LastUpdateBy, LastUpdateDate FROM VIEW_Storage_CLMRep_Search WHERE CASNumber = @c",
            new { c }, ct)).Select(r => new CaseCarton("Repudiated", null, S(r, "STRCartonNo"), S(r, "CRTCartonStatus"), S(r, "STRFileStatus"),
            S(r, "STRRequestBy"), D(r, "DORequest"), D(r, "DODelivery"), D(r, "DOReturn"), S(r, "STRRemarks"), S(r, "LastUpdateBy"), D(r, "LastUpdateDate")));
        return claim.Concat(rep).ToList();
    }

    private sealed record CaseKey(string? MemberNumber, string? Cover, DateTime? Admitted, string? Plan, string? Health, string? Ins, string? Payor,
        string? MemberName, DateTime? Dob);

    private async Task<CaseKey?> KeyAsync(string caseNumber, CancellationToken ct)
    {
        const string sql = """
            SELECT c.MBMNumber, c.MBMPatientCovID, c.CASDateAdmitted, m.PLNCode, m.HLTCode, m.INSCode, m.PAYCode, m.MBMName, m.MBMDOB
            FROM CAS c INNER JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE c.CASNumber = @c
            """;
        var r = (await QueryAsync(GenesisDb.Main, sql, new { c = caseNumber.Trim() }, ct)).FirstOrDefault();
        return r is null ? null : new CaseKey(S(r, "MBMNumber"), S(r, "MBMPatientCovID"), D(r, "CASDateAdmitted"), S(r, "PLNCode"), S(r, "HLTCode"),
            S(r, "INSCode"), S(r, "PAYCode"), S(r, "MBMName"), D(r, "MBMDOB"));
    }

    public async Task<CaseBenefits?> GetBenefitsAsync(string caseNumber, CancellationToken ct = default)
    {
        var key = await KeyAsync(caseNumber, ct);
        if (key is null) return null;
        var plan = key.Plan;
        if (key.Cover is not null and not "00")
        {
            var supp = (await QueryAsync(GenesisDb.Main, "SELECT MBMCPLNCode FROM MBMCoveredPersons WHERE MBMCNumber = @m AND MBMCCoverID = @id",
                new { m = key.MemberNumber, id = key.Cover }, ct)).Select(x => S(x, "MBMCPLNCode")).FirstOrDefault();
            if (supp is not null) plan = supp;
        }
        if (string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(key.Health) || string.IsNullOrWhiteSpace(key.Ins)) return new CaseBenefits(plan, null, []);
        var rows = await QueryAsync(GenesisDb.Main, """
            SELECT BNFCode, BNFNoOfDays, BNFEffDate, BNFClaimAbleAmount, BNFDescription, BNFRMPerDis
            FROM View_BNF WHERE PLNCode = @plan AND HLTCode = @health AND INSCode = @ins AND (@admitted IS NULL OR BNFEffDate <= @admitted)
            ORDER BY BNFEffDate DESC
            """, new { plan, health = key.Health, ins = key.Ins, admitted = key.Admitted }, ct);
        var latest = rows.Count == 0 ? (DateTime?)null : D(rows[0], "BNFEffDate");
        var items = rows.Where(r => D(r, "BNFEffDate") == latest).Select(r =>
        {
            var amount = S(r, "BNFClaimAbleAmount");
            if (decimal.TryParse(amount, out var d)) amount = d.ToString("#,##0.00");
            return new CaseBenefit(S(r, "BNFCode") ?? "", S(r, "BNFDescription"), I(r, "BNFNoOfDays") ?? 0, amount, S(r, "BNFRMPerDis"));
        }).ToList();
        return new CaseBenefits(plan, latest, items);
    }

    public async Task<CaseExclusions?> GetExclusionsAsync(string caseNumber, CancellationToken ct = default)
    {
        var key = await KeyAsync(caseNumber, ct);
        if (key is null) return null;
        var principal = key.Dob is null ? [] : (await QueryAsync(GenesisDb.Main,
            "SELECT MBMExclusion FROM View_Get_MemberExclusion WHERE PAYCode = @pay AND MBMDOB = @dob AND INSCode = @ins AND MBMName = @name",
            new { pay = key.Payor, dob = key.Dob, ins = key.Ins, name = key.MemberName }, ct))
            .Select(r => S(r, "MBMExclusion")).Where(s => s is not null).Select(s => s!).ToList();
        var supp = key.Cover is null or "00" ? [] : (await QueryAsync(GenesisDb.Main,
            "SELECT MBMCExclusion FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @m AND MBMCCoverID = @id", new { m = key.MemberNumber, id = key.Cover }, ct))
            .Select(r => S(r, "MBMCExclusion")).Where(s => s is not null).Select(s => s!).ToList();
        return new CaseExclusions(principal, supp);
    }

    public async Task<IReadOnlyList<SurgicalScheduleItem>> GetSurgicalScheduleAsync(string caseNumber, CancellationToken ct = default)
    {
        var key = await KeyAsync(caseNumber, ct);
        if (key is null || string.IsNullOrWhiteSpace(key.Plan)) return [];
        return (await QueryAsync(GenesisDb.Main,
            "SELECT PAYCode, PLNCode, SurgMainCat, SurgSubCat, SurgPerc FROM PLNSurgOperation WHERE PAYCode = @pay AND PLNCode = @plan ORDER BY SURIndex",
            new { pay = key.Payor, plan = key.Plan }, ct)).Select(r => new SurgicalScheduleItem(S(r, "PAYCode"), S(r, "PLNCode"), S(r, "SurgMainCat"),
            S(r, "SurgSubCat"), I(r, "SurgPerc"))).ToList();
    }
}
