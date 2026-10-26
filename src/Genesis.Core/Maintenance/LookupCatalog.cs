using Genesis.Core.Data;

namespace Genesis.Core.Maintenance;

/// <summary>Maintained code tables. Positions are the legacy USRAccess flags of the menu items (see docs/inventory/menu_map.csv).</summary>
public static class LookupCatalog
{
    public static readonly IReadOnlyList<LookupDefinition> All =
    [
        // Legacy: Maintenance > Security > Department (frmGroupMaintenance). Users point at the group code, so a used group cannot be deleted.
        new("department", "Department (User Group)", "Maintenance", 111, GenesisDb.Maintenance, "GRP", "GRPCode",
            [new("code", "GRPCode", "Group code", 4, Required: true, IsKey: true), new("description", "GRPDescription", "Description", 255, Required: true)],
            [new(GenesisDb.Maintenance, "USR", "GRPCode", "users")]),
    ];

    public static LookupDefinition? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}

