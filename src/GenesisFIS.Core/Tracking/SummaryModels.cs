using GenesisFIS.Core.Data;

namespace GenesisFIS.Core.Tracking;

/// <summary>A "group by" choice. <see cref="Expression"/> is trusted catalog text (a column or a date part such as YEAR(CaseRegDate)).</summary>
public sealed record SummaryDimension(string Key, string Header, string Expression);

/// <summary>An aggregate (COUNT/SUM...). <see cref="Expression"/> is trusted catalog text.</summary>
public sealed record SummaryMeasure(string Key, string Header, string Expression, TrackingColumnType Type);

/// <summary>
/// A read-only summary report: the user chooses grouping dimensions and measures from whitelists, filters work as on tracking screens.
/// Replaces the ~120 hard-coded aggregates (count/sum by payor, plan, age band, diagnosis, hospital, month ...) of the legacy claim report forms.
/// </summary>
public sealed record SummaryDefinition(
    string Id, string Title, string Module, int ScreenPosition, GenesisDb Database, string Source,
    IReadOnlyList<TrackingFilter> Filters, IReadOnlyList<SummaryDimension> Dimensions, IReadOnlyList<SummaryMeasure> Measures,
    IReadOnlyList<string> DefaultDimensions, IReadOnlyList<string> DefaultMeasures, bool RequiresFilter = false, string? Description = null)
{
    public const int MaxDimensions = 4;
    public const int MaxRows = 2000;
}

/// <summary>Null dimensions/measures mean "use the definition defaults"; an empty dimension list means a single grand-total row.</summary>
public sealed record SummaryRequest(IReadOnlyList<string>? Dimensions, IReadOnlyList<string>? Measures, int? Top = null);

public interface ISummaryService
{
    Task<TrackingResult> RunAsync(SummaryDefinition definition, SummaryRequest request, IReadOnlyDictionary<string, string> filters, CancellationToken ct = default);
}

