using System.Globalization;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Maintenance;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Maintenance;

internal sealed class LookupRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ILookupRepository
{
    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    private static string Columns(LookupDefinition def) => string.Join(", ", def.Fields.Select(f => f.Column));

    private static string Display(object? value) => value switch
    {
        null or DBNull => "",
        decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s => s.Trim().Trim('\0'),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    // Some whole-number columns are decimal(18,0) (e.g. PLN.SpeGPeriodDays): show them without decimals so an edit round-trips through Integer validation.
    private static string Display(LookupField f, object? value) => f.Kind == LookupFieldKind.Integer && value is decimal d ? d.ToString("0", CultureInfo.InvariantCulture) : Display(value);

    private static LookupRow ToRow(LookupDefinition def, IDictionary<string, object?> r) =>
        new(string.Join("|", def.KeyFields.Select(k => Display(Get(r, k.Column)))), def.Fields.ToDictionary(f => f.Key, f => Display(f, Get(r, f.Column)) is { Length: > 0 } v ? v : null, StringComparer.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<LookupRow>> ListAsync(LookupDefinition def, string? search, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + Like(search.Trim()) + "%";
        var match = string.Join(" OR ", def.Fields.Select(f => f.Kind is LookupFieldKind.Money or LookupFieldKind.Date ? null : f.Kind == LookupFieldKind.Text && !f.IsKey ? $"{f.Column} LIKE @like" : $"CAST({f.Column} AS nvarchar(50)) LIKE @like").Where(x => x is not null));
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
                case LookupFieldKind.Date:
                    if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day.Year < 1900 || day.Year > 2999)
                        throw new BusinessRuleException($"{f.Label} must be a valid date.", field: f.Key);
                    clean[f.Key] = day;
                    continue;
                case LookupFieldKind.Integer:
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                        throw new BusinessRuleException($"{f.Label} must be a whole number.", field: f.Key);
                    clean[f.Key] = whole;
                    continue;
            }
            clean[f.Key] = value;
        }
        foreach (var f in def.Fields.Where(f => f.RequiredWhen is not null && clean.ContainsKey(f.Key) && clean[f.Key] is null))
            if (clean.TryGetValue(f.RequiredWhen!.Field, out var other) && string.Equals(other as string, f.RequiredWhen.Value, StringComparison.OrdinalIgnoreCase))
                throw new BusinessRuleException($"Please enter {f.Label.ToLowerInvariant()}.", field: f.Key);
        return clean;
    }

    private static string SourceCode(LookupSource s) => $"LTRIM(RTRIM({s.CodeColumn}))";
    private static string SourceFilter(LookupSource s) => s.Filter is null ? "" : $" AND ({s.Filter})";

    public async Task<IReadOnlyList<LookupOption>> OptionsAsync(LookupSource source, CancellationToken ct = default)
    {
        var label = source.LabelColumn is null ? "NULL" : $"MAX(LTRIM(RTRIM({source.LabelColumn})))";
        var sql = $"SELECT TOP ({LookupDefinition.MaxRows}) {SourceCode(source)} AS Value, {label} AS Label FROM {source.Table} " +
                  $"WHERE {SourceCode(source)} <> ''{SourceFilter(source)} GROUP BY {SourceCode(source)} ORDER BY 1";
        return (await QueryAsync(source.Database, sql, null, ct)).Select(r => new LookupOption(Display(Get(r, "Value")), Display(Get(r, "Label")) is { Length: > 0 } l ? l : null)).ToList();
    }

    /// <summary>
    /// Refuses a drop-down value that is not in its code table. On edit only changed values are checked, so a legacy row whose code has since
    /// been removed can still be saved.
    /// </summary>
    private async Task CheckSourcesAsync(LookupDefinition def, Dictionary<string, object?> clean, LookupRow? before, CancellationToken ct)
    {
        foreach (var f in def.Fields.Where(f => f.Source is not null && clean.TryGetValue(f.Key, out var v) && v is string))
        {
            var value = (string)clean[f.Key]!;
            if (before is not null && string.Equals(before.Values.GetValueOrDefault(f.Key), value, StringComparison.OrdinalIgnoreCase)) continue;
            var s = f.Source!;
            var found = (await QueryAsync(s.Database, $"SELECT COUNT(*) AS N FROM {s.Table} WHERE {SourceCode(s)} = @value{SourceFilter(s)}", new { value }, ct)).Select(r => I(r, "N") ?? 0).First();
            if (found == 0) throw new BusinessRuleException($"'{value}' is not a valid {f.Label.ToLowerInvariant()}.", field: f.Key);
        }
    }

