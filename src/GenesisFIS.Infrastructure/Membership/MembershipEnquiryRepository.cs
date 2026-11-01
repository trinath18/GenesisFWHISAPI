using System.Data;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Infrastructure.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Infrastructure.Membership;

/// <summary>
/// Read-only port of the legacy frmNewMBMEnquiry. Uses the same stored procedures / views the VB6 form calls,
/// with parameters instead of concatenated SQL. Columns are read defensively (dynamic rows) because several
/// procedures return slightly different column sets.
/// </summary>
internal sealed class MembershipEnquiryRepository(IDbConnectionFactory connections) : SqlRepository(connections), IMembershipEnquiryRepository
{
    private const int Timeout = 120;

    public async Task<MemberSearchPage> SearchAsync(string prefix, CancellationToken ct = default)
    {
        // Same columns as the legacy Search_MBM_05 procedure, but LEFT JOINs: the procedure's INNER JOINs hide members
        // that have no MBMTwo/MBMOthers row, which the VB6 form worked around by looking up exact numbers directly.
        const string sql = """
            SELECT TOP (@take) m.MBMNumber, m.MBMPolicyNo, m.MBMCOVID, m.MBMIcBcPp, m.MBMName, m.MBMPayorEffDate, m.MBMPayorExpDate,
                   m.MBMValueDate, m.MBMBordxDate, m.MBMStatus, t.MBMRegUser, o.MBMGroupCompany
            FROM MBM m
            LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
            LEFT JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber
            WHERE m.MBMNumber LIKE @like
            ORDER BY m.MBMNumber
            """;
        await using var conn = await Db.OpenAsync(GenesisDb.Main, ct);
        var cmd = new CommandDefinition(sql, new { take = MemberNumber.SearchLimit + 1, like = prefix.Trim() + "%" },
            commandTimeout: Timeout, flags: CommandFlags.NoCache, cancellationToken: ct);
        var items = new List<MemberSearchResult>();
        var truncated = false;
        await using var reader = await conn.ExecuteReaderAsync(cmd);
        while (await reader.ReadAsync(ct))
        {
            if (items.Count >= MemberNumber.SearchLimit) { truncated = true; break; }
            items.Add(new MemberSearchResult(
                Str(reader, "MBMNumber")!, Str(reader, "MBMCOVID"), Str(reader, "MBMName") ?? "", Str(reader, "MBMIcBcPp"),
                Str(reader, "MBMPolicyNo"), Str(reader, "MBMStatus"), Date(reader, "MBMPayorEffDate"), Date(reader, "MBMPayorExpDate"),
                Str(reader, "MBMGroupCompany"), Date(reader, "MBMBordxDate"), Date(reader, "MBMValueDate"), Str(reader, "MBMRegUser")));
        }
        return new MemberSearchPage(items, truncated);
    }

    public async Task<MemberDetail?> GetAsync(string number, CancellationToken ct = default)
    {
        await using var conn = await Db.OpenAsync(GenesisDb.Main, ct);
        var row = (await conn.QueryAsync(new CommandDefinition("SearchMBMCovPersonsProc", new { MBMNumber = number.Trim() },
            commandType: CommandType.StoredProcedure, commandTimeout: Timeout, cancellationToken: ct))).FirstOrDefault();
        if (row is null) return null;
        var r = (IDictionary<string, object?>)row;

        var status = S(r, "MBMStatus");
        MemberRefund? refund = null;
        if (status == "C")
        {
            var rf = (await conn.QueryAsync(new CommandDefinition(
                "SELECT TOP (1) MBMPremReduction, MBMMCORefund, MBMProviderRef, MBMProviderEff, MBMMCOEffective, MBMPremEffective FROM MBMRefund WHERE MBMNumber = @number ORDER BY MIndex DESC",
                new { number = number.Trim() }, commandTimeout: Timeout, cancellationToken: ct))).FirstOrDefault();
            if (rf is not null)
            {
                var d = (IDictionary<string, object?>)rf;
                refund = new MemberRefund(Dec(d, "MBMPremReduction"), Dec(d, "MBMMCORefund"), Dec(d, "MBMProviderRef"),
                    Dec(d, "MBMProviderEff"), Dec(d, "MBMMCOEffective"), Dec(d, "MBMPremEffective"));
            }
        }

        var health = S(r, "HLTCode");
        var annLmt = S(r, "AnnLmtIND");
        var annualLimitApplies = health == "S" || annLmt != "N";

        return new MemberDetail(
            S(r, "MBMNumber") ?? number, S(r, "MBMName") ?? "", S(r, "MBMIcBcPp"), S(r, "MBMPolicyNo"), status, S(r, "MBMDataStatus"),
            S(r, "MBMExpiredStatus"), S(r, "INSCode"), S(r, "PLNCode"), health, S(r, "PAYCode"),
            S(r, "MBMMemberType"), S(r, "MBMRenewFlag"), S(r, "MBMTakeover"), S(r, "MBMSex"), S(r, "RACCode"), S(r, "AGECode"),
            S(r, "MBMAdultChild"), S(r, "BRNCode"), S(r, "MBMPolicyYear"), S(r, "MBMBatchNo"), D(r, "MBMDOB"),
            D(r, "MBMValueDate"), D(r, "MBMBordxDate"), D(r, "MBMPayorEffDate"), D(r, "MBMPayorExpDate"),
            D(r, "MBMFinalExpiryDate"), D(r, "MBMEndorsementDate"), S(r, "MBMEndorseType"), D(r, "MBMPAYDateSubmission"),
            Dec(r, "MBMAvailableLimit"), annualLimitApplies, S(r, "PLNSpecialNote"), S(r, "PLNMajorMedInd") is { } mm ? mm == "Y" : null,
            S(r, "MBMGroupCompany"), S(r, "MBMAddress1"), S(r, "MBMAddress2"), S(r, "MBMAddress3"), S(r, "MBMAddress4"),
            S(r, "CTYCode"), S(r, "MBMPostCode"), S(r, "StaCode"), D(r, "MBMReinstate") is not null, S(r, "MBMTopupInd") == "Y", refund);
    }

