using GenesisFIS.Core.Fees;

namespace GenesisFIS.Core.Membership;

/// <summary>Cancel one supplementary (covered) person: endorsement type K. The principal stays as he is; only that cover is cancelled.</summary>
public sealed record SupplementaryCancelRequest(string? MemberNumber, string? CoverId, DateOnly? EffectiveDate, DateOnly? EndorsementBordxDate, int? BatchNo);

public sealed record SupplementaryCancelQuote(
    string MemberNumber, string CoverId, string? Name, string? Status, DateOnly EffectiveDate, DateOnly ExpiryDate, DateOnly CancelDate, decimal Premium, decimal McoFee,
    string SuppMcoStatus, bool RefundWritten, RefundAmounts Refund);

public sealed record SupplementaryCancelResult(SupplementaryCancelQuote Quote);

public static class SupplementaryCancelRules
{
    public static (string Field, string Message)? Validate(SupplementaryCancelRequest r, bool needEndorsement)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        if (!CardSendingRules.IsValidCoverId(r.CoverId)) return ("coverId", "Please enter a valid cover id (2 digits).");
        var min = new DateOnly(1900, 1, 1);
        if (r.EffectiveDate is null || r.EffectiveDate < min) return ("effectiveDate", "Please enter the cancellation date.");
        if (!needEndorsement) return null;
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

public interface ISupplementaryCancelRepository
{
    Task<SupplementaryCancelQuote> QuoteAsync(SupplementaryCancelRequest request, CancellationToken ct = default);

    /// <summary>Marks the cover cancelled (status C, cancel date), records the K endorsement and history, and writes the refund row when the plan's supplementary MCO status requires it.</summary>
    Task<SupplementaryCancelResult> CancelAsync(SupplementaryCancelRequest request, string user, CancellationToken ct = default);
}
