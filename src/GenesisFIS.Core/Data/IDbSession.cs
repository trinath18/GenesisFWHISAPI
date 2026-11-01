namespace GenesisFIS.Core.Data;

/// <summary>
/// A unit of work on one database: every statement runs in the same transaction. Dispose without <see cref="CommitAsync"/> rolls back.
/// Write flows use this instead of opening connections themselves, so transactions and (in tests) rollback isolation live in one place.
/// </summary>
public interface IDbSession : IAsyncDisposable
{
    Task<IReadOnlyList<IDictionary<string, object?>>> QueryAsync(string sql, object? args = null, CancellationToken ct = default);

    /// <summary>Runs INSERT/UPDATE/DELETE and returns the number of rows affected.</summary>
    Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default);

    Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);
}