    public async Task<IReadOnlyList<MemberAmendment>> GetAmendmentsAsync(string number, CancellationToken ct = default)
    {
        const string sql = """
            SELECT MBMAmendID, MBMAmendEdtType, MBMAmendDate, MBMAEndtEffDate, MBMAEndtBordxDate, MBMAEndtBatchNo
            FROM MBMAmendHistory WHERE MBMNumber = @number ORDER BY MBMAmendID
            """;
        var rows = await QueryAsync(GenesisDb.Main, sql, new { number = number.Trim() }, ct);
        return rows.Select(r => new MemberAmendment(
            Convert.ToInt32(Get(r, "MBMAmendID")), S(r, "MBMAmendEdtType"), D(r, "MBMAmendDate"), D(r, "MBMAEndtEffDate"),
            D(r, "MBMAEndtBordxDate"), Get(r, "MBMAEndtBatchNo") is { } b ? Convert.ToInt32(b) : null)).ToList();
    }

    public async Task<IReadOnlyList<MemberCase>> GetCasesAsync(string number, CancellationToken ct = default) =>
        await RunCaseProcAsync("SearchCas", new { MBMNumber = number.Trim() }, ct);

    public async Task<IReadOnlyList<MemberAccountSummary>> GetAccountSummaryAsync(string number, CancellationToken ct = default)
    {
        const string sql = """
            SELECT MBMPatientCovID, totalApprove, totalPayable2Hos, totalPaid2Hos, totalPayable2Mbm, totalPaidByMbm,
                   MBMPaidAmt, totalPaid2MBM, TotalExcess, TotalReimburse
            FROM view_MBMTotal_summary WHERE MBMNumber = @number
            """;
        var rows = await QueryAsync(GenesisDb.Main, sql, new { number = number.Trim() }, ct);
        return rows.Select(r => new MemberAccountSummary(
            S(r, "MBMPatientCovID"), Dec(r, "totalApprove") ?? 0, Dec(r, "totalPayable2Hos") ?? 0, Dec(r, "totalPaid2Hos") ?? 0,
            Dec(r, "totalPayable2Mbm") ?? 0, Dec(r, "totalPaidByMbm") ?? 0, Dec(r, "totalPaid2MBM") ?? 0,
            Dec(r, "TotalExcess") ?? 0, Dec(r, "TotalReimburse") ?? 0, Dec(r, "MBMPaidAmt") ?? 0)).ToList();
    }

