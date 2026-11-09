namespace GenesisFIS.Core.Membership;

/// <summary>
/// Group company records (GroupCompanyFW.CoGRPPState, filled from the payor's bordereaux) hold Malaysia's official two-digit state codes, which
/// SearchMBMCovPersonsProc returns as the member's state. A member's own record (MBM.STACode) holds the state name, as picked from STA. This shows the
/// name for a code, in the STA spelling, so every screen shows "SELANGOR" rather than "10". No table in the databases holds this mapping.
/// </summary>
public static class MalaysianStates
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["01"] = "JOHOR", ["02"] = "KEDAH", ["03"] = "KELANTAN", ["04"] = "MELAKA", ["05"] = "NEGERI SEMBILAN", ["06"] = "PAHANG", ["07"] = "PENANG", ["08"] = "PERAK",
        ["09"] = "PERLIS", ["10"] = "SELANGOR", ["11"] = "TERENGGANU", ["12"] = "SABAH", ["13"] = "SARAWAK", ["14"] = "KUALA LUMPUR", ["15"] = "WILAYAH PERSEKUTUAN",
        ["16"] = "WILAYAH PERSEKUTUAN",
    };

    /// <summary>The state name for a two-digit code; "ZZNIL" (no state) is blank; anything else (already a name) is returned as it is.</summary>
    public static string? Name(string? state)
    {
        var s = state?.Trim();
        if (string.IsNullOrEmpty(s) || s.Equals("ZZNIL", StringComparison.OrdinalIgnoreCase)) return null;
        return Names.TryGetValue(s.Length == 1 ? "0" + s : s, out var name) ? name : state;
    }
}
