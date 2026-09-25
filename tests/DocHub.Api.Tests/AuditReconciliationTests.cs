using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocHub.Api.Audit;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Infrastructure.Audit;
using DocHub.Testing;
using DocHub.Testing.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DocHub.Api.Tests;

/// <summary>T21 §4: the admin audit endpoints and the nightly reconciliation, with the API connecting as an app_api login.</summary>
public sealed class AuditReconciliationTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reconcile_on_a_fresh_publish_finds_nothing_including_no_module_problems()
    {
        var run = await AuditApi.ReconcileAsync(factory);

        Assert.Equal(0, run.GetProperty("newFindings").GetInt32());
        Assert.Equal(0, run.GetProperty("moduleProblems").GetInt32());
        var window = run.GetProperty("toUtc").GetDateTime() - run.GetProperty("fromUtc").GetDateTime();
        Assert.InRange(window, TimeSpan.FromHours(48), TimeSpan.FromHours(48) + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Findings_created_in_the_database_are_listed_newest_first_and_paged()
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync(
            """
            INSERT INTO audit.ReconciliationFinding (Kind, TableName, EntityId, LedgerTransactionId, Detail)
            VALUES ('TriggerBypass', N'app.NodeContent', 7, 900000001, N'first'), ('ForgedAuditRow', N'app.Folder', 8, 900000002, N'second');
            """);
        using var client = factory.CreateClientFor(TestUsers.Admin);

        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/admin/audit/findings?pageSize=1", UriKind.Relative), Ct);

        Assert.True(page.GetProperty("totalCount").GetInt32() >= 2);
        var finding = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("ForgedAuditRow", finding.GetProperty("kind").GetString());
        Assert.Equal("app.Folder", finding.GetProperty("tableName").GetString());
        Assert.Equal(8, finding.GetProperty("entityId").GetInt32());
        Assert.Equal(900000002, finding.GetProperty("ledgerTransactionId").GetInt64());
        Assert.False(finding.GetProperty("afterSigning").GetBoolean());
    }

    [Fact]
    public async Task Nightly_run_reconciles_as_the_system_user()
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        var documentId = await dbo.Data.DocumentAsync();
        var versionId = await dbo.Data.VersionAsync(documentId);
        var nodeId = await dbo.Data.NodeAsync(versionId);
        await dbo.Data.ContentAsync(nodeId);
        await dbo.ExecuteAsync(
            """
            DISABLE TRIGGER app.TR_NodeContent_Audit ON app.NodeContent;
            UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.forged', 1) WHERE NodeId = @n;
            ENABLE TRIGGER app.TR_NodeContent_Audit ON app.NodeContent;
            """,
            ("@n", nodeId));
        var service = factory.Services.GetServices<IHostedService>().OfType<LedgerReconciliationService>().Single();

        var run = await service.RunOnceAsync(Ct);

        Assert.Equal(1, run.NewFindings);
        var log = Assert.Single(await dbo.QueryAsync(
            "SELECT UserId, Source, OperationContext FROM audit.ChangeLog WHERE TableName = N'audit.ReconciliationFinding' AND DocumentVersionId = @v;",
            ("@v", versionId)));
        Assert.Equal(0, log["UserId"]);
        Assert.Equal("App", log["Source"]);
        Assert.Equal("Reconciliation", log["OperationContext"]);
    }

    [Theory]
    [InlineData("2026-03-01T01:00:00", "2026-03-01T02:00:00")]
    [InlineData("2026-03-01T02:00:00", "2026-03-02T02:00:00")]
    [InlineData("2026-03-01T23:59:00", "2026-03-02T02:00:00")]
    public void Nightly_run_is_scheduled_at_the_configured_time(string now, string expected) =>
        Assert.Equal(
            DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            LedgerReconciliationService.NextRun(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(2, 0)));

    [Fact]
    public async Task Audit_endpoints_are_admin_only()
    {
        int?[] callers = [null, TestUsers.System, TestUsers.Admin, TestUsers.Alice, TestUsers.Bob, TestUsers.Carol, TestUsers.Dave, TestUsers.Erin];
        var cases = callers.SelectMany(user =>
        {
            var expected = user switch
            {
                null or TestUsers.System => HttpStatusCode.Unauthorized,
                TestUsers.Admin => HttpStatusCode.OK,
                _ => HttpStatusCode.Forbidden,
            };
            return new[]
            {
                new AccessCase("POST", "/api/admin/audit/reconcile", user, expected),
                new AccessCase("GET", "/api/admin/audit/findings", user, expected),
            };
        });

        await AuthorizationMatrix.AssertAsync(factory, cases, Ct);
    }
}

