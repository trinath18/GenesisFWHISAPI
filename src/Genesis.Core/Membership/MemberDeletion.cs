using System.Text.RegularExpressions;

namespace Genesis.Core.Membership;

/// <summary>How the members to delete are chosen (legacy "Delete Option"): D = bordx date range, B = bordx date + batch, M = member number range.</summary>
public static class DeletionOptions
{
    public const string BordxDate = "D", Batch = "B", Member = "M";
    public static bool IsValid(string? o) => o is BordxDate or Batch or Member;
}

public sealed record DeletionCriteria(
    string? Health, string? Payor, string? Option, DateOnly? BordxFrom, DateOnly? BordxTo, int? BatchNo, string? MemberFrom, string? MemberTo);

public sealed record DeletionCandidate(
    string MemberNumber, string? Name, DateOnly? BordxDate, int? BatchNo, string? MemberType, string? Insurer, int CoveredPersons, int Cases, int BillingRecords)
{
    /// <summary>Why the member cannot be deleted, or null when it can.</summary>
    public string? BlockedReason => (Cases, BillingRecords) switch
    {
        (> 0, > 0) => $"has {Cases} case(s) and {BillingRecords} billing record(s)",
        (> 0, _) => $"has {Cases} case(s)",
        (_, > 0) => $"has {BillingRecords} billing record(s)",
        _ => null,
    };
}

public sealed record DeletionPreview(IReadOnlyList<DeletionCandidate> Candidates, int Principals, int CoveredPersons, int Blocked);

/// <summary><see cref="ExpectedMembers"/> must equal the number of members the criteria match right now (the count the user saw in the preview).</summary>
public sealed record DeletionRequest(DeletionCriteria? Criteria, int? ExpectedMembers);

public sealed record DeletionResult(int MembersDeleted, int CoveredPersonsDeleted, int RecordsArchived);

public static partial class DeletionRules
{
    /// <summary>The legacy form had no limit; one request now handles at most this many principal members so a wrong range cannot wipe thousands.</summary>
    public const int MaxMembers = 500;

    [GeneratedRegex(@"^[A-Za-z0-9*\-]{1,30}$")]
    private static partial Regex MemberPattern();

    public static (string Field, string Message)? Validate(DeletionCriteria? c)
    {
        if (c is null) return ("criteria", "Please enter the selection criteria.");
        if (string.IsNullOrWhiteSpace(c.Health)) return ("health", "Please enter the health plan.");
        if (c.Health.Trim().Length > 4) return ("health", "Invalid health plan.");
        if (string.IsNullOrWhiteSpace(c.Payor)) return ("payor", "Please enter the payor code.");
        if (c.Payor.Trim().Length != 2) return ("payor", "The payor code must be 2 characters.");
        if (!DeletionOptions.IsValid(c.Option)) return ("option", "Please choose how to select members: bordx date, batch or membership.");
        switch (c.Option)
        {
            case DeletionOptions.BordxDate:
                if (c.BordxFrom is null || c.BordxTo is null) return ("bordxFrom", "Please enter the bordx date range.");
                if (c.BordxFrom > c.BordxTo) return ("bordxTo", "The bordx end date cannot be before the start date.");
                break;
            case DeletionOptions.Batch:
                if (c.BordxFrom is null || c.BatchNo is null) return ("batchNo", "Please enter the receipt (bordx) date and the batch number.");
                break;
            case DeletionOptions.Member:
                if (!MemberPattern().IsMatch(c.MemberFrom?.Trim() ?? "") || !MemberPattern().IsMatch(c.MemberTo?.Trim() ?? "")) return ("memberFrom", "Please enter a valid membership number range.");
                break;
        }
        return null;
    }
}

/// <summary>
/// Legacy form: Membership > Deletion (frmDeleteMembers). Archives members to FWHSHISARC and removes MBM, MBMOthers and (insurer F/H) MBMCoveredPersons.
/// Safer than the legacy form: preview first, a confirmed count, a size limit, members with cases or billing records are refused,
/// and the complete record is written to the audit log before anything is changed.
/// </summary>
public sealed record HealthPlan(string Code, string? Description);

public interface IMemberDeletionRepository
{
    Task<IReadOnlyList<HealthPlan>> ListHealthPlansAsync(CancellationToken ct = default);
    Task<DeletionPreview> PreviewAsync(DeletionCriteria criteria, CancellationToken ct = default);
    Task<DeletionResult> DeleteAsync(DeletionRequest request, string user, CancellationToken ct = default);
}

