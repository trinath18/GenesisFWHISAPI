using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Membership;

public sealed record ReinstatementCovered(string CoverId, string? Name, string? AdultChild, string? AgeBand, string? Sex);

/// <summary>The member as the reinstatement screen shows it. <see cref="CanReinstate"/> is true only for cancelled members (status C) with a lapsed date.</summary>
public sealed record ReinstatementMember(
    string MemberNumber, string? Name, string? PolicyNo, string? Status, DateOnly? EffectiveDate, DateOnly? ExpiryDate, DateOnly? FinalExpiryDate, DateOnly? LapsedDate,
    string? Insurer, string? Plan, string? Health, string? Payor, string? AgeCode, string? Sex, string? AdultChild, string? IcNo, IReadOnlyList<ReinstatementCovered> CoveredPersons)
{
    public bool CanReinstate => Status == "C" && LapsedDate is not null && ExpiryDate is not null;
    public bool HasSupplementary => Insurer is "F" or "H";
}

public sealed record ReinstatementRequest(string? MemberNumber, DateOnly? EndorsementBordxDate, DateOnly? ReinstateDate, int? BatchNo);

public sealed record ReinstatementLine(string CoverId, decimal Premium, decimal McoFee);

public sealed record ReinstatementQuote(ReinstatementMember Member, IReadOnlyList<ReinstatementLine> Lines, decimal TotalPremium, decimal TotalMcoFee);

public sealed record ReinstatementResult(int LinesWritten, decimal TotalPremium, decimal TotalMcoFee, bool CrossReferenceUpdated);

public static partial class ReinstatementRules
{
    [GeneratedRegex(@"^[A-Za-z0-9*\-+]{1,30}$")]
    private static partial Regex MemberPattern();

    public static bool IsValidMember(string? v) => v is not null && MemberPattern().IsMatch(v.Trim());

    public static (string Field, string Message)? Validate(ReinstatementRequest r)
    {
        if (!IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        if (r.EndorsementBordxDate is null) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.ReinstateDate is null) return ("reinstateDate", "Please enter the reinstatement date.");
        if (r.BatchNo is null or < 1 or > 99999) return ("batchNo", "Please enter a batch number between 1 and 99999.");
        var min = new DateOnly(1900, 1, 1);
        var max = new DateOnly(2100, 12, 31);
        if (r.EndorsementBordxDate < min || r.EndorsementBordxDate > max) return ("endorsementBordxDate", "The endorsement bordx date is out of range.");
        if (r.ReinstateDate < min || r.ReinstateDate > max) return ("reinstateDate", "The reinstatement date is out of range.");
        return null;
    }
}

/// <summary>
/// Legacy form: Membership > Member Reinstatement (frmMBMReintstatement). Premium and MCO fee come from the shared fee module (rate date and pro-rata start
/// = lapsed date, end = payor expiry, no discounts). Writes MBMReinstatement, MBM, MBMCoveredPersons and MBMAmendHistory in the main database and
/// MBMCrossReference in the maintenance database.
/// </summary>
public interface IReinstatementRepository
{
    Task<ReinstatementMember?> GetAsync(string memberNumber, CancellationToken ct = default);
    Task<ReinstatementQuote> QuoteAsync(ReinstatementRequest request, CancellationToken ct = default);
    Task<ReinstatementResult> ReinstateAsync(ReinstatementRequest request, string user, CancellationToken ct = default);
}
