using GenesisFIS.Core.Data;
using GenesisFIS.Core.Tracking;
using GenesisFIS.Infrastructure.Data;

namespace GenesisFIS.Infrastructure.Tracking;

internal sealed class SummaryService(IDbConnectionFactory connections) : SqlRepository(connections), ISummaryService
{
    public async Task<TrackingResult> RunAsync(SummaryDefinition def, SummaryRequest request, IReadOnlyDictionary<string, string> filters, CancellationToken ct = default)
    {
        var q = SummaryQueryBuilder.Build(def, request, filters);
        var rows = await QueryAsync(def.Database, q.Sql, q.Parameters, ct);
        var take = request.Top ?? SummaryDefinition.MaxRows;

        var dimKeys = (request.Dimensions ?? def.DefaultDimensions).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => def.Dimensions.First(d => string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase))).ToList();
        var measureKeys = (request.Measures ?? def.DefaultMeasures).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => def.Measures.First(m => string.Equals(m.Key, k, StringComparison.OrdinalIgnoreCase))).ToList();

        var columns = dimKeys.Select(d => new TrackingResultColumn(d.Key, d.Header, "text"))
            .Concat(measureKeys.Select(m => new TrackingResultColumn(m.Key, m.Header, m.Type.ToString().ToLowerInvariant()))).ToList();
        var data = rows.Take(take).Select(r =>
            dimKeys.Select(d => Clean(RowReader.Get(r, d.Key)))
                .Concat(measureKeys.Select(m => RowReader.Get(r, m.Key) is null or DBNull ? (object?)0m : Convert.ToDecimal(RowReader.Get(r, m.Key))))
                .ToArray()).ToList();
        return new TrackingResult(columns, data, rows.Count > take);
    }

    private static object? Clean(object? v) => v switch
    {
        null or DBNull => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };
}

