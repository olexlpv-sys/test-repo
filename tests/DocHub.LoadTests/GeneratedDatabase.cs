using System.Globalization;
using DocHub.DataGen;
using DocHub.Testing.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace DocHub.LoadTests;

/// <summary>
/// A freshly deployed database filled by the T19 generator (scale from <c>DOCHUB_LOAD_SCALE</c>, else <see cref="DefaultScale"/>),
/// with the API hosted against it through a SQL login in <c>app_api</c> — like production.
/// </summary>
public abstract class GeneratedDatabase(SqlServerContainerFixture server) : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _login = $"dochub_load_{Guid.NewGuid():N}";
    private readonly string _password = $"Pw_{Guid.NewGuid():N}!Aa1";

    public string DatabaseName { get; } = $"DocHub_Load_{Guid.NewGuid():N}";

    protected abstract double DefaultScale { get; }

    public double Scale =>
        double.TryParse(Environment.GetEnvironmentVariable("DOCHUB_LOAD_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0 ? scale : DefaultScale;

    public DataGenReport Report { get; private set; } = null!;

    public string AdminConnectionString => DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, DatabaseName);

    public string ApiConnectionString => new SqlConnectionStringBuilder(server.MasterConnectionString)
    {
        InitialCatalog = DatabaseName,
        UserID = _login,
        Password = _password,
    }.ConnectionString;

    public virtual async ValueTask InitializeAsync()
    {
        DacpacDeployer.Deploy(server.MasterConnectionString, DatabaseName);
        Report = await new DataGenerator(AdminConnectionString, new DataGenOptions { Scale = Scale }, message => TestContext.Current.SendDiagnosticMessage(message))
            .RunAsync(TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            CREATE LOGIN [{_login}] WITH PASSWORD = N'{_password}', CHECK_POLICY = OFF;
            CREATE USER [{_login}] FOR LOGIN [{_login}];
            ALTER ROLE [app_api] ADD MEMBER [{_login}];
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public HttpClient ClientFor(int userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString(CultureInfo.InvariantCulture));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DocHub", ApiConnectionString);
        builder.UseSetting("Audit:Reconciliation:Enabled", "false");
        builder.UseSetting("Export:Worker:Enabled", "false");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
