using System.Data.Common;

namespace Genesis.Core.Data;

public enum GenesisDb
{
    Main,
    Maintenance,
    Worksheet,
    Payment,
    Archive
}

public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync(GenesisDb db, CancellationToken ct = default);
}
