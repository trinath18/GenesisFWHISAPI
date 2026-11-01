using System.Data;
using Dapper;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Infrastructure.Data;

/// <summary>
/// Shared plumbing for every repository (read and, later, write flows): opens the right database and runs parameterised
/// queries or stored procedures. Rows come back as dictionaries so callers can read them with <see cref="RowReader"/>.
/// </summary>
internal abstract class SqlRepository(IDbConnectionFactory connections)
{
    protected const int DefaultTimeoutSeconds = 120;

    protected IDbConnectionFactory Db { get; } = connections;

    protected async Task<List<IDictionary<string, object?>>> QueryAsync(GenesisDb database, string sql, object? args, CancellationToken ct,
        CommandType type = CommandType.Text, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        await using var conn = await Db.OpenAsync(database, ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, args, commandType: type, commandTimeout: timeoutSeconds, cancellationToken: ct));
        return rows.Select(r => (IDictionary<string, object?>)r).ToList();
    }

    protected async Task<T?> ScalarAsync<T>(GenesisDb database, string sql, object? args, CancellationToken ct)
    {
        await using var conn = await Db.OpenAsync(database, ct);
        return await conn.ExecuteScalarAsync<T?>(new CommandDefinition(sql, args, commandTimeout: DefaultTimeoutSeconds, cancellationToken: ct));
    }
}
