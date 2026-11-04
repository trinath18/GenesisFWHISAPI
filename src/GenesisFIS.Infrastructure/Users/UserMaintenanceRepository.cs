using GenesisFIS.Core.Data;
using GenesisFIS.Core.Security;
using GenesisFIS.Core.Users;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Users;

/// <summary>
/// Maintenance > Security > User Permission (legacy frmUserPermissionMaintenance). Passwords are stored with the legacy cipher (USR.USRPassword) so the old
/// system and this one keep accepting them. Legacy differences: a password is only changed when one is typed (legacy re-saved the one in the box); a user
/// cannot suspend or delete themself or take away their own User Permission access, so nobody locks themself out; deletes are audited.
/// </summary>
internal sealed class UserMaintenanceRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IUserMaintenanceRepository
{
    /// <summary>Legacy USRAccess position of this screen (mnuPermission_Click: CheckAccess(112)).</summary>
    private const int Screen = 112;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<UserMaintenanceOptions> OptionsAsync(CancellationToken ct = default)
    {
        var groups = (await QueryAsync(GenesisDb.Maintenance, "SELECT GRPCode, GRPDescription FROM GRP ORDER BY GRPCode", null, ct))
            .Select(r => new UserOption(S(r, "GRPCode")!, S(r, "GRPDescription") ?? "")).ToList();
        var providers = (await QueryAsync(GenesisDb.Main, "SELECT SRVCode, SRVName FROM SRV ORDER BY SRVCode", null, ct))
            .Select(r => new UserOption(S(r, "SRVCode")!, S(r, "SRVName") ?? "")).ToList();
        return new UserMaintenanceOptions(groups, providers, UserMaintenanceRules.Positions);
    }