/// <summary>Rule 6: a module changed by hand is a module-integrity finding (one database per class: the change stays).</summary>
public sealed class ModuleTamperingTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Fact]
    public async Task Altering_the_procedure_or_a_trigger_disabling_a_trigger_or_adding_a_module_is_found()
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await AuditApi.AlterByHandAsync(dbo, "audit.usp_ReconcileLedger", "\n-- tampered: hides findings");
        await AuditApi.AlterByHandAsync(dbo, "app.TR_NodeContent_Audit", "\n-- tampered");
        await dbo.ExecuteAsync("DISABLE TRIGGER app.TR_Comment_Audit ON app.Comment;");
        await dbo.ExecuteAsync("EXEC (N'CREATE PROCEDURE audit.usp_Helper AS SELECT 1;');");

        var run = await AuditApi.ReconcileAsync(factory);

        Assert.Equal(4, run.GetProperty("moduleProblems").GetInt32());
        var findings = await dbo.QueryAsync("SELECT TableName, Detail FROM audit.ReconciliationFinding WHERE Kind = 'ModuleIntegrity' ORDER BY TableName;");
        Assert.Equal(["app.TR_Comment_Audit", "app.TR_NodeContent_Audit", "audit.usp_Helper", "audit.usp_ReconcileLedger"], findings.Select(f => (string)f["TableName"]!));
        Assert.Contains("disabled", (string)findings[0]["Detail"]!, StringComparison.Ordinal);
        Assert.Contains("differs", (string)findings[3]["Detail"]!, StringComparison.Ordinal);

        // Recorded once per module and detail.
        await AuditApi.ReconcileAsync(factory);
        Assert.Equal(4, await dbo.ScalarAsync<int>("SELECT COUNT(*) FROM audit.ReconciliationFinding WHERE Kind = 'ModuleIntegrity';"));
    }
}

/// <summary>Rule 6: a definition the API login can't read is itself a finding.</summary>
public sealed class ModuleUnreadableTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Fact]
    public async Task A_module_whose_definition_is_hidden_from_the_api_is_found()
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync("DENY VIEW DEFINITION ON OBJECT::audit.fn_ChangeContext TO app_api;");

        var run = await AuditApi.ReconcileAsync(factory);

        Assert.Equal(1, run.GetProperty("moduleProblems").GetInt32());
        var finding = Assert.Single(await dbo.QueryAsync("SELECT TableName, Detail FROM audit.ReconciliationFinding WHERE Kind = 'ModuleIntegrity';"));
        Assert.Equal("audit.fn_ChangeContext", finding["TableName"]);
    }
}

/// <summary>
/// Rule 6, no false positive on an update: a new version of the procedure deployed together with its regenerated hash (as a
/// DACPAC update carries both). Deployed here with ALTER — the normalization makes CREATE and ALTER hash the same.
/// </summary>
public sealed class ModuleUpdateTests(ModuleUpdateTests.Factory factory) : IClassFixture<ModuleUpdateTests.Factory>
{
    private const string Change = "\n-- version 2 of the procedure";

    [Fact]
    public async Task An_updated_module_with_its_regenerated_hash_is_not_a_finding()
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await AuditApi.AlterByHandAsync(dbo, "audit.usp_ReconcileLedger", Change);

