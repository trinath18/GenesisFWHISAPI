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

    private static LookupRow ToRow(LookupDefinition def, IDictionary<string, object?> r) =>
        new(S(r, def.KeyField.Column) ?? "", def.Fields.ToDictionary(f => f.Key, f => S(r, f.Column), StringComparer.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<LookupRow>> ListAsync(LookupDefinition def, string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + Like(search.Trim()) + "%";
        var match = string.Join(" OR ", def.Fields.Select(f => $"{f.Column} LIKE @like"));
        var sql = $"SELECT TOP ({LookupDefinition.MaxRows}) {Columns(def)} FROM {def.Table} WHERE (@like IS NULL OR {match}) ORDER BY {def.OrderBy}";
        return (await QueryAsync(def.Database, sql, new { like }, ct)).Select(r => ToRow(def, r)).ToList();
    }

    /// <summary>Checks required fields, lengths and unknown keys, and returns trimmed values keyed by field key.</summary>
    private static Dictionary<string, string> Validate(LookupDefinition def, IReadOnlyDictionary<string, string?> values, bool includeKey)
    {
        var known = def.Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        if (values.Keys.FirstOrDefault(k => !known.ContainsKey(k)) is { } unknown) throw new BusinessRuleException($"Unknown field '{unknown}'.", field: unknown);

        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in def.Fields.Where(f => includeKey || !f.IsKey))
        {
            var value = (values.TryGetValue(f.Key, out var v) ? v : null)?.Trim() ?? "";
            if (value.Length == 0 && f.Required) throw new BusinessRuleException($"Please enter {f.Label.ToLowerInvariant()}.", field: f.Key);
            if (value.Length > f.MaxLength) throw new BusinessRuleException($"{f.Label} can be at most {f.MaxLength} characters.", field: f.Key);
            if (value.Any(char.IsControl)) throw new BusinessRuleException($"{f.Label} contains characters that are not allowed.", field: f.Key);
            clean[f.Key] = value;
        }
        return clean;
    }

    private static string KeyOf(LookupDefinition def, string key)
    {
        var k = key.Trim();
        var f = def.KeyField;
        if (k.Length == 0 || k.Length > f.MaxLength || k.Any(char.IsControl)) throw new BusinessRuleException($"'{key}' is not a valid {f.Label.ToLowerInvariant()}.", field: f.Key);
        return k;
    }

    private async Task<LookupRow?> FindAsync(IDbSession session, LookupDefinition def, string key, CancellationToken ct) =>
        (await session.QueryAsync($"SELECT {Columns(def)} FROM {def.Table} WHERE {def.KeyField.Column} = @key", new { key }, ct)).Select(r => ToRow(def, r)).FirstOrDefault();

    public async Task<LookupRow> CreateAsync(LookupDefinition def, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        var clean = Validate(def, values, includeKey: true);
        var key = KeyOf(def, clean[def.KeyField.Key]);

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        if (await FindAsync(session, def, key, ct) is not null)
            throw BusinessRuleException.Conflict($"Record duplicated: '{key}' already exists. Edit the existing record instead.");

        var args = new Dictionary<string, object?>();
        foreach (var f in def.Fields) args["v_" + f.Key] = f.IsKey ? key : clean[f.Key];
        var cols = def.Fields.Select(f => f.Column).ToList();
        var vals = def.Fields.Select(f => "@v_" + f.Key).ToList();
        if (def.UpdatedByColumn is not null) { cols.Add(def.UpdatedByColumn); vals.Add("@by"); args["by"] = user; }
        if (def.UpdatedDateColumn is not null) { cols.Add(def.UpdatedDateColumn); vals.Add("@on"); args["on"] = DateTime.Today; }
        await session.ExecuteAsync($"INSERT INTO {def.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})", args, ct);
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

