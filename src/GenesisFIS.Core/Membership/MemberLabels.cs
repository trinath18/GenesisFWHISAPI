namespace GenesisFIS.Core.Membership;

/// <summary>Membership Tracking (Simple) > Print Label (legacy frmNewMBMTrackingSimple.PrintLabel): the member's name and mailing address.</summary>
public static class MemberLabels
{
    /// <summary>
    /// The lines of one address label: name, address 1-3, post code with city, state. Blank parts and the "NA" filler the FWHS uploads put in empty
    /// address fields are left out. Null when the member has no address at all (nothing to post to).
    /// </summary>
    public static IReadOnlyList<string>? Lines(string? name, string? address1, string? address2, string? address3, string? postCode, string? city, string? state)
    {
        static string? Part(string? s) => string.IsNullOrWhiteSpace(s) || s.Trim().Equals("NA", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
        var address = new[] { Part(address1), Part(address2), Part(address3) }.OfType<string>().ToList();
        var town = string.Join(' ', new[] { Part(postCode), Part(city) }.OfType<string>());
        if (town.Length > 0) address.Add(town);
        if (Part(state) is { } st) address.Add(st);
        if (address.Count == 0) return null;
        return [Part(name) ?? "", .. address];
    }
}
