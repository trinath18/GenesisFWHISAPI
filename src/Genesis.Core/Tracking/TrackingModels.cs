using Genesis.Core.Data;

namespace Genesis.Core.Tracking;

public enum TrackingFilterKind
{
    /// <summary>Exact match on the typed value.</summary>
    Exact,
    /// <summary>Exact whole-number match (for integer columns).</summary>
    Integer,
    /// <summary>Starts with the typed value.</summary>
    Prefix,
    /// <summary>Contains the typed value.</summary>
    Contains,
    /// <summary>Inclusive date range (from / to, either may be empty).</summary>
    DateRange,
    /// <summary>Inclusive text range, e.g. case numbers from..to.</summary>
    TextRange,
    /// <summary>One of a fixed list of options.</summary>
    Choice,
}

public enum TrackingColumnType { Text, Date, Money, Number }

public sealed record TrackingChoice(string Value, string Label);

public sealed record TrackingFilter(string Key, string Label, string Column, TrackingFilterKind Kind, IReadOnlyList<TrackingChoice>? Choices = null);

public sealed record TrackingColumn(string Column, string Header, TrackingColumnType Type);

/// <summary>
/// Describes one read-only tracking / report screen. Adding a screen means adding a definition here; the query builder, API,
/// Excel export and UI page are generic. Column and filter names are whitelisted (checked by tests against the real schema).
/// </summary>
public sealed record TrackingDefinition(
    string Id, string Title, string Module, int ScreenPosition, GenesisDb Database, string Source, string OrderBy,
    IReadOnlyList<TrackingFilter> Filters, IReadOnlyList<TrackingColumn> Columns, string? Description = null, bool RequiresFilter = false, string? FixedWhere = null)
{
    public const int DefaultMaxRows = 500;
    public const int ExportMaxRows = 50_000;
}

public sealed record TrackingResultColumn(string Key, string Header, string Type);

public sealed record TrackingResult(IReadOnlyList<TrackingResultColumn> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

public sealed record TrackingDefinitionSummary(string Id, string Title, string Module, int ScreenPosition, string? Description);

public sealed record TrackingFilterError(string Key, string Message);

public interface ITrackingService
{
    /// <summary>Validates the filters, builds a parameterised query for the definition and runs it.</summary>
    Task<TrackingResult> SearchAsync(TrackingDefinition definition, IReadOnlyDictionary<string, string> filters, int maxRows, CancellationToken ct = default);
}

public sealed class TrackingFilterException(IReadOnlyList<TrackingFilterError> errors) : Exception("Invalid filters.")
{
    public IReadOnlyList<TrackingFilterError> Errors { get; } = errors;
}




