using Microsoft.Data.SqlClient;
using Xunit;

namespace DocHub.Testing.Database;

/// <summary>
/// A freshly deployed DocHub database per test class (class fixture). Tests that change data should use
/// <see cref="OpenRolledBackTransactionAsync"/> so they don't affect each other.
/// </summary>
public sealed class DocHubDatabaseFixture(SqlServerContainerFixture server) : IAsyncLifetime
{
    public string DatabaseName { get; } = $"DocHub_{Guid.NewGuid():N}";

    public string MasterConnectionString => server.MasterConnectionString;

    public string ConnectionString => DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, DatabaseName);

    public ValueTask InitializeAsync()
    {
        DacpacDeployer.Deploy(server.MasterConnectionString, DatabaseName);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Opens a connection with a transaction that is rolled back when the scope is disposed.</summary>
    public async Task<RolledBackScope> OpenRolledBackTransactionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return new RolledBackScope(connection, transaction);
    }
}
