using GenesisFIS.Core.Membership;

namespace GenesisFIS.Infrastructure.Data;

/// <summary>Defensive column readers for Dapper dynamic rows (legacy views/procs differ slightly in column sets and casing).</summary>
internal static class RowReader
{
    public static object? Get(IDictionary<string, object?> r, string key) =>
        r.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    public static string? S(IDictionary<string, object?> r, string key) => Trim(Convert.ToString(Get(r, key)));
    public static DateTime? D(IDictionary<string, object?> r, string key) => Get(r, key) is DateTime d ? d : null;
    public static decimal? Dec(IDictionary<string, object?> r, string key) => Get(r, key) is null or DBNull ? null : Convert.ToDecimal(Get(r, key));
    public static int? I(IDictionary<string, object?> r, string key) => Get(r, key) is null or DBNull ? null : Convert.ToInt32(Get(r, key));

    public static MemberCase ToMemberCase(IDictionary<string, object?> r)
    {
        var principal = S(r, "MBMPatientCovID") == "00";
        return new MemberCase(
            S(r, "CASNumber") ?? "", S(r, "MBMNumber") ?? "",
            principal ? S(r, "MBMName") : S(r, "MBMCName") ?? S(r, "MBMName"), principal ? "P" : S(r, "MBMCRELCode"),
            D(r, "CASDateAdmitted"), D(r, "CASDischargeDate"), S(r, "CASClaimability"), S(r, "DIAFinalDiag1"),
            S(r, "CASStatus"), S(r, "HOSName"), S(r, "PAYCode"));
    }
}
