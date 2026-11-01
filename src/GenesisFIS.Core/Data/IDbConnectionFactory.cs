using System.Data.Common;

namespace GenesisFIS.Core.Data;

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

    /// <summary>Starts a transactional unit of work for write flows. Dispose without committing to roll back.</summary>
    Task<IDbSession> BeginSessionAsync(GenesisDb db, CancellationToken ct = default);
}

