using GenesisFIS.Core.Data;

namespace GenesisFIS.Core.Maintenance;

public enum LookupFieldKind { Text, Money, Choice, Date, Integer }

/// <summary>
/// One editable column. The key field identifies the row and cannot be changed after creation. Optional fields are stored as NULL when left empty
/// (as the legacy forms did). <see cref="Choices"/> restricts a Choice field to the listed values (<see cref="ChoiceLabels"/>, same order, are shown
/// next to them); <see cref="Source"/> instead fills the drop-down from a code table and refuses codes that are not in it. Money fields hold a
/// non-negative amount. <see cref="RequiredWhen"/> makes an optional field mandatory while another field holds the given value.
/// </summary>
public sealed record LookupField(string Key, string Column, string Label, int MaxLength, bool Required = false, bool IsKey = false,
    LookupFieldKind Kind = LookupFieldKind.Text, IReadOnlyList<string>? Choices = null, bool Multiline = false,
    IReadOnlyList<string>? ChoiceLabels = null, LookupSource? Source = null, LookupCondition? RequiredWhen = null);

/// <summary>
/// Where a drop-down's values come from: the code column (and optional description column) of a table, as the legacy combo boxes were filled.
/// <see cref="Filter"/> is a fixed, trusted catalog condition (never user input).
/// </summary>
public sealed record LookupSource(GenesisDb Database, string Table, string CodeColumn, string? LabelColumn = null, string? Filter = null);

public sealed record LookupCondition(string Field, string Value);

public sealed record LookupOption(string Value, string? Label);

/// <summary>A place that points at a lookup row; deleting a referenced row is refused.</summary>
public sealed record LookupReference(GenesisDb Database, string Table, string Column, string Description);

/// <summary>
/// A simple maintained table (code + descriptive columns). Adding a definition gives a full list / add / edit / delete screen with validation,
/// duplicate and reference checks, legacy update stamps and audit. Column and table names are trusted catalog text, checked against the real schema by tests.
/// </summary>
public sealed record LookupDefinition(
    string Id, string Title, string Module, int ScreenPosition, GenesisDb Database, string Table, string OrderBy,
    IReadOnlyList<LookupField> Fields, IReadOnlyList<LookupReference> References,
    string? UpdatedByColumn = null, string? UpdatedDateColumn = null, bool AllowDelete = true, bool KeyIsIdentity = false,
    LookupCodeSequence? CodeSequence = null, string? UniqueField = null)
{
    public const int MaxRows = 2000;
    public const decimal MaxMoney = 922337203685477m;
    public LookupField KeyField => Fields.First(f => f.IsKey);

    /// <summary>All key columns in order; a table with more than one (composite key) uses "a|b|c" as the row key.</summary>
    public IReadOnlyList<LookupField> KeyFields => Fields.Where(f => f.IsKey).ToList();
}

/// <summary>
/// A code the legacy form generated instead of letting the user type it (GeneratePLNSeq, GenerateCoGrpSeq): the first character of
/// <see cref="SourceField"/> plus a three-digit running number kept per character in <see cref="Table"/>. <see cref="CodeField"/> receives it.
/// </summary>
public sealed record LookupCodeSequence(string Table, string PrefixColumn, string CounterColumn, string SourceField, string CodeField);

/// <summary>
/// Plan Code tab, legacy "No of Plan Code": one save creates up to six plans that share every field except code and description.
/// For health type N the codes are generated (<see cref="Count"/> plans; <see cref="Plans"/> only carries descriptions); for other health types
/// each entry of <see cref="Plans"/> with a code becomes a plan.
/// </summary>
public sealed record PlanBatch(IReadOnlyDictionary<string, string?> Values, int Count, IReadOnlyList<PlanBatchEntry> Plans)
{
    public const int MaxPlans = 6;
    public const string GeneratedHealthType = "N";
}

public sealed record PlanBatchEntry(string? Code, string? Description);

public sealed record LookupRow(string Key, IReadOnlyDictionary<string, string?> Values);

public interface ILookupRepository
{
    Task<IReadOnlyList<LookupRow>> ListAsync(LookupDefinition def, string? search, CancellationToken ct = default);
    Task<LookupRow> CreateAsync(LookupDefinition def, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default);
    Task<LookupRow> UpdateAsync(LookupDefinition def, string key, IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct = default);
    Task DeleteAsync(LookupDefinition def, string key, string user, CancellationToken ct = default);
    Task<IReadOnlyList<LookupRow>> CreatePlansAsync(LookupDefinition def, PlanBatch batch, string user, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> OptionsAsync(LookupSource source, CancellationToken ct = default);
}



