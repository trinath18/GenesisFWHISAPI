using System.Data;
using System.Data.Common;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Infrastructure.Data;
using GenesisFIS.Core.Membership;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

/// <summary>
/// Remaining tabs of the legacy frmNewMBMEnquiry. The VB6 form passed raw SQL fragments to several procedures
/// (SearchMBMHistory06, Case_SearchIllness, SP_SearchLTLmt, Case_Registration); those are replaced here by
/// parameterised queries against the same views/tables the procedures used.
/// </summary>
internal sealed class MembershipTabsRepository(IDbConnectionFactory connections) : SqlRepository(connections), IMembershipTabsRepository
{
    private const int Timeout = 120;

    private sealed record Key(string Number, string? Ic, string? Name, DateTime? Dob, string? Ins, string? Plan,
        string? Health, string? Payor, DateTime? PayorEff, string? PolicyNo);

    private async Task<Key?> LoadKeyAsync(string number, CancellationToken ct)
    {
        await using var conn = await Db.OpenAsync(GenesisDb.Main, ct);
        var row = (await conn.QueryAsync(new CommandDefinition(
            "SELECT MBMNumber, MBMIcBcPp, MBMName, MBMDOB, INSCode, PLNCode, HLTCode, PAYCode, MBMPayorEffDate, MBMPolicyNo FROM MBM WHERE MBMNumber = @number",
            new { number = number.Trim() }, commandTimeout: Timeout, cancellationToken: ct))).FirstOrDefault();
        if (row is null) return null;
        var r = (IDictionary<string, object?>)row;
        return new Key(S(r, "MBMNumber") ?? number, S(r, "MBMIcBcPp"), S(r, "MBMName"), D(r, "MBMDOB"), S(r, "INSCode"),
            S(r, "PLNCode"), S(r, "HLTCode"), S(r, "PAYCode"), D(r, "MBMPayorEffDate"), S(r, "MBMPolicyNo"));
    }

    public async Task<IReadOnlyList<CoveredPerson>> GetCoveredPersonsAsync(string number, CancellationToken ct = default)
    {
        const string sql = """
            SELECT MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCDOB, MBMCSex, MBMCAdultChild, MBMCRELCode, MBMCStatus, MBMCPLNCode,
                   MBMCMemberType, MBMCAnnualLimit, MBMCAvailableLimit, MBMCEffDate, MBMCExpDate, MBMCCancelDate, MBMCPremium,
                   MBMCBordxDate, MBMCBatchNo
            FROM MBMCoveredPersons WHERE MBMCNumber = @number ORDER BY MBMCCoverID
            """;
        return (await QueryAsync(GenesisDb.Main, sql, new { number = number.Trim() }, ct)).Select(r => new CoveredPerson(
            S(r, "MBMCCoverID") ?? "", S(r, "MBMCName"), S(r, "MBMCICBCPPNo"), D(r, "MBMCDOB"), S(r, "MBMCSex"), S(r, "MBMCAdultChild"),
            S(r, "MBMCRELCode"), S(r, "MBMCStatus"), S(r, "MBMCPLNCode"), S(r, "MBMCMemberType"), Dec(r, "MBMCAnnualLimit"),
            Dec(r, "MBMCAvailableLimit"), D(r, "MBMCEffDate"), D(r, "MBMCExpDate"), D(r, "MBMCCancelDate"), Dec(r, "MBMCPremium"),
            D(r, "MBMCBordxDate"), I(r, "MBMCBatchNo"))).ToList();
    }

