using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Users;

namespace Genesis.Infrastructure.Users;

internal sealed class UserRepository(IDbConnectionFactory db) : IUserRepository
{
    private const string Sql = """
        SELECT USRCode, USRName, USRPassword, USRStatus, DPTCode, GRPCode, USRAccess, USRLogonCHGStatus
        FROM USR
        WHERE USRCode = @code
        """;

    public async Task<UserRecord?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(GenesisDb.Maintenance, ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Sql, new { code }, cancellationToken: ct));
        return row is null
            ? null
            : new UserRecord(row.USRCode.Trim(), row.USRName.Trim(), row.USRPassword, row.USRStatus.Trim(),
                row.DPTCode?.Trim(), row.GRPCode?.Trim(), row.USRAccess, row.USRLogonCHGStatus?.Trim() == "Y");
    }

    private sealed record Row(string USRCode, string USRName, string? USRPassword, string USRStatus,
        string? DPTCode, string? GRPCode, string? USRAccess, string? USRLogonCHGStatus);
}
