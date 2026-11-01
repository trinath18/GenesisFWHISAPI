using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net.Http.Headers;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Users;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenesisFIS.Tests;

/// <summary>
/// Connection factory for write tests: every database gets ONE connection with an open outer transaction. Reads and writes (including sessions,
/// which use savepoints) share it, so tests see their own changes, and disposing the factory rolls everything back. Nothing is ever committed.
/// </summary>
public sealed class RollbackConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly IConfiguration _config = TestHost.Config();
    private readonly ConcurrentDictionary<GenesisDb, Lazy<(SqlConnection Connection, SqlTransaction Transaction)>> _open = new();

    private (SqlConnection Connection, SqlTransaction Transaction) Get(GenesisDb db) => _open.GetOrAdd(db, key => new(() =>
    {
        var connection = new SqlConnection(_config[$"ConnectionStrings:{key}"] ?? throw new InvalidOperationException($"Connection string '{key}' missing."));
        connection.Open();
        return (connection, connection.BeginTransaction());
    })).Value;

    public Task<DbConnection> OpenAsync(GenesisDb db, CancellationToken ct = default) => Task.FromResult<DbConnection>(new SharedConnection(Get(db).Connection, Get(db).Transaction));

    public async Task<IDbSession> BeginSessionAsync(GenesisDb db, CancellationToken ct = default)
    {
        var (connection, transaction) = Get(db);
        return await DbSession.BeginSavepointAsync(connection, transaction, ct);
    }

    public ValueTask DisposeAsync() => new(ResetAsync());

    /// <summary>Rolls back everything done so far and releases the locks. Call after each test.</summary>
    public async Task ResetAsync()
    {
        foreach (var lazy in _open.Values.Where(l => l.IsValueCreated))
        {
            var (connection, transaction) = lazy.Value;
            await transaction.RollbackAsync();
            await connection.DisposeAsync();
        }
        _open.Clear();
    }

    /// <summary>A connection that hands every command the shared outer transaction and ignores Close/Dispose.</summary>
    private sealed class SharedConnection(SqlConnection inner, SqlTransaction transaction) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => inner.ConnectionString; set { } }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException("Use IDbConnectionFactory.BeginSessionAsync in write flows.");
        protected override DbCommand CreateDbCommand()
        {
            var command = inner.CreateCommand();
            command.Transaction = transaction;
            return command;
        }
        protected override void Dispose(bool disposing) { }
    }
}

public sealed class CapturingAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    public IReadOnlyList<AuditEntry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public void Record(AuditEntry entry) { lock (_entries) _entries.Add(entry); }
    public void Clear() { lock (_entries) _entries.Clear(); }
}

/// <summary>API host for write-flow tests: all database work goes through <see cref="RollbackConnectionFactory"/> and audit entries are captured.</summary>
public sealed class WriteApiFactory : WebApplicationFactory<Program>
{
    public const string TestUser = "TEST";
    public RollbackConnectionFactory Db { get; } = new();
    public CapturingAuditLog Audit { get; } = new();

    public async Task ResetAsync()
    {
        await Db.ResetAsync();
        Audit.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = ApiFactory.JwtKey }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbConnectionFactory>();
            services.AddSingleton<IDbConnectionFactory>(Db);
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog>(Audit);
        });
    }

    public HttpClient ClientFor(string access)
    {
        var tokens = Services.GetRequiredService<TokenService>();
        var (token, _) = tokens.Create(new UserRecord(TestUser, "Test User", null, "A", "ITD", "ITD", access, false));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Access string with only the given 1-based positions allowed.</summary>
    public static string Access(params int[] positions)
    {
        var chars = new string('0', 200).ToCharArray();
        foreach (var p in positions) chars[p - 1] = '1';
        return new string(chars);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Db.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

