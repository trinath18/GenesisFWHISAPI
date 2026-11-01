using System.Text;
using GenesisFIS.Core.Tracking;

namespace GenesisFIS.Infrastructure.Tracking;

/// <summary>
/// Builds a GROUP BY query from a summary definition. Dimension and measure expressions come only from the definition (chosen by key);
/// filters reuse the tracking filter compiler, so every user value is a parameter.
/// </summary>
public static class SummaryQueryBuilder
{
    public static BuiltQuery Build(SummaryDefinition def, SummaryRequest request, IReadOnlyDictionary<string, string> filters)
    {
        var errors = new List<TrackingFilterError>();
        var dims = Pick(def.Dimensions, request.Dimensions ?? def.DefaultDimensions, d => d.Key, "dimension", errors);
        var measures = Pick(def.Measures, request.Measures ?? def.DefaultMeasures, m => m.Key, "measure", errors);
        if (dims.Count > SummaryDefinition.MaxDimensions) errors.Add(new("dimensions", $"Choose at most {SummaryDefinition.MaxDimensions} grouping columns."));
        if (measures.Count == 0) errors.Add(new("measures", "Choose at least one measure."));
        if (request.Top is < 1 or > SummaryDefinition.MaxRows) errors.Add(new("top", $"Top must be between 1 and {SummaryDefinition.MaxRows}."));

        var compiled = TrackingQueryBuilder.CompileFilters(def.Filters, filters, errors);
        if (def.RequiresFilter && compiled.Count == 0 && errors.Count == 0)
            errors.Add(new("", "This report reads a very large table: enter at least one filter."));
        if (errors.Count > 0) throw new TrackingFilterException(errors);

        var take = request.Top ?? SummaryDefinition.MaxRows;
        compiled.Parameters["take"] = take + 1;
        var select = new StringBuilder();
        select.Append(string.Join(", ", dims.Select(d => $"{d.Expression} AS [{d.Key}]").Concat(measures.Select(m => $"{m.Expression} AS [{m.Key}]"))));
        var sql = new StringBuilder($"SELECT TOP (@take) {select} FROM {def.Source} WHERE 1 = 1{compiled.Where}");
        if (dims.Count > 0)
        {
            sql.Append(" GROUP BY ").Append(string.Join(", ", dims.Select(d => d.Expression)));
            // With a "top" request the biggest groups first; otherwise stable by the grouping columns.
            sql.Append(" ORDER BY ").Append(request.Top is not null ? $"{measures[0].Expression} DESC" : string.Join(", ", dims.Select(d => d.Expression)));
        }
        return new BuiltQuery(sql.ToString(), compiled.Parameters);
    }

    private static List<T> Pick<T>(IReadOnlyList<T> allowed, IReadOnlyList<string> keys, Func<T, string> keyOf, string what, List<TrackingFilterError> errors)
    {
        var picked = new List<T>();
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = allowed.FirstOrDefault(a => string.Equals(keyOf(a), key, StringComparison.OrdinalIgnoreCase));
            if (item is null) errors.Add(new(what + "s", $"Unknown {what}: {key}")); else picked.Add(item);
        }
        return picked;
    }
}

