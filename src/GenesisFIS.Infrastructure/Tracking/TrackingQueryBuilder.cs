using System.Globalization;
using System.Text;
using GenesisFIS.Core.Tracking;

namespace GenesisFIS.Infrastructure.Tracking;

public sealed record BuiltQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>A compiled WHERE fragment (" AND col = @p0 ...") plus its parameters; the first parameter index to use is passed in.</summary>
public sealed record CompiledFilters(string Where, Dictionary<string, object?> Parameters, int Count);

/// <summary>
/// Turns a definition plus user filters into a parameterised SELECT. Only whitelisted columns from the definition are ever
/// put into the SQL text; every user value travels as a parameter. Filter keys the definition does not know are rejected.
/// The filter compiler is shared by the tracking (row listing) and summary (group-by) engines.
/// </summary>
public static class TrackingQueryBuilder
{
    public const int MaxValueLength = 60;

    public static BuiltQuery Build(TrackingDefinition def, IReadOnlyDictionary<string, string> filters, int maxRows)
    {
        var errors = new List<TrackingFilterError>();
        var compiled = CompileFilters(def.Filters, filters, errors);
        if (def.RequiresFilter && compiled.Count == 0 && errors.Count == 0)
            errors.Add(new("", "This screen reads a very large table: enter at least one filter."));
        if (errors.Count > 0) throw new TrackingFilterException(errors);

        var select = string.Join(", ", def.Columns.Select(c => c.Column));
        compiled.Parameters["take"] = maxRows + 1;
        var fixedWhere = string.IsNullOrWhiteSpace(def.FixedWhere) ? "" : $" AND ({def.FixedWhere})";
        var sql = $"SELECT TOP (@take) {select} FROM {def.Source} WHERE 1 = 1{fixedWhere}{compiled.Where} ORDER BY {def.OrderBy}";
        return new BuiltQuery(sql, compiled.Parameters);
    }

    /// <summary>Validates the user's filters against the definitions and compiles them. Problems are added to <paramref name="errors"/>.</summary>
    public static CompiledFilters CompileFilters(IReadOnlyList<TrackingFilter> definitions, IReadOnlyDictionary<string, string> userFilters, List<TrackingFilterError> errors)
    {
        var filters = new Dictionary<string, string>(userFilters, StringComparer.OrdinalIgnoreCase);
        var known = definitions.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in filters.Keys)
        {
            var baseKey = key.EndsWith(".from", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".to", StringComparison.OrdinalIgnoreCase) ? key[..key.LastIndexOf('.')] : key;
            if (!known.TryGetValue(baseKey, out var f) || (key != baseKey && f.Kind is not (TrackingFilterKind.DateRange or TrackingFilterKind.TextRange)))
                errors.Add(new(key, "Unknown filter."));
        }

        var where = new StringBuilder();
        var args = new Dictionary<string, object?>();
        var n = 0;
        string P(object? value) { var name = $"p{n++}"; args[name] = value; return "@" + name; }
        void Add(string clause) => where.Append(" AND ").Append(clause);

        foreach (var f in definitions)
        {
            string? Get(string suffix = "") => filters.TryGetValue(f.Key + suffix, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            string? Text(string? raw, string key)
            {
                if (raw is null) return null;
                if (raw.Length > MaxValueLength || raw.Any(char.IsControl)) { errors.Add(new(key, $"Value must be at most {MaxValueLength} characters with no control characters.")); return null; }
                return raw;
            }

            switch (f.Kind)
            {
                case TrackingFilterKind.Exact when Text(Get(), f.Key) is { } eq:
                    Add($"{f.Column} = {P(eq)}"); break;
                case TrackingFilterKind.Integer when Get() is { } num:
                    if (int.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) Add($"{f.Column} = {P(i)}");
                    else errors.Add(new(f.Key, "Enter a whole number."));
                    break;
                case TrackingFilterKind.Prefix when Text(Get(), f.Key) is { } pre:
                    Add($"{f.Column} LIKE {P(Like(pre) + "%")}"); break;
                case TrackingFilterKind.Contains when Text(Get(), f.Key) is { } con:
                    Add($"{f.Column} LIKE {P("%" + Like(con) + "%")}"); break;
                case TrackingFilterKind.Choice when Get() is { } choice:
                    if (f.Choices!.Any(c => c.Value == choice)) Add($"{f.Column} = {P(choice)}");
                    else errors.Add(new(f.Key, "Not one of the allowed values."));
                    break;
                case TrackingFilterKind.TextRange:
                    if (Text(Get(".from"), f.Key + ".from") is { } tf) Add($"{f.Column} >= {P(tf)}");
                    if (Text(Get(".to"), f.Key + ".to") is { } tt) Add($"{f.Column} <= {P(tt)}");
                    break;
                case TrackingFilterKind.DateRange:
                    if (Get(".from") is { } df)
                    {
                        if (TryDate(df, out var d)) Add($"{f.Column} >= {P(d)}"); else errors.Add(new(f.Key + ".from", "Use a valid date (yyyy-mm-dd)."));
                    }
                    if (Get(".to") is { } dt)
                    {
                        // Inclusive end date: compare to the start of the next day so times on the last day are included.
                        if (TryDate(dt, out var d)) Add($"{f.Column} < {P(d.AddDays(1))}"); else errors.Add(new(f.Key + ".to", "Use a valid date (yyyy-mm-dd)."));
                    }
                    break;
            }
        }

        return new CompiledFilters(where.ToString(), args, n);
    }

    private static bool TryDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
