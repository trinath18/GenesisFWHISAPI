using System.Text.RegularExpressions;
using GenesisFIS.Core.Fees;

namespace GenesisFIS.Core.Membership;

public sealed record RegistrationPayor(string Code, string? Name, string? Health);

public sealed record RegistrationOptions(IReadOnlyList<HealthPlan> HealthPlans, IReadOnlyList<RegistrationPayor> Payors);

/// <summary>New membership (principal member) registration. Header fields come from the legacy first screen (frmMemberEntry), the rest from frmNewMBMReg05.</summary>
public sealed record RegistrationRequest(
    string? HealthCode, string? PayorCode, DateOnly? BordxDate, int? BatchNo,
    string? Renewal, string? PreviousMemberNo, string? PreviousPolicyNo, string? Takeover,
    string? Name, string? Salutation, string? IcBcPp, DateOnly? DateOfBirth, string? Sex, string? Race, string? Nationality,
    string? Address1, string? Address2, string? Address3, string? City, string? State, string? PostCode, string? Email, string? TelHome, string? TelMobile, string? TelOffice,
    string? Insurer, string? Plan, string? PolicyNo, DateOnly? PayorEffectiveDate, DateOnly? PayorExpiryDate,
    string? GroupCompany, string? EmployeeNo, string? AgentCode, string? Department, string? Branch, string? InstallmentMode,
    string? Occupation, string? WorkNature, string? MaritalStatus, string? Allergies, string? Exclusion, string? Remarks,
    IReadOnlyList<RegistrationSupplementary>? Supplementary = null);

/// <summary>A supplementary (covered) person entered on the Supplementary tab. Cover ids 01, 02, ... follow the list order.</summary>
public sealed record RegistrationSupplementary(
    string? Name, string? Salutation, string? IcBcPp, DateOnly? DateOfBirth, string? Sex, string? Relationship, string? Allergies, string? Exclusion,
    string? SupplementaryPlan = null, DateOnly? EffectiveDate = null, DateOnly? ExpiryDate = null, decimal? RenewalDiscount = null, decimal? PolicyDiscount = null);

public sealed record RegistrationSupplementaryQuote(
    string CoverId, string Name, string AgeText, string AgeCode, string AdultChild, string Plan, DateOnly EffectiveDate, DateOnly ExpiryDate,
    decimal AnnualLimit, decimal BasicPremium, decimal Premium, decimal BasicMco, decimal Mco);

public sealed record RegistrationQuote(
    string AgeText, string AgeCode, string AdultChild, decimal BasicPremium, decimal Premium, decimal BasicMco, decimal McoFee, decimal AnnualLimit,
    string MemberType, string? DuplicateMemberNo, DateOnly PayorEffectiveDate, DateOnly PayorExpiryDate, IReadOnlyList<RegistrationSupplementaryQuote>? Supplementary = null);

public sealed record RegistrationResult(string MemberNumber, RegistrationQuote Quote, bool CrossReferenceSaved);

