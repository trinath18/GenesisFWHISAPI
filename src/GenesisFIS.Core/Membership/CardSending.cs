using System.Text.RegularExpressions;

namespace GenesisFIS.Core.Membership;

public static class CardSendingModes
{
    public static readonly IReadOnlyList<string> All = ["By Hand", "By Post"];
}

/// <summary>Search filters of Membership > Card Sending Mode. Dates are inclusive; member numbers and batch numbers are ranges.</summary>
public sealed record CardSendingCriteria(
    string? Payor, string? MemberFrom, string? MemberTo, DateOnly? BordxFrom, DateOnly? BordxTo, int? BatchFrom, int? BatchTo,
    DateOnly? SentFrom, DateOnly? SentTo, string? Mode);

public sealed record CardSendingRow(string MemberNumber, string CoverId, string? Name, DateOnly? BordxDate, int? BatchNo, string? SendingMode, DateOnly? CardDate);

public sealed record CardSendingPage(IReadOnlyList<CardSendingRow> Rows, int Principals, int Supplementary, bool Truncated);

public sealed record CardSendingTarget(string? MemberNumber, string? CoverId);

public sealed record CardSendingUpdate(string? Mode, DateOnly? Date, IReadOnlyList<CardSendingTarget>? Members);

public sealed record CardSendingResult(int Updated);

public static partial class CardSendingRules
{
    public const int MaxPrincipals = 500, MaxTargets = 5000;

    [GeneratedRegex(@"^[A-Za-z0-9*\-]{1,30}$")]
    private static partial Regex MemberPattern();

    [GeneratedRegex(@"^\d{2}$")]
    private static partial Regex CoverPattern();

    public static bool IsValidMemberNumber(string? v) => v is not null && MemberPattern().IsMatch(v);
    public static bool IsValidCoverId(string? v) => v is not null && CoverPattern().IsMatch(v);

    /// <summary>Returns the first problem with a search, or null.</summary>
    public static (string Field, string Message)? ValidateSearch(CardSendingCriteria c)
    {
        if (c.Payor is { Length: > 0 } && c.Payor.Trim().Length != 2) return ("payor", "The payor code must be 2 characters.");
        if (c.MemberFrom is { Length: > 0 } && !IsValidMemberNumber(c.MemberFrom.Trim())) return ("memberFrom", "Invalid member number.");
        if (c.MemberTo is { Length: > 0 } && !IsValidMemberNumber(c.MemberTo.Trim())) return ("memberTo", "Invalid member number.");
        if (c.BordxFrom > c.BordxTo) return ("bordxTo", "The bordx end date cannot be before the start date.");
        if (c.SentFrom > c.SentTo) return ("sentTo", "The card sent end date cannot be before the start date.");
        if (c.BatchFrom > c.BatchTo) return ("batchTo", "The batch end cannot be before the start.");
        if (c.Mode is { Length: > 0 } && !CardSendingModes.All.Contains(c.Mode)) return ("mode", "Unknown card sending mode.");
        return null;
    }

    public static (string Field, string Message)? ValidateUpdate(CardSendingUpdate u)
    {
        if (string.IsNullOrWhiteSpace(u.Mode) || !CardSendingModes.All.Contains(u.Mode)) return ("mode", "Please enter the card sending mode.");
        if (u.Date is null) return ("date", "Please enter the card sent date.");
        if (u.Date < new DateOnly(1900, 1, 1) || u.Date > new DateOnly(2100, 12, 31)) return ("date", "The card sent date is out of range.");
        if (u.Members is null || u.Members.Count == 0) return ("members", "Please select at least one member.");
        if (u.Members.Count > MaxTargets) return ("members", $"At most {MaxTargets} members can be updated at once.");
        foreach (var m in u.Members)
        {
            if (!IsValidMemberNumber(m.MemberNumber)) return ("members", $"'{m.MemberNumber}' is not a valid member number.");
            if (!IsValidCoverId(m.CoverId)) return ("members", $"'{m.CoverId}' is not a valid cover id.");
        }
        return null;
    }
}

/// <summary>Legacy form: Membership > Card Sending Mode (frmMembershipCardControl). Tables MBMTwo / MBMCoveredPersonsTwo, view VIEW_CardSent_Principal.</summary>
public interface ICardSendingRepository
{
    Task<CardSendingPage> SearchAsync(CardSendingCriteria criteria, CancellationToken ct = default);

    /// <summary>Sets mode and date on every listed principal (cover id 00) or covered person in one transaction. Any unknown member rolls everything back.</summary>
    Task<CardSendingResult> UpdateAsync(CardSendingUpdate update, string user, CancellationToken ct = default);
}
