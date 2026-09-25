using Testcontainers.MsSql;
using Xunit;

namespace DocHub.Testing.Database;

/// <summary>
/// One SQL Server 2022 container per test assembly. Register with
/// <c>[assembly: AssemblyFixture(typeof(SqlServerContainerFixture))]</c>.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    /// <summary>Connection string to the <c>master</c> database of the container.</summary>
    public string MasterConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);
}
