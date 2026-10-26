using System.Data.Common;
using Genesis.Core.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Genesis.Infrastructure.Data;

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
}
