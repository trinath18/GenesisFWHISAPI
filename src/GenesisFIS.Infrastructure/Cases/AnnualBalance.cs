using GenesisFIS.Core.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// A patient's remaining annual limit: the plan's annual (or supplementary) limit minus everything already approved on the member's cases, as the legacy
/// LG and worksheet screens worked it out. The legacy View_Claim_Get* views read the HISMaintenance database (a different dataset), so the member's
/// cases are read from CAS and their approved amounts summed in Claim. SuppLimitStatus decides which cases share the limit: A all of them,
/// B the principal alone or all covered persons together, C each patient alone.
/// </summary>
internal static class AnnualBalance
{
    public delegate Task<List<IDictionary<string, object?>>> Query(GenesisDb db, string sql, object args);

    /// <summary>The balance, and whether it is a covered person's own (supplementary) limit, kept in MBMCoveredPersons rather than MBM.</summary>
    public sealed record Result(decimal Balance, bool CoveredPersonLimit);

    /// <summary>Null when the plan has no annual limit row.</summary>
    public static async Task<decimal?> ForAsync(Query query, string member, string patient, string plan, string ins, string? memberType, DateTime effective) =>
        (await DetailAsync(query, member, patient, plan, ins, memberType, effective))?.Balance;

    public static async Task<Result?> DetailAsync(Query query, string member, string patient, string plan, string ins, string? memberType, DateTime effective)
    {
        var limits = (await query(GenesisDb.Main, "SELECT TOP 1 AnnLimit, SuppLimit, SuppLimitStatus FROM AnnualLimit WHERE PLNCode = @plan AND INSCode = @ins AND AnnualEffDate <= @effective ORDER BY AnnualEffDate DESC",
            new { plan, ins, effective })).FirstOrDefault();
        if (limits is null) return null;
        var status = S(limits, "SuppLimitStatus")?.Trim() ?? "";
        var cases = (await query(GenesisDb.Main, "SELECT CASNumber, MBMPatientCovID FROM CAS WHERE MBMNumber = @member", new { member }))
            .Select(x => (No: S(x, "CASNumber")?.Trim() ?? "", Patient: S(x, "MBMPatientCovID")?.Trim() ?? "")).Where(x => status switch
            {
                "A" => true,
                "B" => patient == "00" ? x.Patient == "00" : x.Patient != "00",
                _ => patient == "00" ? x.Patient == "00" : x.Patient == patient,
            }).Select(x => x.No).ToList();
        var approved = 0m;
        foreach (var chunk in cases.Chunk(1000))
            approved += (await query(GenesisDb.Worksheet, "SELECT SUM(CLMTotalApproved) AS Amt FROM Claim WHERE CASNumber IN @cases", new { cases = chunk })).Select(x => Dec(x, "Amt") ?? 0).FirstOrDefault();
        var useSupp = memberType?.Trim() == "Q" && status is "B" or "C" && patient != "00";
        // Legacy UpdateMBMBalance: a covered person under limit type B or C has a balance of their own.
        return new Result(((useSupp ? Dec(limits, "SuppLimit") : Dec(limits, "AnnLimit")) ?? 0m) - approved, patient != "00" && status is "B" or "C");
    }
}