    public async Task<MemberBenefits?> GetBenefitsAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key is null) return null;
        var principal = await BenefitsFor(key.Plan, key, ct);
        var supplementary = new List<BenefitListing>();
        if (key.Ins is "F" or "H")
        {
            var plans = (await GetCoveredPersonsAsync(number, ct)).Select(c => c.PlanCode).Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct().Where(p => p != key.Plan);
            foreach (var plan in plans) supplementary.Add(await BenefitsFor(plan, key, ct));
        }
        return new MemberBenefits(principal, supplementary);
    }

    private async Task<BenefitListing> BenefitsFor(string? plan, Key key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(key.Health) || string.IsNullOrWhiteSpace(key.Ins) || key.PayorEff is null)
            return new BenefitListing(plan, null, []);
        const string sql = """
            SELECT BNFCode, BNFNoOfDays, BNFEffDate, BNFClaimAbleAmount, BNFDescription, BNFRMPerDis
            FROM View_BNF WHERE PLNCode = @plan AND HLTCode = @health AND INSCode = @ins AND BNFEffDate <= @eff
            ORDER BY BNFEffDate DESC
            """;
        var rows = await QueryAsync(GenesisDb.Main, sql, new { plan, health = key.Health, ins = key.Ins, eff = key.PayorEff }, ct);
        // Legacy behaviour: only the most recent effective-date block applies.
        var latest = rows.Count == 0 ? (DateTime?)null : D(rows[0], "BNFEffDate");
        var items = rows.Where(r => D(r, "BNFEffDate") == latest).Select(r =>
        {
            var amount = S(r, "BNFClaimAbleAmount");
            if (decimal.TryParse(amount, out var d)) amount = Math.Round(d, 2).ToString("#,##0.00");
            return new BenefitItem(S(r, "BNFCode") ?? "", S(r, "BNFDescription"), I(r, "BNFNoOfDays") ?? 0, amount, S(r, "BNFRMPerDis"));
        }).ToList();
        return new BenefitListing(plan, latest, items);
    }

    public async Task<MemberHistory?> GetMemberHistoryAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key is null) return null;
        var byIc = key.Ic is { Length: > 4 } ? await History("m.MBMIcBcPp = @ic", new { ic = key.Ic }, ct) : [];
        var byName = key.Name is not null && key.Dob is not null ? await History("m.MBMName = @name AND m.MBMDOB = @dob", new { name = key.Name, dob = key.Dob }, ct) : [];
        return new MemberHistory(byIc, byName);
    }

    private async Task<List<MemberHistoryItem>> History(string predicate, object args, CancellationToken ct)
    {
        var sql = $"""
            SELECT m.MBMNumber, m.MBMCOVID, m.MBMTopupInd, m.MBMTopupNumber, m.MBMPolicyNo, m.MBMPayorEffDate,
                   g.CoPolicyExpDate AS MBMPayorExpDate, m.MBMDataStatus, m.MBMStatus, m.MBMBordxDate
            FROM GroupCompanyFW g RIGHT OUTER JOIN MBM m ON g.CoPolicyNo = m.MBMPolicyNo
            WHERE {predicate}
            ORDER BY m.MBMPayorEffDate, m.MBMNumber
            """;
        return (await QueryAsync(GenesisDb.Main, sql, args, ct)).Select(r => new MemberHistoryItem(
            S(r, "MBMNumber") ?? "", S(r, "MBMCOVID"), S(r, "MBMTopupInd"), S(r, "MBMTopupNumber"), S(r, "MBMPolicyNo"),
            D(r, "MBMPayorEffDate"), D(r, "MBMPayorExpDate"), S(r, "MBMDataStatus"), S(r, "MBMStatus"), D(r, "MBMBordxDate"))).ToList();
    }

    public async Task<MemberCaseHistory?> GetCaseHistoryAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key is null) return null;
        var covered = key.Ins is "F" or "H" ? await GetCoveredPersonsAsync(number, ct) : [];

        var byIc = new List<MemberCase>();
        if (key.Ic is { Length: > 5 })
        {
            byIc.AddRange(await Cases("SearchCASHistory", new { ICNumber = key.Ic }, ct));
            byIc.AddRange(await Cases("SearchCasHistorySupp", new { ICNumber = key.Ic }, ct));
        }
        foreach (var ic in covered.Select(c => c.IcBcPp).Where(i => i is { Length: > 5 }))
            if (await CrossReferenceExistsAsync("MBMIcBcPp = @v", new { v = ic }, ct))
                byIc.AddRange(await Cases("SearchCASHistory", new { ICNumber = ic }, ct));

        var byName = new List<MemberCase>();
        if (key.Name is not null && key.Dob is not null)
        {
            byName.AddRange(await Cases("SearchCASHistoryTwo", new { MBMName = key.Name, MBMDOB = key.Dob }, ct));
            byName.AddRange(await Cases("SearchCasHistorySuppTwo", new { MBMName = key.Name, MBMDOB = key.Dob }, ct));
            if (covered.Count > 0 && await CrossReferenceExistsAsync("MBMName = @name AND MBMDOB = @dob", new { name = key.Name, dob = key.Dob }, ct))
                foreach (var c in covered.Where(c => !string.IsNullOrWhiteSpace(c.Name) && c.DateOfBirth is not null))
                    byName.AddRange(await Cases("SearchCASHistoryTwo", new { MBMName = c.Name, MBMDOB = c.DateOfBirth }, ct));
        }
        return new MemberCaseHistory(byIc, byName);
    }

    private async Task<bool> CrossReferenceExistsAsync(string predicate, object args, CancellationToken ct) =>
        (await QueryAsync(GenesisDb.Maintenance, $"SELECT TOP (1) 1 AS x FROM MBMCrossReference WHERE {predicate}", args, ct)).Count > 0;

    private async Task<List<MemberCase>> Cases(string proc, object args, CancellationToken ct) =>
        (await QueryAsync(GenesisDb.Main, proc, args, ct, CommandType.StoredProcedure)).Select(ToMemberCase).ToList();

    public async Task<MemberFees?> GetFeesAsync(string number, CancellationToken ct = default)
    {
        const string sql = "SELECT MBMPremium, MBMMCOFee, MBMAccessFee, MBMBasicPrem, MBMBasicMCO, MBMPayorMCO, MBMBilledMCO FROM MBMOthers WHERE MBMNumber = @number";
        var r = (await QueryAsync(GenesisDb.Main, sql, new { number = number.Trim() }, ct)).FirstOrDefault();
        return r is null ? null : new MemberFees(Dec(r, "MBMPremium"), Dec(r, "MBMMCOFee"), Dec(r, "MBMAccessFee"),
            Dec(r, "MBMBasicPrem"), Dec(r, "MBMBasicMCO"), Dec(r, "MBMPayorMCO"), Dec(r, "MBMBilledMCO"));
    }

    public async Task<IReadOnlyList<NonDisclosureItem>> GetNonDisclosureAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key?.Ic is null) return [];
        const string sql = "SELECT * FROM VIEW_Cas_SearchIll WHERE MBMIcBcPp = @ic";
        return (await QueryAsync(GenesisDb.Main, sql, new { ic = key.Ic }, ct)).Select(r => new NonDisclosureItem(
            S(r, "MBMNumber") ?? "", S(r, "CASNumber"), S(r, "MBMPatientCovID"), D(r, "CASDateAdmitted"), D(r, "CASPreIllDate"),
            S(r, "CASPreIllCAT") switch { "1" => "Illness", "2" => "Injury", "3" => "Hospitalisation", "4" => S(r, "CASPreIllOthers"), _ => null },
            S(r, "CASPreIllInfBy"), D(r, "CASUndIllDate"),
            S(r, "CASUndIllCAT") switch { "1" => "Diabetes", "2" => "Hypertension", "3" => S(r, "CASUndIllOthers"), _ => null },
            S(r, "CASUndIllInfBy"))).ToList();
    }

    public async Task<IReadOnlyList<PolicyTerm>> GetPolicyTermsAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key is null || string.IsNullOrWhiteSpace(key.Plan)) return [];
        // Legacy Case_Registration case 6: health code S matches on plan only; N also matches the payor.
        var byPayor = key.Health == "N";
        if (key.Health is not ("S" or "N")) return [];
        var sql = "SELECT TOP (1) PLNRefRqdAdmin, PLNRefRqdPreSpec, PLNMinDurHosp, PLNPhyVstPerDay, PLNSepPeriodPrin, PLNSepPeriodSup, " +
                  "PLNLowerAgeLimit, PLNUpperAgeLimit, PLNStudentAge, PLNChildUntilAge FROM PLN WHERE PLNCode = @plan" +
                  (byPayor ? " AND PAYCode = @payor" : "");
        var r = (await QueryAsync(GenesisDb.Main, sql, new { plan = key.Plan, payor = key.Payor }, ct)).FirstOrDefault();
        if (r is null) return [];
        return
        [
            new("Referral required for admission", S(r, "PLNRefRqdAdmin")),
            new("Referral required for pre-specialist", S(r, "PLNRefRqdPreSpec")),
            new("Minimum duration of hospitalisation", S(r, "PLNMinDurHosp")),
            new("Physician visits per day", S(r, "PLNPhyVstPerDay")),
            new("Separation period (principal)", S(r, "PLNSepPeriodPrin")),
            new("Separation period (supplementary)", S(r, "PLNSepPeriodSup")),
            new("Lower age limit", S(r, "PLNLowerAgeLimit")),
            new("Upper age limit", S(r, "PLNUpperAgeLimit")),
            new("Student age", S(r, "PLNStudentAge")),
            new("Child until age", S(r, "PLNChildUntilAge")),
        ];
    }

    public async Task<IReadOnlyList<LifetimeLimitRow>> GetLifetimeLimitAsync(string number, CancellationToken ct = default)
    {
        var n = number.Trim();
        var result = new List<LifetimeLimitRow>();
        var principals = await QueryAsync(GenesisDb.Main,
            "SELECT MBMNumber, MBMCOVID, MBMTopupNumber, MBMAvailableLimit, MBMLifeTimeLmt, INSCode, MBMName, MBMGuaRenewal FROM View_Search_LTPrinLmt WHERE MBMNumber = @n", new { n }, ct);
        foreach (var p in principals)
        {
            var topup = S(p, "MBMTopupNumber");
            var available = Dec(p, "MBMAvailableLimit") ?? 0;
            var balance = available;
            if (S(p, "MBMGuaRenewal") == "3")
                balance = (await QueryAsync(GenesisDb.Main, "SELECT MBMLifeTimeLmt FROM View_Search_LTPrinLmt WHERE MBMNumber = @topup", new { topup }, ct))
                    .Select(r => Dec(r, "MBMLifeTimeLmt")).FirstOrDefault() ?? 0;
            var cur = (await QueryAsync(GenesisDb.Main, "SELECT Cases, BordxAmt FROM View_Search_LTPrinCase WHERE MBMNumber = @n", new { n }, ct)).LastOrDefault();
            var his = (await QueryAsync(GenesisDb.Main, "SELECT CasHistory, BordxAmt FROM View_Search_LTPrinCaseHistory WHERE MBMTopupNumber = @topup", new { topup }, ct)).LastOrDefault();
            result.Add(new LifetimeLimitRow(S(p, "MBMNumber") ?? n, S(p, "MBMCOVID"), S(p, "MBMName"), available, balance,
                cur is null ? 0 : I(cur, "Cases") ?? 0, his is null ? 0 : I(his, "CasHistory") ?? 0,
                cur is null ? 0 : Dec(cur, "BordxAmt") ?? 0, his is null ? 0 : Dec(his, "BordxAmt") ?? 0));

            if (S(p, "INSCode") is "F" or "H")
            {
                var supps = await QueryAsync(GenesisDb.Main,
                    "SELECT MBMTopupNumber, MBMLifeTimeLmt, MBMCNumber, MBMCCoverID, MBMCAvailableLimit, MBMCName, MBMGuaRenewal FROM View_Search_LTSuppLmt WHERE MBMCNumber = @n", new { n }, ct);
                foreach (var s in supps)
                {
                    var cover = S(s, "MBMCCoverID");
                    var sAvail = Dec(s, "MBMCAvailableLimit") ?? 0;
                    var sBal = sAvail;
                    if (S(p, "MBMGuaRenewal") == "3")
                        sBal = (await QueryAsync(GenesisDb.Main, "SELECT MBMLifeTimeLmt FROM View_Search_LTSuppLmt WHERE MBMCNumber = @topup AND MBMCCoverID = @cover", new { topup, cover }, ct))
                            .Select(r => Dec(r, "MBMLifeTimeLmt")).FirstOrDefault() ?? 0;
                    var sCur = (await QueryAsync(GenesisDb.Main, "SELECT Cases, BordxAmt FROM View_Search_LTSuppCase WHERE MBMCNumber = @n AND MBMCCoverID = @cover", new { n, cover }, ct)).LastOrDefault();
                    var sHis = (await QueryAsync(GenesisDb.Main, "SELECT CasHistory, BordxAmt FROM VIEW_Search_LTSuppCaseHistory WHERE MBMTopupNumber = @topup AND MBMCCoverID = @cover", new { topup, cover }, ct)).LastOrDefault();
                    result.Add(new LifetimeLimitRow(S(s, "MBMCNumber") ?? n, cover, S(s, "MBMCName"), sAvail, sBal,
                        sCur is null ? 0 : I(sCur, "Cases") ?? 0, sHis is null ? 0 : I(sHis, "CasHistory") ?? 0,
                        sCur is null ? 0 : Dec(sCur, "BordxAmt") ?? 0, sHis is null ? 0 : Dec(sHis, "BordxAmt") ?? 0));
                }
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<TakeoverClaim>> GetTakeoverClaimsAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (string.IsNullOrWhiteSpace(key?.PolicyNo)) return [];
        const string sql = """
            SELECT CLMEffDate, CLMExpDate, CLMPatientName, CLMPatientIC, CLMDOA, CLMDOD, CLMHospital, CLMDiagnosis1, CLMDiagnosis2,
                   CLMDiagnosis3, CLMDrName1, CLMDrName2, CLMBillAmt, CLMAppAmt, CLMExcessAmt
            FROM CLMMAATakaful WHERE CLMPolNo = @policy ORDER BY CLMIndex
            """;
        return (await QueryAsync(GenesisDb.Main, sql, new { policy = key.PolicyNo }, ct)).Select(r => new TakeoverClaim(
            D(r, "CLMEffDate"), D(r, "CLMExpDate"), S(r, "CLMPatientName"), S(r, "CLMPatientIC"), D(r, "CLMDOA"), D(r, "CLMDOD"),
            S(r, "CLMHospital"),
            string.Join("/", new[] { S(r, "CLMDiagnosis1"), S(r, "CLMDiagnosis2"), S(r, "CLMDiagnosis3") }.Where(x => x is not null)),
            string.Join("/", new[] { S(r, "CLMDrName1"), S(r, "CLMDrName2") }.Where(x => x is not null)),
            Dec(r, "CLMBillAmt"), Dec(r, "CLMAppAmt"), Dec(r, "CLMExcessAmt"))).ToList();
    }

    public async Task<string?> GetPolicyWordingFolderAsync(string number, CancellationToken ct = default)
    {
        var key = await LoadKeyAsync(number, ct);
        if (key is null || string.IsNullOrWhiteSpace(key.Plan)) return null;
        var rows = await QueryAsync(GenesisDb.Main,
            "SELECT TOP (1) PLNPolicyWording FROM PLN WHERE PLNCode = @plan AND PAYCode = @payor AND HLTCode = @health",
            new { plan = key.Plan, payor = key.Payor, health = key.Health }, ct);
        return rows.Count == 0 ? null : S(rows[0], "PLNPolicyWording");
    }
}


