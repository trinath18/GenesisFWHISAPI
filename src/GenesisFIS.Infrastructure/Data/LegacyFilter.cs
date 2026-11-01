using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GenesisFIS.Core.Writes;

namespace GenesisFIS.Infrastructure.Data;

/// <summary>
/// Builds the "@StrAppend" SQL fragment that several legacy procedures (Case_Send2ClaimRep, CRU_Send2Claim, Case_ClaimAscii) append to their own
/// dynamic SQL. The fragment itself is passed as a parameter, but the procedure concatenates it, so values must never carry SQL:
/// column names come only from constants in the calling code, text values must match a strict whitelist (letters, digits, * - _ . / and space; no quotes),
/// dates are written by the server as 'MM/dd/yyyy' and numbers as integers. Anything else is refused before the procedure is called.
/// Use this only for those legacy procedures; new code uses ordinary parameterised SQL.
/// </summary>
internal sealed partial class LegacyFilter
{
    [GeneratedRegex(@"^[A-Za-z0-9*\-_./ ]{1,30}$")]
    private static partial Regex SafeValue();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,60}$")]
    private static partial Regex SafeColumn();

    private readonly StringBuilder _sql = new();
    public int Count { get; private set; }

    public static string Value(string? value, string field)
    {
        var v = value?.Trim() ?? "";
        if (!SafeValue().IsMatch(v)) throw new BusinessRuleException("Invalid search value.", field: field);
        return v;
    }

    private static string Col(string column) => SafeColumn().IsMatch(column) ? column : throw new ArgumentException($"Bad column name {column}", nameof(column));

    /// <summary>AND column op 'value' for a whitelisted text value; skipped when the value is empty.</summary>
    public LegacyFilter Text(string column, string op, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        return Add($" AND {Col(column)} {Op(op)} '{Value(value, field)}'");
    }

    /// <summary>AND column op 'MM/dd/yyyy' (the format the legacy forms used); skipped when there is no date.</summary>
    public LegacyFilter Date(string column, string op, DateOnly? value) =>
        value is { } d ? Add($" AND {Col(column)} {Op(op)} '{d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}'") : this;

    /// <summary>AND column op 'n' (the legacy quoted numbers too); skipped when there is no number.</summary>
    public LegacyFilter Number(string column, string op, int? value) =>
        value is { } n ? Add($" AND {Col(column)} {Op(op)} '{n.ToString(CultureInfo.InvariantCulture)}'") : this;

    /// <summary>AND (column IS NULL OR column = ''): the legacy "*" search for empty data.</summary>
    public LegacyFilter Empty(string column, bool when) => when ? Add($" AND ({Col(column)} IS NULL OR {Col(column)} = '')") : this;

    /// <summary>A fixed fragment written in the calling code (never user input).</summary>
    public LegacyFilter Constant(string sql) => Add(" " + sql.Trim());

    private static string Op(string op) => op is "=" or ">=" or "<=" ? op : throw new ArgumentException($"Bad operator {op}", nameof(op));
    private LegacyFilter Add(string s) { _sql.Append(s); Count++; return this; }
    public override string ToString() => _sql.ToString();
}
