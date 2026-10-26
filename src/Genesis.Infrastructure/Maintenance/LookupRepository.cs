using System.Globalization;
using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class LookupRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ILookupRepository
{
    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    private static string Columns(LookupDefinition def) => string.Join(", ", def.Fields.Select(f => f.Column));

    private static string Display(object? value) => value switch
    {
        null or DBNull => "",
        decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
        string s => s.Trim().Trim('\0'),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    private static LookupRow ToRow(LookupDefinition def, IDictionary<string, object?> r) =>
        new(Display(Get(r, def.KeyField.Column)), def.Fields.ToDictionary(f => f.Key, f => Display(Get(r, f.Column)) is { Length: > 0 } v ? v : null, StringComparer.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<LookupRow>> ListAsync(LookupDefinition def, string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + Like(search.Trim()) + "%";
        var match = string.Join(" OR ", def.Fields.Select(f => f.Kind == LookupFieldKind.Money ? null : f.Kind == LookupFieldKind.Text && !f.IsKey ? $"{f.Column} LIKE @like" : $"CAST({f.Column} AS nvarchar(50)) LIKE @like").Where(x => x is not null));
        var sql = $"SELECT TOP ({LookupDefinition.MaxRows}) {Columns(def)} FROM {def.Table} WHERE (@like IS NULL OR {match}) ORDER BY {def.OrderBy}";
        return (await QueryAsync(def.Database, sql, new { like }, ct)).Select(r => ToRow(def, r)).ToList();
    }

    /// <summary>
    /// Checks required fields, lengths, allowed values and unknown keys. Returns typed values keyed by field key: trimmed text, a decimal for money,
    /// and null for an optional field left empty (the legacy forms stored NULL).
    /// </summary>
    private static Dictionary<string, object?> Validate(LookupDefinition def, IReadOnlyDictionary<string, string?> values, bool includeKey)
    {
        var known = def.Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        if (values.Keys.FirstOrDefault(k => !known.ContainsKey(k)) is { } unknown) throw new BusinessRuleException($"Unknown field '{unknown}'.", field: unknown);

        var clean = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in def.Fields.Where(f => includeKey || !f.IsKey))
        {
            var value = (values.TryGetValue(f.Key, out var v) ? v : null)?.Trim() ?? "";
            if (value.Length == 0)
            {
                if (f.Required) throw new BusinessRuleException($"Please enter {f.Label.ToLowerInvariant()}.", field: f.Key);
                clean[f.Key] = null;
                continue;
            }
            if (value.Length > f.MaxLength) throw new BusinessRuleException($"{f.Label} can be at most {f.MaxLength} characters.", field: f.Key);
            if (value.Any(c => char.IsControl(c) && !(f.Multiline && c is '\r' or '\n'))) throw new BusinessRuleException($"{f.Label} contains characters that are not allowed.", field: f.Key);

            switch (f.Kind)
            {
                case LookupFieldKind.Choice when f.Choices is { Count: > 0 } && !f.Choices.Contains(value):
                    throw new BusinessRuleException($"{f.Label} must be one of: {string.Join(", ", f.Choices)}.", field: f.Key);
                case LookupFieldKind.Money:
                    if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var amount) || amount < 0 || amount > LookupDefinition.MaxMoney)
                        throw new BusinessRuleException($"{f.Label} must be an amount of zero or more.", field: f.Key);
                    clean[f.Key] = Math.Round(amount, 4);
                    continue;
            }
            clean[f.Key] = value;
        }
        return clean;
    }

    private static string KeyOf(LookupDefinition def, string key)
    {
        var k = key.Trim();
        var f = def.KeyField;
        var bad = k.Length == 0 || k.Length > f.MaxLength || k.Any(char.IsControl) || (def.KeyIsIdentity && !k.All(char.IsAsciiDigit));
        if (bad) throw new BusinessRuleException($"'{key}' is not a valid {f.Label.ToLowerInvariant()}.", field: f.Key);
        return k;
    }

    private static async Task<LookupRow?> FindAsync(IDbSession session, LookupDefinition def, string key, CancellationToken ct) =>
        (await session.QueryAsync($"SELECT {Columns(def)} FROM {def.Table} WHERE {def.KeyField.Column} = @key", new { key }, ct)).Select(r => ToRow(def, r)).FirstOrDefault();

    public async Task<LookupRow> CreateAsync(LookupDefinition def, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        if (def.KeyIsIdentity && values.TryGetValue(def.KeyField.Key, out var given) && !string.IsNullOrWhiteSpace(given))
            throw new BusinessRuleException($"The {def.KeyField.Label.ToLowerInvariant()} is generated automatically.", field: def.KeyField.Key);
        var clean = Validate(def, values, includeKey: !def.KeyIsIdentity);

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        string key;
        var args = new Dictionary<string, object?>();
        var cols = new List<string>();
        var vals = new List<string>();
        foreach (var f in def.Fields.Where(f => !(f.IsKey && def.KeyIsIdentity)))
        {
            cols.Add(f.Column); vals.Add("@v_" + f.Key); args["v_" + f.Key] = clean[f.Key];
        }
        if (def.UpdatedByColumn is not null) { cols.Add(def.UpdatedByColumn); vals.Add("@by"); args["by"] = user; }
        if (def.UpdatedDateColumn is not null) { cols.Add(def.UpdatedDateColumn); vals.Add("@on"); args["on"] = DateTime.Today; }

        if (def.KeyIsIdentity)
        {
            var id = await session.ScalarAsync<int>($"INSERT INTO {def.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)}); SELECT CAST(SCOPE_IDENTITY() AS int);", args, ct);
            key = id.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            key = KeyOf(def, (string)clean[def.KeyField.Key]!);
            if (await FindAsync(session, def, key, ct) is not null)
                throw BusinessRuleException.Conflict($"Record duplicated: '{key}' already exists. Edit the existing record instead.");
            await session.ExecuteAsync($"INSERT INTO {def.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})", args, ct);
        }

        var created = await FindAsync(session, def, key, ct) ?? throw new InvalidOperationException("The new record could not be read back.");
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Lookup.Create", def.Table, key, user, new Dictionary<string, object?> { ["values"] = created.Values }));
        return created;
    }

    public async Task<LookupRow> UpdateAsync(LookupDefinition def, string key, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        key = KeyOf(def, key);
        if (values.TryGetValue(def.KeyField.Key, out var sent) && !string.Equals(sent?.Trim(), key, StringComparison.Ordinal))
            throw new BusinessRuleException($"The {def.KeyField.Label.ToLowerInvariant()} cannot be changed.", field: def.KeyField.Key);
        var clean = Validate(def, values, includeKey: false);

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        var before = await FindAsync(session, def, key, ct) ?? throw BusinessRuleException.NotFound($"'{key}' does not exist.");

        var args = new Dictionary<string, object?> { ["key"] = key };
        var sets = new List<string>();
        foreach (var f in def.Fields.Where(f => !f.IsKey)) { sets.Add($"{f.Column} = @v_{f.Key}"); args["v_" + f.Key] = clean[f.Key]; }
        if (def.UpdatedByColumn is not null) { sets.Add($"{def.UpdatedByColumn} = @by"); args["by"] = user; }
        if (def.UpdatedDateColumn is not null) { sets.Add($"{def.UpdatedDateColumn} = @on"); args["on"] = DateTime.Today; }
        await session.ExecuteAsync($"UPDATE {def.Table} SET {string.Join(", ", sets)} WHERE {def.KeyField.Column} = @key", args, ct);
        var after = await FindAsync(session, def, key, ct) ?? throw new InvalidOperationException("The record could not be read back.");
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Lookup.Update", def.Table, key, user, new Dictionary<string, object?> { ["before"] = before.Values, ["after"] = after.Values }));
        return after;
    }

    public async Task DeleteAsync(LookupDefinition def, string key, string user, CancellationToken ct = default)
    {
        key = KeyOf(def, key);
        if (!def.AllowDelete) throw new BusinessRuleException("Records of this table cannot be deleted.");

        foreach (var reference in def.References)
        {
            var used = (await QueryAsync(reference.Database, $"SELECT COUNT(*) AS N FROM {reference.Table} WHERE {reference.Column} = @key", new { key }, ct)).Select(r => I(r, "N") ?? 0).First();
            if (used > 0) throw BusinessRuleException.Conflict($"'{key}' cannot be deleted: it is used by {used} {reference.Description}.");
        }

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        var before = await FindAsync(session, def, key, ct) ?? throw BusinessRuleException.NotFound($"'{key}' does not exist.");
        await session.ExecuteAsync($"DELETE FROM {def.Table} WHERE {def.KeyField.Column} = @key", new { key }, ct);
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Lookup.Delete", def.Table, key, user, new Dictionary<string, object?> { ["before"] = before.Values }));
    }
}