    private static string KeyOf(LookupDefinition def, string key)
    {
        if (def.KeyFields.Count > 1)
        {
            var parts = key.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length != def.KeyFields.Count || parts.Select((p, i) => p.Length == 0 || p.Length > def.KeyFields[i].MaxLength || p.Any(char.IsControl)).Any(b => b))
                throw new BusinessRuleException($"'{key}' is not a valid {def.Title.ToLowerInvariant()} key.");
            return string.Join("|", parts);
        }
        var k = key.Trim();
        var f = def.KeyField;
        var bad = k.Length == 0 || k.Length > f.MaxLength || k.Any(char.IsControl) || (def.KeyIsIdentity && !k.All(char.IsAsciiDigit));
        if (bad) throw new BusinessRuleException($"'{key}' is not a valid {f.Label.ToLowerInvariant()}.", field: f.Key);
        return k;
    }

    private static (string Where, Dictionary<string, object?> Args) KeyWhere(LookupDefinition def, string key)
    {
        var parts = key.Split('|');
        var args = new Dictionary<string, object?> { ["key"] = key };
        for (var i = 0; i < def.KeyFields.Count; i++) args["k" + i] = parts[i];
        return (string.Join(" AND ", def.KeyFields.Select((k, i) => $"{k.Column} = @k{i}")), args);
    }

    private static async Task<LookupRow?> FindAsync(IDbSession session, LookupDefinition def, string key, CancellationToken ct)
    {
        var (where, args) = KeyWhere(def, key);
        return (await session.QueryAsync($"SELECT {Columns(def)} FROM {def.Table} WHERE {where}", args, ct)).Select(r => ToRow(def, r)).FirstOrDefault();
    }

    public async Task<LookupRow> CreateAsync(LookupDefinition def, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        var generatedKey = def.KeyIsIdentity ? def.KeyField : def.CodeSequence is { } seq ? def.Fields.First(f => f.Key == seq.CodeField) : null;
        if (generatedKey is not null && values.TryGetValue(generatedKey.Key, out var given) && !string.IsNullOrWhiteSpace(given))
            throw new BusinessRuleException($"The {generatedKey.Label.ToLowerInvariant()} is generated automatically.", field: generatedKey.Key);
        // A sequence code is validated with a placeholder and generated inside the transaction, once the rest of the row is known to be valid.
        var input = def.CodeSequence is null ? values : new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase) { [def.CodeSequence.CodeField] = "X" };
        var clean = Validate(def, input, includeKey: !def.KeyIsIdentity);
        await CheckSourcesAsync(def, clean, null, ct);

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        if (def.UniqueField is { } unique)
        {
            var f = def.Fields.First(x => x.Key == unique);
            if (await session.ScalarAsync<int>($"SELECT COUNT(*) FROM {def.Table} WHERE {f.Column} = @value", new { value = clean[unique] }, ct) > 0)
                throw BusinessRuleException.Conflict($"Record duplicated: {f.Label.ToLowerInvariant()} '{clean[unique]}' already exists.");
        }
        if (def.CodeSequence is { } sequence) clean[sequence.CodeField] = await NextCodeAsync(session, def, sequence, (string)clean[sequence.SourceField]!, ct);
        var created = await InsertAsync(session, def, clean, user, ct);
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Lookup.Create", def.Table, created.Key, user, new Dictionary<string, object?> { ["values"] = created.Values }));
        return created;
    }

    /// <summary>Inserts one validated row (stamping the user and date) and reads it back. The caller commits.</summary>
    private static async Task<LookupRow> InsertAsync(IDbSession session, LookupDefinition def, Dictionary<string, object?> clean, string user, CancellationToken ct)
    {
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
            key = KeyOf(def, string.Join("|", def.KeyFields.Select(k => (string)clean[k.Key]!)));
            if (await FindAsync(session, def, key, ct) is not null)
                throw BusinessRuleException.Conflict($"Record duplicated: '{key}' already exists. Edit the existing record instead.");
            await session.ExecuteAsync($"INSERT INTO {def.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})", args, ct);
        }

        return await FindAsync(session, def, key, ct) ?? throw new InvalidOperationException("The new record could not be read back.");
    }

    public async Task<IReadOnlyList<LookupRow>> CreatePlansAsync(LookupDefinition def, PlanBatch batch, string user, CancellationToken ct = default)
    {
        if (batch.Values.Keys.FirstOrDefault(k => k.Equals("code", StringComparison.OrdinalIgnoreCase) || k.Equals("description", StringComparison.OrdinalIgnoreCase) || k.Equals("index", StringComparison.OrdinalIgnoreCase)) is { } perPlan)
            throw new BusinessRuleException($"'{perPlan}' is entered per plan, not for the whole batch.", field: perPlan);

        // The shared fields are validated once; the code is a placeholder until each plan's own code is known.
        var shared = Validate(def, new Dictionary<string, string?>(batch.Values, StringComparer.OrdinalIgnoreCase) { ["code"] = "X" }, includeKey: false);
        await CheckSourcesAsync(def, shared, null, ct);
        var generated = string.Equals((string?)shared["health"], PlanBatch.GeneratedHealthType, StringComparison.OrdinalIgnoreCase);
        var description = def.Fields.First(f => f.Key == "description");
        var code = def.Fields.First(f => f.Key == "code");

        List<(string? Code, string? Description)> plans;
        if (generated)
        {
            if (batch.Count is < 1 or > PlanBatch.MaxPlans) throw new BusinessRuleException($"Please choose 1 to {PlanBatch.MaxPlans} plan codes.", field: "count");
            if (batch.Plans.Any(p => !string.IsNullOrWhiteSpace(p.Code))) throw new BusinessRuleException("Plan codes of this health type are generated automatically.", field: "code");
            if (batch.Plans.Count > batch.Count) throw new BusinessRuleException("There are more descriptions than plans.", field: "description");
            plans = Enumerable.Range(0, batch.Count).Select(i => ((string?)null, i < batch.Plans.Count ? batch.Plans[i].Description?.Trim() : null)).ToList();
        }
        else
        {
            plans = batch.Plans.Where(p => !string.IsNullOrWhiteSpace(p.Code)).Select(p => ((string?)p.Code!.Trim(), p.Description?.Trim())).ToList();
            if (plans.Count == 0) throw new BusinessRuleException("Please enter plan code.", field: "code");
            if (plans.Count > PlanBatch.MaxPlans) throw new BusinessRuleException($"At most {PlanBatch.MaxPlans} plans can be saved at once.", field: "code");
            if (plans.GroupBy(p => p.Code).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new BusinessRuleException($"Plan code '{twice.Key}' is entered twice.", field: "code");
            foreach (var p in plans)
                if (p.Code!.Length > code.MaxLength || p.Code.Any(char.IsControl))
                    throw new BusinessRuleException($"Plan code '{p.Code}' can be at most {code.MaxLength} characters.", field: "code");
        }
        foreach (var p in plans)
            if (p.Description is { Length: > 0 } d && (d.Length > description.MaxLength || d.Any(char.IsControl)))
                throw new BusinessRuleException($"{description.Label} can be at most {description.MaxLength} characters.", field: "description");

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        var created = new List<LookupRow>();
        foreach (var p in plans)
        {
            var planCode = p.Code ?? await NextCodeAsync(session, def, PlanCodes, (string)shared["group"]!, ct);
            // Plans are versioned by effective date (the legacy edit saved a new row), so only the same code on the same date is a duplicate.
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM PLN WHERE PLNCode = @planCode AND PLNEffDate = @effective", new { planCode, effective = shared["effective"] }, ct) > 0)
                throw BusinessRuleException.Conflict($"Plan '{planCode}' already has a version effective on that date.");
            var row = new Dictionary<string, object?>(shared, StringComparer.OrdinalIgnoreCase) { ["code"] = planCode, ["description"] = string.IsNullOrEmpty(p.Description) ? null : p.Description };
            created.Add(await InsertAsync(session, def, row, user, ct));
        }
        await session.CommitAsync(ct);

        foreach (var row in created) audit.Record(new AuditEntry("Lookup.Create", def.Table, row.Key, user, new Dictionary<string, object?> { ["values"] = row.Values, ["batch"] = created.Count }));
        return created;
    }

    /// <summary>Legacy GeneratePLNSeq: plan codes of health type N.</summary>
    private static readonly LookupCodeSequence PlanCodes = new("PLNSeq", "PLNSeqCode", "PLNNoSeq", "group", "code");

    /// <summary>
    /// Legacy GeneratePLNSeq / GenerateCoGrpSeq: the first character of the source field plus a three-digit running number kept per character.
    /// The counter row is locked until the caller commits, so two users cannot get the same code.
    /// </summary>
    private static async Task<string> NextCodeAsync(IDbSession session, LookupDefinition def, LookupCodeSequence seq, string source, CancellationToken ct)
    {
        var letter = char.ToUpperInvariant(source.TrimStart()[0]);
        var sourceLabel = def.Fields.First(f => f.Key == seq.SourceField).Label;
        if (!char.IsAsciiLetterOrDigit(letter))
            throw new BusinessRuleException($"The {sourceLabel.ToLowerInvariant()} must start with a letter or digit: its first character prefixes the generated codes.", field: seq.SourceField);
        var prefix = letter.ToString();
        var last = await session.ScalarAsync<string?>($"SELECT {seq.CounterColumn} FROM {seq.Table} WITH (UPDLOCK, HOLDLOCK) WHERE {seq.PrefixColumn} = @prefix", new { prefix }, ct);
        int next;
        if (last is null)
        {
            next = 1;
            await session.ExecuteAsync($"INSERT INTO {seq.Table} ({seq.PrefixColumn}, {seq.CounterColumn}) VALUES (@prefix, '001')", new { prefix }, ct);
        }
        else
        {
            next = int.TryParse(last.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n + 1 : throw new InvalidOperationException($"{seq.Table} for '{prefix}' holds '{last}', not a number.");
            if (next > 999) throw BusinessRuleException.Conflict($"All codes starting with '{prefix}' (up to {prefix}999) are used.");
            await session.ExecuteAsync($"UPDATE {seq.Table} SET {seq.CounterColumn} = @seq WHERE {seq.PrefixColumn} = @prefix", new { prefix, seq = next.ToString("000", CultureInfo.InvariantCulture) }, ct);
        }
        return prefix + next.ToString("000", CultureInfo.InvariantCulture);
    }

    public async Task<LookupRow> UpdateAsync(LookupDefinition def, string key, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        key = KeyOf(def, key);
        var keyParts = key.Split('|');
        for (var i = 0; i < def.KeyFields.Count; i++)
            if (values.TryGetValue(def.KeyFields[i].Key, out var sent) && !string.Equals(sent?.Trim(), keyParts[i], StringComparison.Ordinal))
                throw new BusinessRuleException($"The {def.KeyFields[i].Label.ToLowerInvariant()} cannot be changed.", field: def.KeyFields[i].Key);
        var clean = Validate(def, values, includeKey: false);

        await using var session = await Db.BeginSessionAsync(def.Database, ct);
        var before = await FindAsync(session, def, key, ct) ?? throw BusinessRuleException.NotFound($"'{key}' does not exist.");
        await CheckSourcesAsync(def, clean, before, ct);

        var args = new Dictionary<string, object?> { ["key"] = key };
        var sets = new List<string>();
        foreach (var f in def.Fields.Where(f => !f.IsKey)) { sets.Add($"{f.Column} = @v_{f.Key}"); args["v_" + f.Key] = clean[f.Key]; }
        if (def.UpdatedByColumn is not null) { sets.Add($"{def.UpdatedByColumn} = @by"); args["by"] = user; }
        if (def.UpdatedDateColumn is not null) { sets.Add($"{def.UpdatedDateColumn} = @on"); args["on"] = DateTime.Today; }
        var (keyWhere, keyArgs) = KeyWhere(def, key);
        foreach (var p in keyArgs) args[p.Key] = p.Value;
        await session.ExecuteAsync($"UPDATE {def.Table} SET {string.Join(", ", sets)} WHERE {keyWhere}", args, ct);
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
        var (delWhere, delArgs) = KeyWhere(def, key);
        await session.ExecuteAsync($"DELETE FROM {def.Table} WHERE {delWhere}", delArgs, ct);
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Lookup.Delete", def.Table, key, user, new Dictionary<string, object?> { ["before"] = before.Values }));
    }
}


