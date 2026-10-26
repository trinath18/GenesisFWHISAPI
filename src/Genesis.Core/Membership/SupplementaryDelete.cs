namespace Genesis.Core.Membership;

/// <summary>Delete one supplementary (covered) person completely: endorsement type T. Destructive, so it is previewed first and refused when the cover has any history.</summary>
public sealed record SupplementaryDeleteRequest(string? MemberNumber, string? CoverId, DateOnly? EffectiveDate, DateOnly? EndorsementBordxDate, int? BatchNo);

public sealed record SupplementaryDeleteQuote(
    string MemberNumber, string CoverId, string? Name, string? Status, int Cases, int RefundRows, int LifetimeRows, int ClinicRows, int BenefitLimitRows)
{
    /// <summary>Why the cover cannot be deleted, or null when it can.</summary>
    public string? BlockedReason
    {
        get
        {
            var reasons = new List<string>();
            if (Cases > 0) reasons.Add($"{Cases} case(s)");
            if (RefundRows > 0) reasons.Add($"{RefundRows} refund row(s)");
            if (LifetimeRows > 0) reasons.Add($"{LifetimeRows} lifetime-limit row(s)");
            return reasons.Count == 0 ? null : "has " + string.Join(", ", reasons);
        }
    }
}

public sealed record SupplementaryDeleteResult(SupplementaryDeleteQuote Quote, int RowsDeleted);

public interface ISupplementaryDeleteRepository
{
    Task<SupplementaryDeleteQuote> QuoteAsync(SupplementaryDeleteRequest request, CancellationToken ct = default);

    /// <summary>Writes the full covered-person records to the audit log, then deletes them from MBMCoveredPersons, MBMCoveredPersonsTwo, MBMCoveredPersonsCLN and MBMBnfLmt with the T history, in one transaction.</summary>
    Task<SupplementaryDeleteResult> DeleteAsync(SupplementaryDeleteRequest request, string user, CancellationToken ct = default);
}