    public async Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken ct = default)
    {
        var term = Blank(search);
        var rows = await QueryAsync(GenesisDb.Maintenance, $"""
            SELECT USRCode, USRName, USRLGCode, GRPCode, SRVCode, USRStatus, AuthorisedStatus FROM USR
            {(term is null ? "" : "WHERE USRCode LIKE @like OR USRName LIKE @like")} ORDER BY USRCode
            """, new { like = term is null ? null : "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%" }, ct);
        return rows.Select(r => new UserSummary(S(r, "USRCode")!, S(r, "USRName") ?? "", S(r, "USRLGCode"), S(r, "GRPCode"), S(r, "SRVCode"), S(r, "USRStatus"), S(r, "AuthorisedStatus"))).ToList();
    }

    private const string DetailSql = "SELECT USRCode, USRName, USRLGCode, USRDateCreated, USRStatus, AuthorisedStatus, GRPCode, SRVCode, USRAccess, USRLogonCHGStatus FROM USR";

    private static UserDetail Detail(IDictionary<string, object?> r) =>
        new(S(r, "USRCode")!, S(r, "USRName") ?? "", S(r, "USRLGCode"), D(r, "USRDateCreated") is { } d ? DateOnly.FromDateTime(d) : null, S(r, "USRStatus"), S(r, "AuthorisedStatus"),
            S(r, "GRPCode"), S(r, "SRVCode"), Get(r, "USRAccess") as string ?? "", S(r, "USRLogonCHGStatus") is "1" or "Y");

    public async Task<UserDetail?> GetAsync(string code, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Maintenance, $"{DetailSql} WHERE USRCode = @code", new { code = code.Trim() }, ct)).Select(Detail).FirstOrDefault();

    public async Task<UserDetail> SaveAsync(UserSaveRequest r, bool isNew, string user, CancellationToken ct = default)
    {
        if (UserMaintenanceRules.Validate(r, isNew) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var code = r.Code.Trim();
        var group = r.Group!.Trim(); var provider = r.ServiceProvider!.Trim(); var status = r.Status!.Trim();
        if (await ScalarAsync<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM GRP WHERE GRPCode = @group", new { group }, ct) == 0)
            throw new BusinessRuleException($"Department {group} not found.", field: "group");
        if (await ScalarAsync<int>(GenesisDb.Main, "SELECT COUNT(*) FROM SRV WHERE SRVCode = @provider", new { provider }, ct) == 0)
            throw new BusinessRuleException($"Service provider {provider} not found.", field: "serviceProvider");

        await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        var current = (await maint.QueryAsync($"{DetailSql} WITH (UPDLOCK) WHERE USRCode = @code", new { code }, ct)).Select(Detail).FirstOrDefault();
        if (isNew && current is not null) throw BusinessRuleException.Conflict("Record Duplicated! Please Change The Corresponding Field");
        if (!isNew && current is null) throw BusinessRuleException.NotFound($"User {code} not found.");
        var access = UserMaintenanceRules.MergeAccess(r.Access, current?.Access);
        if (string.Equals(code, user, StringComparison.OrdinalIgnoreCase))
        {
            if (status != "A") throw new BusinessRuleException("You cannot suspend yourself.", field: "status");
            if (!new AccessString(access).IsAllowed(Screen)) throw new BusinessRuleException("You cannot take away your own User Permission access.", field: "access");
        }
        var args = new
        {
            code, name = r.Name!.Trim(), lg = Blank(r.LgCode), password = string.IsNullOrEmpty(r.Password) ? null : LegacyPasswordCipher.Encrypt(r.Password), today = DateTime.Today,
            status, authorised = r.Authorised!.Trim(), group, provider, access,
        };
        if (isNew)
            await maint.ExecuteAsync("""
                INSERT INTO USR (USRCode, USRLGCode, USRName, USRPassword, USRDateCreated, USRStatus, AuthorisedStatus, GRPCode, SRVCode, USRAccess)
                VALUES (@code, @lg, @name, @password, @today, @status, @authorised, @group, @provider, @access)
                """, args, ct);
        else
            await maint.ExecuteAsync("""
                UPDATE USR SET USRLGCode = @lg, USRName = @name, USRPassword = COALESCE(@password, USRPassword), USRStatus = @status, AuthorisedStatus = @authorised,
                    GRPCode = @group, SRVCode = @provider, USRAccess = @access
                WHERE USRCode = @code
                """, args, ct);
        await maint.CommitAsync(ct);

        var before = new AccessString(current?.Access ?? "");
        var after = new AccessString(access);
        audit.Record(new AuditEntry(isNew ? "User.Create" : "User.Update", "USR", code, user, new Dictionary<string, object?>
        {
            ["name"] = args.name, ["status"] = status, ["authorised"] = args.authorised, ["group"] = group, ["serviceProvider"] = provider, ["passwordChanged"] = args.password is not null,
            ["granted"] = UserMaintenanceRules.Positions.Where(p => after.IsAllowed(p.Position) && !before.IsAllowed(p.Position)).Select(p => p.Position).ToList(),
            ["revoked"] = UserMaintenanceRules.Positions.Where(p => before.IsAllowed(p.Position) && !after.IsAllowed(p.Position)).Select(p => p.Position).ToList(),
        }));
        return (await GetAsync(code, ct))!;
    }

    /// <summary>Legacy cmdDelete_Click: the user is archived (FWHSHISARC.USRArchive) and deleted from USR.</summary>
    public async Task DeleteAsync(string code, string user, CancellationToken ct = default)
    {
        code = code.Trim();
        if (string.Equals(code, user, StringComparison.OrdinalIgnoreCase)) throw new BusinessRuleException("You cannot delete yourself.", field: "code");
        await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        await using var arc = await Db.BeginSessionAsync(GenesisDb.Archive, ct);
        var row = (await maint.QueryAsync("SELECT USRCode, USRName FROM USR WITH (UPDLOCK) WHERE USRCode = @code", new { code }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound($"User {code} not found.");
        await arc.ExecuteAsync("INSERT INTO USRArchive (USRCode, USRName, USRLastUpdateDate, USRLastUpdateUser) VALUES (@code, @name, @today, @who)",
            new { code, name = S(row, "USRName"), today = DateTime.Today, who = user.Length > 10 ? user[..10] : user }, ct);
        await maint.ExecuteAsync("DELETE FROM USR WHERE USRCode = @code", new { code }, ct);
        await arc.CommitAsync(ct);
        await maint.CommitAsync(ct);
        audit.Record(new AuditEntry("User.Delete", "USR", code, user, new Dictionary<string, object?> { ["name"] = S(row, "USRName") }));
    }
}
