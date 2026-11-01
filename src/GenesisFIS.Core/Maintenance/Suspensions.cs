namespace GenesisFIS.Core.Maintenance;

/// <summary>Suspension category codes used by the legacy form: 01 = membership (member number), 02 = agent (agent code).</summary>
public static class SuspensionKinds
{
    public const string Member = "01";
    public const string Agent = "02";
    public static bool IsValid(string? kind) => kind is Member or Agent;
}

public sealed record Suspension(string Kind, string Code, string? Remarks, string? PayorCode, string? PayorName, string? UpdatedBy, DateOnly? UpdatedOn);

/// <summary>Create or change one suspension. <see cref="Kind"/> is 01 (member) or 02 (agent); the code is the member number or agent code.</summary>
public sealed record SuspensionRequest(string? Kind, string? Code, string? Remarks, string? PayorCode);

public sealed record Payor(string Code, string? Name);

public static class SuspensionRules
{
    public const int MaxMemberNumber = 50, MaxAgentCode = 15, MaxRemarks = 100, SearchLimit = 1000;

    /// <summary>Legacy form checks: everything is required, and a member number must start with its payor code.</summary>
    public static (string Field, string Message)? Validate(SuspensionRequest r)
    {
        if (!SuspensionKinds.IsValid(r.Kind)) return ("kind", "Please select Member or Agent.");
        var code = r.Code?.Trim() ?? "";
        if (code.Length == 0) return ("code", r.Kind == SuspensionKinds.Member ? "Please enter the member number." : "Please enter the agent code.");
        var max = r.Kind == SuspensionKinds.Member ? MaxMemberNumber : MaxAgentCode;
        if (code.Length > max) return ("code", $"The code can be at most {max} characters.");
        if (code.Any(char.IsControl)) return ("code", "The code contains characters that are not allowed.");
        if (string.IsNullOrWhiteSpace(r.Remarks)) return ("remarks", "Please enter the remarks.");
        if (r.Remarks.Trim().Length > MaxRemarks) return ("remarks", $"Remarks can be at most {MaxRemarks} characters.");
        if (r.Remarks.Any(c => char.IsControl(c) && c is not ('\r' or '\n'))) return ("remarks", "The remarks contain characters that are not allowed.");
        var payor = r.PayorCode?.Trim() ?? "";
        if (payor.Length == 0) return ("payorCode", "Please select the payor.");
        if (payor.Length != 2) return ("payorCode", "The payor code must be 2 characters.");
        if (r.Kind == SuspensionKinds.Member && !code.StartsWith(payor, StringComparison.OrdinalIgnoreCase))
            return ("code", "The member number does not belong to the selected payor.");
        return null;
    }
}

/// <summary>Legacy form: Maintenance > Suspension, "detail" tab (table SuspensionDetail in the main database). Members or agents that are suspended.</summary>
public interface ISuspensionRepository
{
    Task<IReadOnlyList<Payor>> ListPayorsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Suspension>> SearchAsync(string? kind, string? code, string? payorCode, CancellationToken ct = default);
    Task<Suspension> CreateAsync(SuspensionRequest request, string user, CancellationToken ct = default);

    /// <summary>Changes remarks and payor of an existing suspension (the kind and code identify it and cannot change).</summary>
    Task<Suspension> UpdateAsync(SuspensionRequest request, string user, CancellationToken ct = default);
    Task DeleteAsync(string kind, string code, string user, CancellationToken ct = default);
}