        var run = await AuditApi.ReconcileAsync(factory);

        Assert.Equal(0, run.GetProperty("moduleProblems").GetInt32());
    }

    public sealed class Factory(SqlServerContainerFixture server) : DocHubApiFactory(server)
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            var script = File.ReadAllText(Path.Combine(Repository.DatabaseProject, "audit", "StoredProcedures", "usp_ReconcileLedger.sql"));
            var hashes = new Dictionary<string, string>(AuditModuleHashes.Expected, StringComparer.OrdinalIgnoreCase)
            {
                ["audit.usp_ReconcileLedger"] = ModuleDefinition.Hash(script.TrimEnd() + Change),
            };
            services.RemoveAll<IAuditModuleHashes>();
            services.AddSingleton<IAuditModuleHashes>(new FixedHashes(hashes));
        }

        private sealed class FixedHashes(IReadOnlyDictionary<string, string> hashes) : IAuditModuleHashes
        {
            public IReadOnlyDictionary<string, string> Hashes => hashes;
        }
    }
}

/// <summary>The generated hashes match the module scripts of the DB project under the runtime normalization.</summary>
public sealed class AuditModuleHashesTests
{
    private static readonly string[] ModuleDirectories = ["Functions", "StoredProcedures", "Views"];

    [Fact]
    public void Generated_hashes_match_the_db_project_modules()
    {
        var project = Repository.DatabaseProject;
        var files = ModuleDirectories
            .Select(d => Path.Combine(project, "audit", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.sql"))
            .Concat(Directory.GetFiles(Path.Combine(project, "app", "Triggers"), "TR_*_Audit.sql"));
        var actual = files.ToDictionary(
            f => System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), @"^CREATE\s+\w+\s+\[(\w+)\]\.\[(\w+)\]", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m
                ? $"{m.Groups[1].Value}.{m.Groups[2].Value}"
                : throw new InvalidOperationException($"No module in {f}"),
            f => ModuleDefinition.Hash(File.ReadAllText(f)));

        Assert.Equal(actual.OrderBy(p => p.Key, StringComparer.Ordinal), AuditModuleHashes.Expected.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p AS SELECT 1;", "ALTER PROCEDURE p AS SELECT 1;")]
    [InlineData("-- c\n/* d */\nCREATE PROCEDURE p AS SELECT 1;", "-- c\r\n/* d */\r\ncreate or alter PROCEDURE p AS SELECT 1;   \r\n\r\n")]
    public void Normalization_ignores_line_endings_trailing_whitespace_and_the_create_keyword(string a, string b) =>
        Assert.Equal(ModuleDefinition.Hash(a), ModuleDefinition.Hash(b));

    [Fact]
    public void Normalization_keeps_every_other_change() =>
        Assert.NotEqual(ModuleDefinition.Hash("CREATE PROCEDURE p AS SELECT 1;"), ModuleDefinition.Hash("CREATE PROCEDURE p AS  SELECT 1;"));
}

internal static class AuditApi
{
    public static async Task<JsonElement> ReconcileAsync(DocHubApiFactory factory)
    {
        using var client = factory.CreateClientFor(TestUsers.Admin);
        using var response = await client.PostAsync(new Uri("/api/admin/audit/reconcile", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    /// <summary>A privileged principal re-creating a module by hand: its current definition with CREATE → ALTER, plus a change.</summary>
    public static async Task AlterByHandAsync(SqlSession dbo, string module, string change)
    {
        var definition = await dbo.ScalarAsync<string>("SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(@m);", ("@m", module));
        var altered = new System.Text.RegularExpressions.Regex(@"^CREATE\s", System.Text.RegularExpressions.RegexOptions.Multiline).Replace(definition, "ALTER ", 1);
        await dbo.ExecuteAsync(altered.TrimEnd() + change);
    }
}
