using System.Text.RegularExpressions;

namespace Genesis.Core.Membership;

/// <summary>A principal member as the adjustment screen edits it (tables MBM and MBMTwo).</summary>
public sealed record AdjustmentMember(
    string MemberNumber, string? Name, string? PolicyNo, string? IcBcPp, DateOnly? DateOfBirth, string? Sex, string? Address1, string? Address2, string? Address3,
    string? City, string? State, string? PostCode, string? Race, string? Nationality, string? Salutation, string? Email, string? TelHome, string? TelMobile, string? TelOffice,
    string? Status, string? Insurer, string? Plan, string? Payor, DateOnly? PayorEffectiveDate, DateOnly? PayorExpiryDate, int? BatchNo);

/// <summary>Amendment of personal details (endorsement type A). Every field is replaced by the value sent; empty optional fields are cleared.</summary>
public sealed record PrincipalAmendment(
    string? MemberNumber, string? Name, string? PolicyNo, string? IcBcPp, DateOnly? DateOfBirth, string? Sex, string? Address1, string? Address2, string? Address3,
    string? City, string? State, string? PostCode, string? Race, string? Nationality, string? Salutation, string? Email, string? TelHome, string? TelMobile, string? TelOffice,
    DateOnly? EndorsementBordxDate, DateOnly? EffectiveDate, int? BatchNo);

/// <summary>Everything the legacy Member Adjustment header shows next to the principal details, plus the management note (MBMTwo.MBMMmgtNotes).</summary>
public sealed record AdjustmentOverview(MemberDetail Detail, string? ManagementNote);

public sealed record MgmtNoteRequest(string? MemberNumber, string? Note);

public sealed record AmendmentResult(AdjustmentMember Member, bool CrossReferenceUpdated, bool RenewalKeyUpdated);

public static partial class AdjustmentRules
{
    [GeneratedRegex(@"^[A-Za-z0-9*\-+]{1,30}$")]
    private static partial Regex MemberPattern();

    public static bool IsValidMember(string? v) => v is not null && MemberPattern().IsMatch(v.Trim());

    /// <summary>Column sizes come from the real tables (checked by a schema test).</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int>
    {
        ["name"] = 60, ["policyNo"] = 25, ["icBcPp"] = 30, ["address1"] = 50, ["address2"] = 50, ["address3"] = 50, ["city"] = 50, ["state"] = 50, ["postCode"] = 6,
        ["race"] = 2, ["nationality"] = 10, ["salutation"] = 5, ["email"] = 50, ["telHome"] = 12, ["telMobile"] = 15, ["telOffice"] = 15,
    };

    public static (string Field, string Message)? Validate(PrincipalAmendment a)
    {
        if (!IsValidMember(a.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        var text = new (string Key, string? Value, string Label, bool Required)[]
        {
            ("name", a.Name, "the member name", true), ("policyNo", a.PolicyNo, "the policy number", false), ("icBcPp", a.IcBcPp, "the IC / BC / passport number", true),
            ("address1", a.Address1, "address line 1", false), ("address2", a.Address2, "address line 2", false), ("address3", a.Address3, "address line 3", false),
            ("city", a.City, "the city", false), ("state", a.State, "the state", false), ("postCode", a.PostCode, "the postcode", false), ("race", a.Race, "the race", false),
            ("nationality", a.Nationality, "the nationality", false), ("salutation", a.Salutation, "the salutation", false), ("email", a.Email, "the e-mail", false),
            ("telHome", a.TelHome, "the home telephone", false), ("telMobile", a.TelMobile, "the mobile telephone", false), ("telOffice", a.TelOffice, "the office telephone", false),
        };
        foreach (var (key, value, label, required) in text)
        {
            var v = value?.Trim() ?? "";
            if (v.Length == 0) { if (required) return (key, $"Please enter {label}."); continue; }
            if (v.Length > MaxLengths[key]) return (key, $"{char.ToUpper(label[0])}{label[1..]} can be at most {MaxLengths[key]} characters.");
            if (v.Any(char.IsControl)) return (key, $"{char.ToUpper(label[0])}{label[1..]} contains characters that are not allowed.");
        }
        if (a.Sex is not ("M" or "F")) return ("sex", "Please select the sex (M or F).");
        var min = new DateOnly(1900, 1, 1);
        if (a.DateOfBirth is null || a.DateOfBirth < min || a.DateOfBirth > DateOnly.FromDateTime(DateTime.Today)) return ("dateOfBirth", "Please enter a valid date of birth.");
        if (a.EndorsementBordxDate is null || a.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (a.EffectiveDate is null || a.EffectiveDate < min) return ("effectiveDate", "Please enter the endorsement effective date.");
        if (a.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

/// <summary>
/// Legacy form: Membership > Adjustment (frmNewMBMAdjustment), first slice: personal-detail amendment of a principal. Fee-changing endorsements
/// (plan change, cancellation, supplementary persons) come later on the shared fee module.
/// </summary>
public interface IAdjustmentRepository
{
    Task<AdjustmentMember?> GetAsync(string memberNumber, CancellationToken ct = default);
    Task<AmendmentResult> AmendAsync(PrincipalAmendment amendment, string user, CancellationToken ct = default);

    /// <summary>Legacy Pre / Next: the neighbouring membership number in sort order, or null at the first / last record.</summary>
    Task<string?> NeighbourAsync(string memberNumber, bool next, CancellationToken ct = default);

    Task<string?> GetMgmtNoteAsync(string memberNumber, CancellationToken ct = default);

    Task SaveMgmtNoteAsync(string memberNumber, string? note, string user, CancellationToken ct = default);
}

