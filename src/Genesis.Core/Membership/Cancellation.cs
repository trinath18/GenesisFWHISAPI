using Genesis.Core.Fees;

namespace Genesis.Core.Membership;

/// <summary>Cancel a principal membership (endorsement type C) effective on <see cref="CancelDate"/>.</summary>
public sealed record CancellationRequest(string? MemberNumber, DateOnly? CancelDate, DateOnly? EndorsementBordxDate, int? BatchNo);

public sealed record CancellationQuote(string MemberNumber, string? Name, string? Status, DateOnly EffectiveDate, DateOnly ExpiryDate, DateOnly CancelDate, decimal Premium, decimal McoFee,
    RefundAmounts Refund, int CoveredPersons);

public sealed record CancellationResult(CancellationQuote Quote, bool CrossReferenceUpdated);

public static class CancellationRules
{
    public static (string Field, string Message)? Validate(CancellationRequest r, bool needEndorsement)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        var min = new DateOnly(1900, 1, 1);
        if (r.CancelDate is null || r.CancelDate < min) return ("cancelDate", "Please enter the cancellation date.");
        if (!needEndorsement) return null;
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

public interface ICancellationRepository
{
    /// <summary>Calculates the refund without changing anything.</summary>
    Task<CancellationQuote> QuoteAsync(CancellationRequest request, CancellationToken ct = default);

    /// <summary>Cancels the member and covered persons, writes history and the refund row, in one transaction (maintenance-database copy afterwards).</summary>
    Task<CancellationResult> CancelAsync(CancellationRequest request, string user, CancellationToken ct = default);
}
