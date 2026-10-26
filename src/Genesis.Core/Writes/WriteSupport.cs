namespace Genesis.Core.Writes;

public enum RuleFailure { Invalid, NotFound, Conflict }

/// <summary>A business rule or validation failure. The API turns it into 400, 404 or 409 with a message the user can act on.</summary>
public sealed class BusinessRuleException(string message, RuleFailure kind = RuleFailure.Invalid, string? field = null) : Exception(message)
{
    public RuleFailure Kind { get; } = kind;
    public string? Field { get; } = field;

    public static BusinessRuleException NotFound(string message) => new(message, RuleFailure.NotFound);
    public static BusinessRuleException Conflict(string message) => new(message, RuleFailure.Conflict);
}

/// <summary>One audited change. The legacy tables only keep "last updated by/on"; this keeps the full history without touching the database schema.</summary>
public sealed record AuditEntry(string Action, string Entity, string Key, string User, IReadOnlyDictionary<string, object?> Details);

public interface IAuditLog
{
    void Record(AuditEntry entry);
}
