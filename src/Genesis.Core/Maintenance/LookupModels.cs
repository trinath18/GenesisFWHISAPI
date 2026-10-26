using Genesis.Core.Data;

namespace Genesis.Core.Maintenance;

/// <summary>One editable column. The key field identifies the row and cannot be changed after creation.</summary>
public sealed record LookupField(string Key, string Column, string Label, int MaxLength, bool Required = false, bool IsKey = false);

/// <summary>A place that points at a lookup row; deleting a referenced row is refused.</summary>
public sealed record LookupReference(GenesisDb Database, string Table, string Column, string Description);

/// <summary>
/// A simple maintained table (code + descriptive columns). Adding a definition gives a full list / add / edit / delete screen with validation,
/// duplicate and reference checks, legacy update stamps and audit. Column and table names are trusted catalog text, checked against the real schema by tests.
/// </summary>
public sealed record LookupDefinition(
    string Id, string Title, string Module, int ScreenPosition, GenesisDb Database, string Table, string OrderBy,
    IReadOnlyList<LookupField> Fields, IReadOnlyList<LookupReference> References,
    string? UpdatedByColumn = null, string? UpdatedDateColumn = null, bool AllowDelete = true)
{
    public const int MaxRows = 2000;
    public LookupField KeyField => Fields.Single(f => f.IsKey);
}

public sealed record LookupRow(string Key, IReadOnlyDictionary<string, string?> Values);

public interface ILookupRepository
{
    Task<IReadOnlyList<LookupRow>> ListAsync(LookupDefinition def, string? search, CancellationToken ct = default);
    Task<LookupRow> CreateAsync(LookupDefinition def, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default);
    Task<LookupRow> UpdateAsync(LookupDefinition def, string key, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default);
    Task DeleteAsync(LookupDefinition def, string key, string user, CancellationToken ct = default);
}
