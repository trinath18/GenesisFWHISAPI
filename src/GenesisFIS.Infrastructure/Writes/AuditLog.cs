using GenesisFIS.Core.Writes;
using Microsoft.Extensions.Logging;

namespace GenesisFIS.Infrastructure.Writes;

/// <summary>
/// Writes audit entries through the logging pipeline under the category <see cref="Category"/>. The API host routes that category to its own
/// append-only JSON file (Audit:Path), so history is kept without adding tables to the legacy databases.
/// </summary>
internal sealed class AuditLog(ILoggerFactory loggers) : IAuditLog
{
    public const string Category = "Genesis.Audit";
    private readonly ILogger _log = loggers.CreateLogger(Category);

    public void Record(AuditEntry entry) =>
        _log.LogInformation("AUDIT {Action} {Entity} {Key} by {User} {@Details}", entry.Action, entry.Entity, entry.Key, entry.User, entry.Details);
}
