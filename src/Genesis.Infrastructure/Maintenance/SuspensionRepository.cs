using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class SuspensionRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ISuspensionRepository
{
    private const string Columns = "s.SuspensionCode, s.MembershipNo, s.AgentCode, s.Remarks, s.PayorCode, p.PAYCompanyName, s.LastUpdateUser, s.LastUpdateDate";
    private const string From = "SuspensionDetail s LEFT JOIN PAY p ON p.PAYCode = s.PayorCode";

    private static string Like(string v) => v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static Suspension Map(IDictionary<string, object?> r)
    {
        var kind = S(r, "SuspensionCode") ?? "";
        return new Suspension(kind, (kind == SuspensionKinds.Member ? S(r, "MembershipNo") : S(r, "AgentCode")) ?? "", S(r, "Remarks"), S(r, "PayorCode"),
            S(r, "PAYCompanyName"), S(r, "LastUpdateUser"), D(r, "LastUpdateDate") is { } d ? DateOnly.FromDateTime(d) : null);
    }

    private static string CodeColumn(string kind) => kind == SuspensionKinds.Member ? "MembershipNo" : "AgentCode";

    public async Task<IReadOnlyList<Payor>> ListPayorsAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY ORDER BY PAYCode", null, ct)).Select(r => new Payor(S(r, "PAYCode") ?? "", S(r, "PAYCompanyName"))).ToList();

    public async Task<IReadOnlyList<Suspension>> SearchAsync(string? kind, string? code, string? payorCode, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !SuspensionKinds.IsValid(kind)) throw new BusinessRuleException("Unknown suspension type.", field: "kind");
        var like = string.IsNullOrWhiteSpace(code) ? null : Like(code.Trim()) + "%";
        var sql = $"""
            SELECT TOP ({SuspensionRules.SearchLimit}) {Columns} FROM {From}
            WHERE (@kind IS NULL OR s.SuspensionCode = @kind)
              AND (@like IS NULL OR s.MembershipNo LIKE @like OR s.AgentCode LIKE @like)
              AND (@payor IS NULL OR s.PayorCode = @payor)
            ORDER BY s.SuspensionCode, s.SUSIndex
            """;
        return (await QueryAsync(GenesisDb.Main, sql, new { kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim(), like, payor = string.IsNullOrWhiteSpace(payorCode) ? null : payorCode.Trim() }, ct))
            .Select(Map).ToList();
    }

    private static async Task<Suspension?> FindAsync(IDbSession session, string kind, string code, CancellationToken ct) =>
        (await session.QueryAsync($"SELECT {Columns} FROM {From} WHERE s.SuspensionCode = @kind AND s.{CodeColumn(kind)} = @code", new { kind, code }, ct)).Select(Map).FirstOrDefault();

    private static async Task EnsurePayorAsync(IDbSession session, string payor, CancellationToken ct)
    {
        if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM PAY WHERE PAYCode = @payor", new { payor }, ct) == 0)
            throw new BusinessRuleException($"Payor '{payor}' does not exist.", RuleFailure.NotFound, "payorCode");
    }

    public async Task<Suspension> CreateAsync(SuspensionRequest request, string user, CancellationToken ct = default)
    {
        if (SuspensionRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var (kind, code, payor) = (request.Kind!, request.Code!.Trim(), request.PayorCode!.Trim());

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await EnsurePayorAsync(session, payor, ct);
        if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM SuspensionCat WHERE SuspensionCode = @kind", new { kind }, ct) == 0)
            throw new BusinessRuleException($"Suspension category '{kind}' does not exist.", RuleFailure.NotFound, "kind");
        if (await FindAsync(session, kind, code, ct) is not null)
            throw BusinessRuleException.Conflict($"'{code}' is already suspended. Edit the existing record instead.");

        var column = CodeColumn(kind);
        await session.ExecuteAsync($"""
            INSERT INTO SuspensionDetail (SuspensionCode, {column}, Remarks, PayorCode, LastUpdateUser, LastUpdateDate)
            VALUES (@kind, @code, @remarks, @payor, @user, @today)
            """, new { kind, code, remarks = request.Remarks!.Trim(), payor, user, today = DateTime.Today }, ct);
        var created = await FindAsync(session, kind, code, ct) ?? throw new InvalidOperationException("The new record could not be read back.");
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Suspension.Create", "SuspensionDetail", $"{kind}:{code}", user, new Dictionary<string, object?> { ["values"] = created }));
        return created;
    }

    public async Task<Suspension> UpdateAsync(SuspensionRequest request, string user, CancellationToken ct = default)
    {
        if (SuspensionRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var (kind, code, payor) = (request.Kind!, request.Code!.Trim(), request.PayorCode!.Trim());

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var before = await FindAsync(session, kind, code, ct) ?? throw BusinessRuleException.NotFound($"'{code}' is not suspended.");
        await EnsurePayorAsync(session, payor, ct);
        await session.ExecuteAsync($"UPDATE SuspensionDetail SET Remarks = @remarks, PayorCode = @payor, LastUpdateUser = @user, LastUpdateDate = @today WHERE SuspensionCode = @kind AND {CodeColumn(kind)} = @code",
            new { kind, code, remarks = request.Remarks!.Trim(), payor, user, today = DateTime.Today }, ct);
        var after = await FindAsync(session, kind, code, ct) ?? throw new InvalidOperationException("The record could not be read back.");
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Suspension.Update", "SuspensionDetail", $"{kind}:{code}", user, new Dictionary<string, object?> { ["before"] = before, ["after"] = after }));
        return after;
    }

    public async Task DeleteAsync(string kind, string code, string user, CancellationToken ct = default)
    {
        if (!SuspensionKinds.IsValid(kind)) throw new BusinessRuleException("Unknown suspension type.", field: "kind");
        code = code.Trim();
        if (code.Length == 0 || code.Length > SuspensionRules.MaxMemberNumber) throw new BusinessRuleException("Please enter the member number or agent code.", field: "code");

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var before = await FindAsync(session, kind, code, ct) ?? throw BusinessRuleException.NotFound($"'{code}' is not suspended.");
        await session.ExecuteAsync($"DELETE FROM SuspensionDetail WHERE SuspensionCode = @kind AND {CodeColumn(kind)} = @code", new { kind, code }, ct);
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("Suspension.Delete", "SuspensionDetail", $"{kind}:{code}", user, new Dictionary<string, object?> { ["before"] = before }));
    }
}