    public async Task<IReadOnlyList<MemberAccountHistoryItem>> GetAccountHistoryAsync(string number, CancellationToken ct = default)
    {
        var ic = await ScalarIcAsync(number, ct);
        if (ic is null || ic.Length == 0) return [];
        const string sql = """
            SELECT CASNumber, ClaimVersion, DebitCreditDate, DebitCreditRefNo, CLMPayableByMedix2M, ReceiptNo, ReceiptDate,
                   ReceiptAmt, MBMNumber, CASDateAdmitted, CaseRegDate, MBMPayorEffDate
            FROM VIEW_Account_History WHERE MBMIcBcPp = @ic
            """;
        var rows = await QueryAsync(GenesisDb.Worksheet, sql, new { ic }, ct);
        return rows.Select(r =>
        {
            var payable = Dec(r, "CLMPayableByMedix2M") ?? 0;
            return new MemberAccountHistoryItem(
                S(r, "MBMNumber") ?? "", D(r, "MBMPayorEffDate"), D(r, "CASDateAdmitted"), D(r, "CaseRegDate"),
                $"{S(r, "CASNumber")}-{Get(r, "ClaimVersion")}", payable != 0 ? S(r, "DebitCreditRefNo") : null,
                payable != 0 ? D(r, "DebitCreditDate") : null, payable, S(r, "ReceiptNo"), D(r, "ReceiptDate"), Dec(r, "ReceiptAmt") ?? 0);
        }).ToList();
    }

    public Task<IReadOnlyList<MemberNote>> GetExclusionsAsync(string number, CancellationToken ct = default) =>
        GetNotesAsync("View_Get_MemberExclusion", "MBMExclusion", number, ct);

    public Task<IReadOnlyList<MemberNote>> GetRemarksAsync(string number, CancellationToken ct = default) =>
        GetNotesAsync("View_Get_MemberRemarks", "MBMRemarks", number, ct);

    private async Task<IReadOnlyList<MemberNote>> GetNotesAsync(string view, string column, string number, CancellationToken ct)
    {
        var member = await GetAsync(number, ct);
        if (member?.DateOfBirth is null) return [];
        var sql = $"SELECT MBMNumber, {column} AS Txt FROM {view} WHERE PAYCode = @pay AND MBMDOB = @dob AND INSCode = @ins AND MBMName = @name";
        var rows = await QueryAsync(GenesisDb.Main, sql, new { pay = member.PayorCode, dob = member.DateOfBirth, ins = member.InsurerCode, name = member.Name }, ct);
        return rows.Where(r => !string.IsNullOrWhiteSpace(S(r, "Txt"))).Select(r => new MemberNote(S(r, "MBMNumber") ?? "", S(r, "Txt")!)).ToList();
    }

    private async Task<string?> ScalarIcAsync(string number, CancellationToken ct)
    {
        await using var conn = await Db.OpenAsync(GenesisDb.Main, ct);
        var ic = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MBMIcBcPp FROM MBM WHERE MBMNumber = @number", new { number = number.Trim() }, commandTimeout: Timeout, cancellationToken: ct));
        return ic?.Trim();
    }

    private async Task<IReadOnlyList<MemberCase>> RunCaseProcAsync(string proc, object args, CancellationToken ct)
    {
        await using var conn = await Db.OpenAsync(GenesisDb.Main, ct);
        var rows = await conn.QueryAsync(new CommandDefinition(proc, args, commandType: CommandType.StoredProcedure,
            commandTimeout: Timeout, cancellationToken: ct));
        return rows.Select(x =>
        {
            var r = (IDictionary<string, object?>)x;
            var principal = S(r, "MBMPatientCovID") == "00";
            return new MemberCase(
                S(r, "CASNumber") ?? S(r, "CasNumber") ?? "", S(r, "MBMNumber") ?? "",
                principal ? S(r, "MBMName") : S(r, "MBMCName") ?? S(r, "MBMName"), principal ? "P" : S(r, "MBMCRELCode"),
                D(r, "CASDateAdmitted"), D(r, "CASDischargeDate"), S(r, "CASClaimability"), S(r, "DIAFinalDiag1"),
                S(r, "CASStatus"), S(r, "HOSName"), S(r, "PAYCode"));
        }).ToList();
    }

    private static string? Str(IDataRecord r, string name)
    {
        var i = Ordinal(r, name);
        return i < 0 || r.IsDBNull(i) ? null : Trim(Convert.ToString(r.GetValue(i)));
    }

    private static DateTime? Date(IDataRecord r, string name)
    {
        var i = Ordinal(r, name);
        return i < 0 || r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i));
    }

    private static int Ordinal(IDataRecord r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static object? Get(IDictionary<string, object?> r, string key) =>
        r.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? S(IDictionary<string, object?> r, string key) => Trim(Convert.ToString(Get(r, key)));
    private static DateTime? D(IDictionary<string, object?> r, string key) => Get(r, key) is DateTime d ? d : null;
    private static decimal? Dec(IDictionary<string, object?> r, string key) => Get(r, key) is null or DBNull ? null : Convert.ToDecimal(Get(r, key));
}



