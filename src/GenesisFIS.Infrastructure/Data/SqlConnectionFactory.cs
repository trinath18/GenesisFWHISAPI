using System.Data.Common;
using GenesisFIS.Core.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace GenesisFIS.Infrastructure.Data;

internal sealed class SqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(GenesisDb db, CancellationToken ct = default)
    {
        var cs = configuration.GetConnectionString(db.ToString())
                 ?? throw new InvalidOperationException($"Connection string '{db}' is not configured.");
        var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<IDbSession> BeginSessionAsync(GenesisDb db, CancellationToken ct = default) =>
        await DbSession.BeginAsync(await OpenAsync(db, ct), ct);
}

