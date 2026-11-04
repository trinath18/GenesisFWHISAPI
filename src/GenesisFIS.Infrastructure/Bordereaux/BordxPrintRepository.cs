using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Data;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Bordereaux;

/// <summary>Print Bordx data: View_PrintBordx (legacy procedure Print_Bordx, case 1) for the claims and PAY (main database) for the payor.</summary>
internal sealed class BordxPrintRepository(IDbConnectionFactory connections) : SqlRepository(connections), IBordxPrintRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<IReadOnlyList<BordxPrint>> LoadAsync(IReadOnlyCollection<string> bordxNos, CancellationToken ct = default)
    {
        var keys = bordxNos.Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(BordxPrintRules.MaxBordereaux).ToArray();
        if (keys.Length == 0) return [];
        // The view repeats a claim per invoice pay-to; the printed columns are the claim's, so DISTINCT without INVPayTo gives one row per claim.
        var rows = await QueryAsync(GenesisDb.Worksheet, """
            SELECT DISTINCT BordxRefNo, BordxDate, PAYCode, MBMPolicyNo, PatientName, MBMIcBcPp, CASNumber, ClaimVersion, CASDateAdmitted, CASDischargeDate, HOSName,
                CASPAYType, CLMPayTo, CASAdmissionReason, CASMRNNo, ClaimID, CLMPayableByMedix2H, CLMMedical
            FROM View_PrintBordx WHERE BordxRefNo IN @keys
            """, new { keys }, ct);
        var payCodes = rows.Select(r => Clean(S(r, "PAYCode"))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var payors = payCodes.Length == 0 ? [] : (await QueryAsync(GenesisDb.Main, """
                SELECT PAYCode, PAYCompanyName, PAYAddress1, PAYAddress2, PAYAddress3, PAYAdmissionContact, PAYTELno, PAYFAXno FROM PAY WHERE PAYCode IN @payCodes
                """, new { payCodes }, ct))
            .GroupBy(r => S(r, "PAYCode")!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return rows.GroupBy(r => S(r, "BordxRefNo")!.Trim(), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var first = g.First();
            var code = Clean(S(first, "PAYCode")) ?? "";
            var p = payors.GetValueOrDefault(code);
            var payor = new BordxPrintPayor(code, p is null ? null : Clean(S(p, "PAYCompanyName")), p is null ? null : Clean(S(p, "PAYAddress1")), p is null ? null : Clean(S(p, "PAYAddress2")),
                p is null ? null : Clean(S(p, "PAYAddress3")), p is null ? null : Clean(S(p, "PAYAdmissionContact")), p is null ? null : Clean(S(p, "PAYTELno")), p is null ? null : Clean(S(p, "PAYFAXno")));
            var claims = g.GroupBy(r => (S(r, "CASNumber")!.Trim().ToUpperInvariant(), I(r, "ClaimVersion") ?? 0)).Select(c => c.First())
                .OrderBy(r => S(r, "CASNumber"), StringComparer.OrdinalIgnoreCase).ThenBy(r => I(r, "ClaimVersion"))
                .Select((r, n) =>
                {
                    var hospital = Clean(S(r, "HOSName"));
                    var (charges, medical, total) = BordxPrintRules.Amounts(Dec(r, "CLMPayableByMedix2H") ?? 0, Dec(r, "CLMMedical") ?? 0);
                    return new BordxPrintClaim(n + 1, Clean(S(r, "MBMPolicyNo")), Clean(S(r, "PatientName")), Clean(S(r, "MBMIcBcPp")), S(r, "CASNumber")!.Trim(), I(r, "ClaimVersion") ?? 0,
                        Day(r, "CASDateAdmitted"), Day(r, "CASDischargeDate"), hospital, BordxPrintRules.PayTo(S(r, "CASPAYType"), hospital, S(r, "CLMPayTo")),
                        Clean(S(r, "CASAdmissionReason")), Clean(S(r, "CASMRNNo")), Clean(S(r, "ClaimID")), charges, medical, total);
                }).ToList();
            return new BordxPrint(g.Key, Day(first, "BordxDate"), payor, claims);
        }).ToList();
    }
}