public static partial class RegistrationRules
{
    public static readonly IReadOnlyList<string> Renewals = ["N", "R", "T", "P"];
    public static readonly IReadOnlyList<string> Takeovers = ["N", "Y", "C"];

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,2}$")]
    private static partial Regex Short();

    public static (string Field, string Message)? Validate(RegistrationRequest r, bool full)
    {
        string T(string? s) => s?.Trim() ?? "";
        bool Bad(string? s, int max) => T(s).Length > max || (s?.Any(c => char.IsControl(c)) ?? false);
        if (T(r.HealthCode).Length is 0 or > 2) return ("healthCode", "Please select the health plan.");
        if (T(r.PayorCode).Length != 2) return ("payorCode", "Please select the payor.");
        if (r.BordxDate is null || r.BordxDate < new DateOnly(1900, 1, 1)) return ("bordxDate", "Please enter a valid bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        if (!full) return null;
        if (!Renewals.Contains(T(r.Renewal))) return ("renewal", "Renewal must be N, R, T or P.");
        if (T(r.Renewal) == "R" && T(r.PreviousMemberNo).Length == 0 && T(r.PreviousPolicyNo).Length == 0) return ("previousMemberNo", "Please enter the previous membership number or previous policy number.");
        if (!Takeovers.Contains(T(r.Takeover))) return ("takeover", "Take over must be N, Y or C.");
        if (T(r.Name).Length == 0) return ("name", "Please enter the membership name.");
        if (Bad(r.Name, 60)) return ("name", "The name can be at most 60 characters.");
        if (T(r.IcBcPp).Length == 0) return ("icBcPp", "Please enter the IC / BC / passport number.");
        if (Bad(r.IcBcPp, 30)) return ("icBcPp", "The IC / BC / passport number can be at most 30 characters.");
        if (r.Sex is not ("M" or "F")) return ("sex", "Please select the sex (M or F).");
        if (r.DateOfBirth is null || r.DateOfBirth < new DateOnly(1900, 1, 1) || r.DateOfBirth > DateOnly.FromDateTime(DateTime.Today)) return ("dateOfBirth", "Please enter a valid date of birth.");
        if (T(r.Address1).Length == 0) return ("address1", "Please enter the address.");
        if (T(r.PostCode).Length == 0) return ("postCode", "Please enter the post code.");
        if (T(r.State).Length == 0) return ("state", "Please select the state.");
        foreach (var (key, value, max) in new (string, string?, int)[] { ("address1", r.Address1, 50), ("address2", r.Address2, 50), ("address3", r.Address3, 50), ("city", r.City, 50), ("state", r.State, 50),
            ("postCode", r.PostCode, 6), ("email", r.Email, 50), ("telHome", r.TelHome, 12), ("telMobile", r.TelMobile, 15), ("telOffice", r.TelOffice, 15), ("salutation", r.Salutation, 5),
            ("policyNo", r.PolicyNo, 25), ("groupCompany", r.GroupCompany, 300), ("employeeNo", r.EmployeeNo, 30), ("agentCode", r.AgentCode, 30), ("department", r.Department, 100), ("branch", r.Branch, 100),
            ("allergies", r.Allergies, 1000), ("exclusion", r.Exclusion, 8000), ("remarks", r.Remarks, 4000), ("previousMemberNo", r.PreviousMemberNo, 30), ("previousPolicyNo", r.PreviousPolicyNo, 50) })
            if (Bad(value, max)) return (key, $"This field can be at most {max} characters.");
        if (T(r.Race).Length > 2) return ("race", "The race code can be at most 2 characters.");
        if (T(r.Nationality).Length > 2) return ("nationality", "The nationality code can be at most 2 characters.");
        if (T(r.Plan).Length == 0) return ("plan", "Please enter the plan code.");
        if (T(r.Plan).Length > 4 || T(r.Plan).Any(c => !char.IsLetterOrDigit(c))) return ("plan", "The plan code is invalid.");
        if (T(r.Insurer).Length != 1) return ("insurer", "Please enter the insured code (1 character).");
        if (T(r.PolicyNo).Length == 0) return ("policyNo", "Please enter the policy number.");
        if (r.PayorEffectiveDate is null) return ("payorEffectiveDate", "Please enter the payor effective date.");
        if (r.PayorExpiryDate is null) return ("payorExpiryDate", "Please enter the payor expiry date.");
        if (r.PayorExpiryDate < r.PayorEffectiveDate) return ("payorExpiryDate", "The payor expiry date cannot be before the effective date.");
        if (T(r.InstallmentMode) is { Length: > 0 } m && m is not ("A" or "Q" or "S" or "M")) return ("installmentMode", "Installment mode must be A, Q, S or M.");
        if (r.Supplementary is { Count: > 20 }) return ("supplementary", "At most 20 supplementary persons can be registered at once.");
        foreach (var s in r.Supplementary ?? [])
        {
            if (T(s.Name).Length == 0) return ("supplementary", "Please enter the covered person name.");
            if (Bad(s.Name, 60)) return ("supplementary", "A covered person name can be at most 60 characters.");
            if (T(s.IcBcPp).Length == 0) return ("supplementary", $"Please enter the identification of {T(s.Name)}.");
            if (Bad(s.IcBcPp, 30)) return ("supplementary", "A covered person identification can be at most 30 characters.");
            if (T(s.Relationship).Length != 1) return ("supplementary", $"Please enter the relationship code (1 character) of {T(s.Name)}.");
            if (s.Sex is not ("M" or "F")) return ("supplementary", $"Please select the sex of {T(s.Name)}.");
            if (s.DateOfBirth is null || s.DateOfBirth < new DateOnly(1900, 1, 1) || s.DateOfBirth > DateOnly.FromDateTime(DateTime.Today)) return ("supplementary", $"Please enter a valid birth date for {T(s.Name)}.");
            if (Bad(s.Salutation, 5) || Bad(s.Allergies, 500) || Bad(s.Exclusion, 4000)) return ("supplementary", $"A field of {T(s.Name)} is too long.");
            if (T(s.SupplementaryPlan).Length > 4) return ("supplementary", "The supplementary plan code can be at most 4 characters.");
            if (s.RenewalDiscount is < 0 or > 100 || s.PolicyDiscount is < 0 or > 100) return ("supplementary", "Discounts must be between 0 and 100.");
        }
        return null;
    }
}

public interface IRegistrationRepository
{
    Task<RegistrationOptions> OptionsAsync(CancellationToken ct = default);

    /// <summary>The last batch number used for that payor and bordx date (0 when none), as the legacy form showed it.</summary>
    Task<int> LastBatchAsync(string payor, DateOnly bordxDate, CancellationToken ct = default);

    Task<RegistrationQuote> QuoteAsync(RegistrationRequest request, CancellationToken ct = default);

    /// <summary>Generates the membership number, writes the member to MBM, MBMTwo, MBMOthers, MBMOthersTwo, BAT and the maintenance cross reference.</summary>
    Task<RegistrationResult> RegisterAsync(RegistrationRequest request, string user, CancellationToken ct = default);
}

