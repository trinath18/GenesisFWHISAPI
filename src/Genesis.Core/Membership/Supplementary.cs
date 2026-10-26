using Genesis.Core.Fees;

namespace Genesis.Core.Membership;

/// <summary>Add a supplementary (covered) person to a principal member: endorsement type S. Fees and limits come from the plan tables exactly as the legacy form calculated them.</summary>
public sealed record SupplementaryRequest(
    string? MemberNumber, string? Name, string? Salutation, string? IcBcPp, DateOnly? DateOfBirth, string? Sex, string? Relationship, string? Allergies, string? Exclusion,
    string? SupplementaryPlan, DateOnly? EffectiveDate, DateOnly? EndorsementBordxDate, int? BatchNo, decimal? RenewalDiscount = null, decimal? PolicyDiscount = null);

public sealed record SupplementaryQuote(
    string MemberNumber, string CoverId, string Name, string AgeText, string AgeCode, string AdultChild, string Plan, DateOnly EffectiveDate, DateOnly ExpiryDate,
    decimal AnnualLimit, decimal BasicPremium, decimal Premium, decimal BasicMco, decimal Mco, string SuppLimitStatus, string SuppMcoStatus, string SuppPremiumStatus);

public sealed record SupplementaryResult(SupplementaryQuote Quote);

public static class SupplementaryRules
{
    public static (string Field, string Message)? Validate(SupplementaryRequest r, bool needEndorsement)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        static bool Bad(string? v, int max) => v is { } s && (s.Trim().Length > max || s.Any(c => char.IsControl(c) && c is not ('\r' or '\n')));
        if (string.IsNullOrWhiteSpace(r.Name)) return ("name", "Please enter the covered person name.");
        if (Bad(r.Name, 60)) return ("name", "The name can be at most 60 characters.");
        if (string.IsNullOrWhiteSpace(r.IcBcPp)) return ("icBcPp", "Please enter the covered person identification.");
        if (Bad(r.IcBcPp, 30)) return ("icBcPp", "The identification can be at most 30 characters.");
        if (string.IsNullOrWhiteSpace(r.Relationship)) return ("relationship", "Please enter the relationship.");
        if (r.Relationship.Trim().Length != 1) return ("relationship", "The relationship code must be 1 character.");
        if (Bad(r.Salutation, 5)) return ("salutation", "The salutation can be at most 5 characters.");
        if (Bad(r.Allergies, 500)) return ("allergies", "Allergies can be at most 500 characters.");
        if (Bad(r.Exclusion, 4000)) return ("exclusion", "The exclusion can be at most 4000 characters.");
        if (r.SupplementaryPlan is { Length: > 0 } sp && (sp.Trim().Length > 4 || sp.Any(c => !char.IsLetterOrDigit(c)))) return ("supplementaryPlan", "The supplementary plan code is invalid (4 characters at most).");
        if (r.Sex is not ("M" or "F")) return ("sex", "Please select the sex (M or F).");
        var min = new DateOnly(1900, 1, 1);
        if (r.DateOfBirth is null || r.DateOfBirth < min || r.DateOfBirth > DateOnly.FromDateTime(DateTime.Today)) return ("dateOfBirth", "Please enter a valid date of birth.");
        if (r.RenewalDiscount is < 0 or > 100) return ("renewalDiscount", "The renewal discount must be between 0 and 100.");
        if (r.PolicyDiscount is < 0 or > 100) return ("policyDiscount", "The policy discount must be between 0 and 100.");
        if (!needEndorsement) return null;
        if (r.EffectiveDate is null || r.EffectiveDate < min) return ("effectiveDate", "Please enter the endorsement effective date.");
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

public interface ISupplementaryRepository
{
    Task<SupplementaryQuote> QuoteAsync(SupplementaryRequest request, CancellationToken ct = default);

    /// <summary>Adds the covered person (MBMCoveredPersons + MBMCoveredPersonsTwo), the S endorsement and history, in one transaction.</summary>
    Task<SupplementaryResult> AddAsync(SupplementaryRequest request, string user, CancellationToken ct = default);
}
