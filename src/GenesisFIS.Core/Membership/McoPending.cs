using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Membership;

/// <summary>MCO pending status: P = pending (held back from billing), R = released, B = billed (final, cannot be changed). Empty = not on the pending list.</summary>
public static class McoPendingStatus
{
    public const string Pending = "P", Released = "R", Billed = "B";
    public static bool IsSearchStatus(string? s) => s is Pending or Released or Billed;
    public static bool IsChangeStatus(string? s) => s is Pending or Released;
}

public sealed record McoPendingCriteria(string? MemberFrom, string? MemberTo, DateOnly? BordxFrom, DateOnly? BordxTo, string? Status);

public sealed record McoPendingRow(string MemberNumber, DateOnly? BordxDate, DateOnly? NewBordxDate, string? Status);

public sealed record McoPendingPage(IReadOnlyList<McoPendingRow> Rows, bool Truncated);

public sealed record McoPendingChange(string? Status, IReadOnlyList<string>? Members);

public sealed record McoPendingResult(int Added, int Released, int Unchanged);

public static partial class McoPendingRules
{
    public const int MaxRows = 500, MaxMembers = 2000;

    [GeneratedRegex(@"^[A-Za-z0-9*\-]{1,30}$")]
    private static partial Regex MemberPattern();

    public static bool IsValidMember(string? v) => v is not null && MemberPattern().IsMatch(v);

    public static (string Field, string Message)? ValidateSearch(McoPendingCriteria c)
    {
        if (c.MemberFrom is { Length: > 0 } && !IsValidMember(c.MemberFrom.Trim())) return ("memberFrom", "Invalid member number.");
        if (c.MemberTo is { Length: > 0 } && !IsValidMember(c.MemberTo.Trim())) return ("memberTo", "Invalid member number.");
        if (c.BordxFrom > c.BordxTo) return ("bordxTo", "The bordx end date cannot be before the start date.");
        if (c.Status is { Length: > 0 } && !McoPendingStatus.IsSearchStatus(c.Status)) return ("status", "Unknown status.");
        return null;
    }

    public static (string Field, string Message)? ValidateChange(McoPendingChange c)
    {
        if (!McoPendingStatus.IsChangeStatus(c.Status)) return ("status", "Please select a status (Pending or Release).");
        if (c.Members is null || c.Members.Count == 0) return ("members", "Please select at least one member.");
        if (c.Members.Count > MaxMembers) return ("members", $"At most {MaxMembers} members can be changed at once.");
        if (c.Members.FirstOrDefault(m => !IsValidMember(m?.Trim())) is { } bad) return ("members", $"'{bad}' is not a valid member number.");
        return null;
    }
}

/// <summary>
/// Legacy form: Membership > MCO Pending (frmMBMMCOPending). Table MCOPending and the member bordx dates (MBMCrossReference) are both in the FWHS maintenance database.
/// The legacy view VIEW_MCOPending also reads another database (HISMaintenance); the new code deliberately reads only FWHS data.
/// </summary>
public interface IMcoPendingRepository
{
    Task<McoPendingPage> SearchAsync(McoPendingCriteria criteria, CancellationToken ct = default);

    /// <summary>
    /// Pending: adds the member to the pending list (already pending = unchanged). Release: removes a pending member (not on the list = unchanged).
    /// Billed members cannot be changed: the whole request is refused. One transaction.
    /// </summary>
    Task<McoPendingResult> ChangeAsync(McoPendingChange change, string user, CancellationToken ct = default);
}
