using System.Text.RegularExpressions;

namespace Genesis.Core.Maintenance;

public sealed record HospitalPanel(string Code, string? Description, string? Payor, string? HealthType);

public sealed record PanelHospital(string HospitalCode, string? Name, string? Telephone, string? Fax, string? Address1, string? Address2, string? Address3, string? Town, string? State);

public sealed record AvailableHospital(string HospitalCode, string? Name, string? Telephone, string? Fax);

public sealed record PanelHospitalChange(IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove);

public sealed record PanelHospitalChangeResult(int Added, int Refreshed, int Removed);

public static partial class PanelHospitalRules
{
    public const int MaxCodesPerRequest = 1000;

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,20}$")]
    private static partial Regex HospitalCodePattern();

    public static bool IsValidHospitalCode(string? code) => code is not null && HospitalCodePattern().IsMatch(code);
}

/// <summary>Legacy form: Maintenance > Panel Hospital Maintenance (FrmMaintenancePanelHos). Tables: HosPanelCode (Maintenance DB), PanelHospitals and HOS (Main DB).</summary>
public interface IPanelHospitalRepository
{
    Task<IReadOnlyList<HospitalPanel>> ListPanelsAsync(CancellationToken ct = default);
    Task<HospitalPanel?> GetPanelAsync(string panelCode, CancellationToken ct = default);
    Task<IReadOnlyList<PanelHospital>> ListMembersAsync(string panelCode, CancellationToken ct = default);
    Task<IReadOnlyList<AvailableHospital>> ListAvailableAsync(string panelCode, string? search, CancellationToken ct = default);

    /// <summary>
    /// Adds and removes hospitals of one panel in a single transaction. Adding an existing member only refreshes its update stamp (as the legacy form did).
    /// Throws <see cref="Genesis.Core.Writes.BusinessRuleException"/> for unknown panels or hospitals and for overlapping add/remove lists.
    /// </summary>
    Task<PanelHospitalChangeResult> ApplyAsync(string panelCode, PanelHospitalChange change, string user, CancellationToken ct = default);
}
