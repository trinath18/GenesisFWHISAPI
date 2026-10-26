using System.Data.Common;
using Dapper;
using Genesis.Core.Data;

namespace Genesis.Infrastructure.Data;

/// <summary>
/// Dapper-backed unit of work. Normally owns its connection and a real transaction. For tests it can run on a shared connection inside
/// an outer transaction using a savepoint (commit is then a no-op and a rollback goes back to the savepoint), so tests never change data.
/// </summary>
public sealed class DbSession : IDbSession
{
    private const int TimeoutSeconds = 120;
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly string? _savepoint;
    private readonly bool _ownsConnection;
    private bool _finished;

    private DbSession(DbConnection connection, DbTransaction transaction, string? savepoint, bool ownsConnection)
    {
        _connection = connection;
        _transaction = transaction;
        _savepoint = savepoint;
        _ownsConnection = ownsConnection;
    }

    public static async Task<DbSession> BeginAsync(DbConnection connection, CancellationToken ct = default) =>
        new(connection, await connection.BeginTransactionAsync(ct), null, true);

    /// <summary>Runs inside an existing outer transaction; only the work of this session is undone on rollback.</summary>
    public static async Task<DbSession> BeginSavepointAsync(DbConnection connection, DbTransaction outer, CancellationToken ct = default)
    {
        var name = "sp_" + Guid.NewGuid().ToString("N")[..12];
        await connection.ExecuteAsync(new CommandDefinition($"SAVE TRANSACTION {name}", transaction: outer, cancellationToken: ct));
        return new DbSession(connection, outer, name, false);
    }

    private CommandDefinition Command(string sql, object? args, CancellationToken ct) =>
        new(sql, args, _transaction, TimeoutSeconds, cancellationToken: ct);

    public async Task<IReadOnlyList<IDictionary<string, object?>>> QueryAsync(string sql, object? args = null, CancellationToken ct = default) =>
        (await _connection.QueryAsync(Command(sql, args, ct))).Select(r => (IDictionary<string, object?>)r).ToList();

    public Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default) =>
        _connection.ExecuteAsync(Command(sql, args, ct));

    public Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default) =>
        _connection.ExecuteScalarAsync<T?>(Command(sql, args, ct));

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_finished) throw new InvalidOperationException("The session is already finished.");
        _finished = true;
        if (_savepoint is null) await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            _finished = true;
            if (_savepoint is null) await _transaction.RollbackAsync();
            else await _connection.ExecuteAsync($"ROLLBACK TRANSACTION {_savepoint}", transaction: _transaction);
        }
        if (_ownsConnection)
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
