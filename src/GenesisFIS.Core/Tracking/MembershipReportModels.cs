using GenesisFIS.Core.Data;

namespace GenesisFIS.Core.Tracking;

/// <summary>
/// One of the three legacy membership report builders. They run the same report code; they differ in the views read
/// (the "_Simple" views or the full ones), the extra Jupiter filters and the menu permission.
/// </summary>
public sealed record MbmReportVariant(string Id, string Title, int ScreenPosition, string LegacyForm, bool SimpleViews, bool ExtendedFilters, string? Note = null)
{
    public GenesisDb Database => GenesisDb.Main;
    /// <summary>The view named <paramref name="baseName"/> for this variant ("VIEW_MBM_Search2" becomes "VIEW_MBM_Search2_Simple").</summary>
    public string View(string baseName) => SimpleViews ? baseName + "_Simple" : baseName;
}

public enum MbmReportKind { Summary, Listing }

/// <summary>Which rows a report counts, from the legacy "Member Type" combo: principals, supplementaries or both.</summary>
public enum MbmBasis { Principal, Supplementary, Both }

/// <summary>A grouping column. The expressions are trusted catalog text; the supplementary leg may group differently (e.g. by the principal's age band).</summary>
public sealed record MbmDimension(string Key, string Header, string Principal, string Supplementary, TrackingColumnType Type = TrackingColumnType.Text);

/// <summary>A column carried from both legs into the grouped row set, so measures can aggregate it. Expressions are trusted catalog text.</summary>
public sealed record MbmLegColumn(string Name, string Principal, string Supplementary);

/// <summary>An aggregate over the unioned legs. <see cref="Expression"/> is trusted catalog text over <c>RowKind</c> ('P'/'S') and the leg columns.</summary>
public sealed record MbmMeasure(string Key, string Header, string Expression, TrackingColumnType Type);

/// <summary>The measures of a summary report and the leg columns they need.</summary>
public sealed record MbmMeasureSet(string Id, IReadOnlyList<MbmLegColumn> LegColumns, IReadOnlyList<MbmMeasure> Measures, bool FullViewsOnly = false);

/// <summary>A listing column: <see cref="Supplementary"/> is the expression on the supplementary leg ("NULL" when the view has no such column).</summary>
public sealed record MbmListingColumn(string Key, string Header, TrackingColumnType Type, string? Principal = null, string? Supplementary = null)
{
    public string PrincipalExpr => Principal ?? Key;
    public string SupplementaryExpr => Supplementary ?? Key;
}

/// <summary>Where a report reads from. Base view names (without "_Simple"); <see cref="FullViews"/> ignores the variant and always reads the full views.</summary>
public sealed record MbmSources(string Principal, string Supplementary, string? FixedWhere = null, string? SupplementaryWhere = null, bool FullViews = false);

/// <summary>One entry of the legacy Report Type / Report Selection pair.</summary>
public sealed record MbmReport(
    string Key, string Type, string Selection, MbmReportKind Kind, MbmSources Sources,
    IReadOnlyList<MbmDimension> Dimensions, MbmMeasureSet? Measures, IReadOnlyList<MbmListingColumn> Columns,
    int? Top = null, bool PrincipalOnly = false, string? Note = null);

/// <summary>A report the legacy form offers but the port leaves out, with the reason.</summary>
public sealed record MbmDroppedReport(string Type, string Selection, string Reason);

public enum MbmFilterKind
{
    /// <summary>One of a fixed list; each choice maps to one or more column values.</summary>
    Choice,
    /// <summary>Exact match on typed text (codes, names).</summary>
    Exact,
    /// <summary>Inclusive text range from / to.</summary>
    TextRange,
    /// <summary>Inclusive date range from / to.</summary>
    DateRange,
    /// <summary>Inclusive whole-number range from / to.</summary>
    IntRange,
    /// <summary>Inclusive amount range from / to.</summary>
    MoneyRange,
}

/// <summary>A choice: the label shown and the column values it stands for; <see cref="IncludeBlank"/> also matches null / empty.</summary>
public sealed record MbmChoice(string Value, string Label, IReadOnlyList<string> Matches, bool IncludeBlank = false);

/// <summary>
/// A Selection Criteria entry of the legacy form: a value (or range) plus the "blank" tick box that finds rows where the column is empty.
/// <see cref="Column"/> is a whitelisted identifier present in every view the reports read.
/// </summary>
public sealed record MbmFilter(string Key, string Label, string Column, MbmFilterKind Kind, IReadOnlyList<MbmChoice>? Choices = null, bool Extended = false, bool Text = true)
{
    /// <summary>Choice lists filled from the database (payor, state, product category) rather than fixed.</summary>
    public string? OptionsFrom { get; init; }
}

public sealed record MbmReportRequest(string Report, MbmBasis Basis, string PrincipalType, IReadOnlyDictionary<string, string> Filters);

public interface IMembershipReportService
{
    Task<TrackingResult> RunAsync(MbmReportVariant variant, MbmReportRequest request, int maxRows, CancellationToken ct = default);

    /// <summary>Choice lists for the filters that come from the database.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<TrackingChoice>>> OptionsAsync(CancellationToken ct = default);
}
