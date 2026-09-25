using DocHub.Api.Endpoints;
using DocHub.Testing.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>
/// The API against its own freshly deployed database (one per test class). The API connects with a SQL login that is a
/// member of <c>app_api</c> — exactly like production — so grants and audit (<c>Source = 'App'</c>) are exercised for real.
/// </summary>
public class DocHubApiFactory(SqlServerContainerFixture server) : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _login = $"dochub_api_{Guid.NewGuid():N}";
    private readonly string _password = $"Pw_{Guid.NewGuid():N}!Aa1";

    public string DatabaseName { get; } = $"DocHub_Api_{Guid.NewGuid():N}";

    /// <summary>Connection as the container's administrator — for arranging data and asserting on the database.</summary>
    public string AdminConnectionString => DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, DatabaseName);

    /// <summary>The connection the API uses (member of app_api, pooling on as in production).</summary>
    public string ApiConnectionString => new SqlConnectionStringBuilder(server.MasterConnectionString)
    {
        InitialCatalog = DatabaseName,
        UserID = _login,
        Password = _password,
    }.ConnectionString;

    public string EnvironmentName { get; init; } = "Development";

    /// <summary>Extra endpoint modules only the tests use (see <see cref="TestEndpoints"/>).</summary>
    protected virtual IEnumerable<IEndpointModule> AdditionalModules => [];

    public async ValueTask InitializeAsync()
    {
        DacpacDeployer.Deploy(server.MasterConnectionString, DatabaseName);
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"""
            CREATE LOGIN [{_login}] WITH PASSWORD = N'{_password}', CHECK_POLICY = OFF;
            CREATE USER [{_login}] FOR LOGIN [{_login}];
            ALTER ROLE [app_api] ADD MEMBER [{_login}];
            """, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public HttpClient CreateClientFor(int? userId)
    {
        var client = CreateClient();
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add("X-User-Id", userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("ConnectionStrings:DocHub", ApiConnectionString);
        builder.ConfigureTestServices(services =>
        {
            foreach (var module in AdditionalModules)
            {
                services.AddSingleton(module);
            }
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>The API plus the test-only endpoints of <see cref="TestEndpoints"/>.</summary>
public sealed class DocHubApiWithTestEndpointsFactory(SqlServerContainerFixture server) : DocHubApiFactory(server)
{
    protected override IEnumerable<IEndpointModule> AdditionalModules => [new TestEndpoints()];
}
