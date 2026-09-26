using DocHub.Testing.Database;
using Microsoft.Data.SqlClient;

namespace DocHub.Database.Tests;

/// <summary>Deployment of the DACPAC and its seed data (T02 acceptance criteria 1–2).</summary>
public sealed class DeploymentTests(SqlServerContainerFixture server)
{
    private static readonly string[] ExpectedTables =
    [
        "app.Comment", "app.ContentStyle", "app.ContentStyleUsage", "app.Document", "app.DocumentNode",
        "app.DocumentPermission", "app.DocumentVersion", "app.ExportJob", "app.Folder", "app.NodeContent", "app.NodeType",
        "app.User", "app.VersionContentHash", "app.VersionSignature", "app.VersionStamp",
        "audit.ChangeLog", "audit.ReconciliationBaseline", "audit.ReconciliationFinding",
        // History tables of the temporal + ledger tables (T21).
        "history.Comment", "history.ContentStyle", "history.Document", "history.DocumentNode", "history.DocumentPermission",
        "history.DocumentVersion", "history.ExportJob", "history.Folder", "history.NodeContent", "history.NodeType", "history.User",
        "history.VersionSignature", "history.VersionStamp",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deploy_to_empty_server_creates_all_tables_roles_and_seed_data()
    {
        var database = NewDatabaseName();

        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        await using var connection = await OpenAsync(database);
        var tables = await QueryListAsync(connection, "SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id ORDER BY 1");
        Assert.Equal(ExpectedTables.Order(StringComparer.Ordinal), tables);

        var roles = await QueryListAsync(connection, "SELECT name FROM sys.database_principals WHERE type = 'R' AND is_fixed_role = 0 AND name <> 'public' ORDER BY name");
        Assert.Equal(["app_api", "ledger_reader", "readonly", "support_writer"], roles);
        // Role-in-role membership comes from the post-deployment script (deployments exclude memberships).
        Assert.Equal(["1"], await QueryListAsync(connection, "SELECT CAST(IS_ROLEMEMBER(N'ledger_reader', N'app_api') AS NVARCHAR (1));"));

        Assert.Equal(SeedCounts.Expected, await SeedCounts.ReadAsync(connection));
    }

    [Fact]
    public async Task Redeploy_is_a_no_op_and_leaves_no_drift()
    {
        var database = NewDatabaseName();
        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        await using var connection = await OpenAsync(database);
        Assert.Equal(SeedCounts.Expected, await SeedCounts.ReadAsync(connection));
        var pending = DacpacDeployer.GetPendingOperations(server.MasterConnectionString, database);
        Assert.True(pending.Count == 0, "Drift after redeploy: " + string.Join("; ", pending));
    }

    [Fact]
    public async Task Redeploy_keeps_admin_owned_data_and_restores_seed_managed_users()
    {
        var database = NewDatabaseName();
        DacpacDeployer.Deploy(server.MasterConnectionString, database);
        await using (var connection = await OpenAsync(database))
        {
            await ExecuteAsync(connection, """
                UPDATE app.NodeType SET Name = N'Kapitel' WHERE Code = 'CHAPTER';
                DELETE FROM app.Folder WHERE Name = N'Templates';
                UPDATE app.ContentStyle SET PropertiesJson = N'{"fontSize":48}' WHERE StyleId = 'Heading1';
                UPDATE app.ContentStyle SET BasedOnStyleId = NULL WHERE StyleId = 'Heading2';
                UPDATE app.[User] SET DisplayName = N'Changed' WHERE Login = N'alice';
                INSERT INTO app.[User] (Login, DisplayName) VALUES (N'loadtest-00001', N'Generated user');
                """);
        }

        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        await using var check = await OpenAsync(database);
        Assert.Equal("Kapitel", await ScalarAsync(check, "SELECT Name FROM app.NodeType WHERE Code = 'CHAPTER'"));
        Assert.Equal(0, await ScalarAsync(check, "SELECT COUNT(*) FROM app.Folder WHERE Name = N'Templates'"));
        Assert.Equal("""{"fontSize":48}""", await ScalarAsync(check, "SELECT PropertiesJson FROM app.ContentStyle WHERE StyleId = 'Heading1'"));
        Assert.IsType<DBNull>(await ScalarAsync(check, "SELECT BasedOnStyleId FROM app.ContentStyle WHERE StyleId = 'Heading2'"));
        Assert.Equal("Alice Anderson", await ScalarAsync(check, "SELECT DisplayName FROM app.[User] WHERE Login = N'alice'"));
        Assert.Equal(1, await ScalarAsync(check, "SELECT COUNT(*) FROM app.[User] WHERE Login = N'loadtest-00001'"));
    }

    [Fact]
    public async Task Redeploy_restores_a_deleted_built_in_style_with_its_inheritance()
    {
        var database = NewDatabaseName();
        DacpacDeployer.Deploy(server.MasterConnectionString, database);
        await using (var connection = await OpenAsync(database))
        {
            await ExecuteAsync(connection, "DELETE FROM app.ContentStyle WHERE StyleId = 'Caption';");
        }

        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        await using var check = await OpenAsync(database);
        Assert.Equal("Normal", await ScalarAsync(check, "SELECT BasedOnStyleId FROM app.ContentStyle WHERE StyleId = 'Caption'"));
    }

    [Fact]
    public async Task Seeded_styles_have_word_compatible_inheritance()
    {
        var database = NewDatabaseName();
        DacpacDeployer.Deploy(server.MasterConnectionString, database);

        await using var connection = await OpenAsync(database);
        Assert.Equal("Normal", await ScalarAsync(connection, "SELECT BasedOnStyleId FROM app.ContentStyle WHERE StyleId = 'Heading1'"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM app.ContentStyle WHERE IsBuiltIn = 0 OR IsActive = 0"));
    }

    private static string NewDatabaseName() => $"DocHub_Deploy_{Guid.NewGuid():N}";

    private async Task<SqlConnection> OpenAsync(string database)
    {
        var connection = new SqlConnection(DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, database));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task<List<string>> QueryListAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private sealed record SeedCounts(int Users, int NodeTypes, int ContentStyles, int Folders)
    {
        public static SeedCounts Expected { get; } = new(7, 5, 15, 2);

        public static async Task<SeedCounts> ReadAsync(SqlConnection connection)
        {
            await using var command = new SqlCommand(
                """
                SELECT (SELECT COUNT(*) FROM app.[User]), (SELECT COUNT(*) FROM app.NodeType),
                       (SELECT COUNT(*) FROM app.ContentStyle), (SELECT COUNT(*) FROM app.Folder);
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            return new SeedCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }
    }
}
