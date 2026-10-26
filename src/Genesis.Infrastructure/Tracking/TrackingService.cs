using Genesis.Core.Data;
using Genesis.Core.Tracking;
using Genesis.Infrastructure.Data;

namespace Genesis.Infrastructure.Tracking;

internal sealed class TrackingService(IDbConnectionFactory connections) : SqlRepository(connections), ITrackingService
{
    public async Task<TrackingResult> SearchAsync(TrackingDefinition def, IReadOnlyDictionary<string, string> filters, int maxRows, CancellationToken ct = default)
    {
        var query = TrackingQueryBuilder.Build(def, filters, maxRows);
        var rows = await QueryAsync(def.Database, query.Sql, query.Parameters, ct);
        var truncated = rows.Count > maxRows;
        var data = rows.Take(maxRows).Select(r => def.Columns.Select(c => Convert(RowReader.Get(r, c.Column[(c.Column.LastIndexOf('.') + 1)..]), c.Type)).ToArray()).ToList();
        return new TrackingResult(def.Columns.Select(c => new TrackingResultColumn(c.Column, c.Header, c.Type.ToString().ToLowerInvariant())).ToList(), data, truncated);
    }

    private static object? Convert(object? value, TrackingColumnType type) => value switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd"),
        decimal m => m,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        bool b => b ? "Y" : "N",
        _ when type is TrackingColumnType.Number or TrackingColumnType.Money => System.Convert.ToDecimal(value),
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim(),
    };
}

