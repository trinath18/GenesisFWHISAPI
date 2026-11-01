namespace GenesisFIS.Core.Membership;

/// <summary>Endorsements that only change the member's status: X = suspend membership (status S), D = delete policy (status D). No fees or refunds.</summary>
public static class StatusEndorsements
{
    public const string Suspend = "X", DeletePolicy = "D";

    public static string? NewStatus(string? type) => type switch { Suspend => "S", DeletePolicy => "D", _ => null };
    public static string Label(string type) => type == Suspend ? "suspension" : "policy deletion";
}

public sealed record StatusEndorsementRequest(string? MemberNumber, string? Type, DateOnly? EffectiveDate, DateOnly? EndorsementBordxDate, int? BatchNo);

public sealed record StatusEndorsementResult(string MemberNumber, string Type, string PreviousStatus, string NewStatus, int CoveredPersonsUpdated, bool CrossReferenceUpdated);

public static class StatusEndorsementRules
{
    public static (string Field, string Message)? Validate(StatusEndorsementRequest r)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        if (StatusEndorsements.NewStatus(r.Type) is null) return ("type", "Please choose suspend membership or delete policy.");
        var min = new DateOnly(1900, 1, 1);
        if (r.EffectiveDate is null || r.EffectiveDate < min) return ("effectiveDate", "Please enter the endorsement effective date.");
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }
}

public interface IStatusEndorsementRepository
{
    /// <summary>Sets the member (and every covered person) to the new status, records the endorsement and history; the maintenance-database copy follows.</summary>
    Task<StatusEndorsementResult> ApplyAsync(StatusEndorsementRequest request, string user, CancellationToken ct = default);
}
