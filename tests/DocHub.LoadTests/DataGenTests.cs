using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocHub.DataGen;
using DocHub.Testing.Database;
using Microsoft.Data.SqlClient;

namespace DocHub.LoadTests;

/// <summary>A tiny generated data set (5 documents) for the smoke tests.</summary>
public sealed class SmallGeneratedDatabase(SqlServerContainerFixture server) : GeneratedDatabase(server)
{
    protected override double DefaultScale => 0.0005;
}

/// <summary>
/// T19 §1: the generated data is consistent (constraints trusted, stamps and hashes valid, signatures count), usable through the
/// API (list, open, tree, content, history, compare) and invisible to the ledger reconciliation (baseline).
/// </summary>
public sealed class DataGenTests(SmallGeneratedDatabase data, SqlServerContainerFixture server) : IClassFixture<SmallGeneratedDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(data.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Limits a query over nodes (<c>n</c>) to the generated documents (other tests add their own).</summary>
    private static string Generated(string sql) =>
        $"{sql} JOIN app.DocumentVersion gv ON gv.Id = n.DocumentVersionId JOIN app.Document gd ON gd.Id = gv.DocumentId WHERE gd.Title LIKE N'Load document%'";

    private async Task<JsonElement> GetAsync(int user, string path)
    {
        using var client = data.ClientFor(user);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task The_volume_follows_the_options_and_every_constraint_stays_trusted()
    {
        var options = new DataGenOptions { Scale = data.Scale };
        Assert.Equal(options.Documents, data.Report.Documents);
        Assert.Equal(options.Documents * options.VersionsPerDocument, data.Report.Versions);
        Assert.Equal(options.Documents * options.GrantsPerDocument, data.Report.Grants);
        Assert.Equal(data.Report.Versions * options.CommentsPerVersion, data.Report.Comments);
        Assert.True(data.Report.Nodes >= data.Report.Contents && data.Report.Contents > 0);
        Assert.Equal(data.Report.Nodes, await ScalarAsync<int>(Generated("SELECT COUNT(*) FROM app.DocumentNode n")));

        Assert.Equal(0, await ScalarAsync<int>("SELECT (SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1) + (SELECT COUNT(*) FROM sys.check_constraints WHERE is_not_trusted = 1)"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentNode n WHERE NOT EXISTS (SELECT 1 FROM audit.ChangeLog l WHERE l.TableName = N'app.DocumentNode' AND l.EntityId = n.Id)"));
        // Stamps cover every version and the cached hashes are valid for them.
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentVersion v LEFT JOIN app.VersionStamp s ON s.DocumentVersionId = v.Id WHERE s.DocumentVersionId IS NULL"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app.VersionContentHash h JOIN app.VersionStamp s ON s.DocumentVersionId = h.DocumentVersionId WHERE h.ContentChangeLogId <> s.ContentChangeLogId"));
    }

    [Fact]
    public async Task Content_copied_unchanged_into_a_new_version_is_audited_as_a_copy_and_changed_content_is_not()
    {
        // The history (T11) reads "copied to the new draft" from CopyVersion rows: they must match the content itself.
        const string Sql = """
            SELECT COUNT(*)
            FROM app.NodeContent c
            JOIN app.DocumentVersion v ON v.Id = c.DocumentVersionId
            JOIN app.NodeContent p ON p.DocumentVersionId = v.BasedOnVersionId AND p.LogicalNodeId = c.LogicalNodeId
            JOIN audit.ChangeLog l ON l.TableName = N'app.NodeContent' AND l.EntityId = c.NodeId
            WHERE CASE WHEN p.ContentHash = c.ContentHash THEN 1 ELSE 0 END <> CASE WHEN l.OperationContext = N'CopyVersion' THEN 1 ELSE 0 END
            """;

        Assert.Equal(0, await ScalarAsync<int>(Sql));
        Assert.True(await ScalarAsync<int>("SELECT COUNT(*) FROM app.NodeContent c JOIN app.DocumentVersion v ON v.Id = c.DocumentVersionId JOIN app.NodeContent p ON p.DocumentVersionId = v.BasedOnVersionId AND p.LogicalNodeId = c.LogicalNodeId WHERE p.ContentHash <> c.ContentHash") > 0);
    }

    [Fact]
    public async Task A_failing_loader_ends_the_run_with_its_error()
    {
        var database = FreshDatabase();
        var generator = new DataGenerator(database, new DataGenOptions { Scale = data.Scale, Parallelism = 1, ChunkDocuments = 1 }, _ => { })
        {
            BeforeChunkLoad = _ => throw new InvalidOperationException("Transaction log full."),
        };

        // Without the abort, the producer waits for room in the channel forever.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.RunAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct));
        Assert.Equal("Transaction log full.", error.Message);
    }

    [Fact]
    public async Task A_run_over_the_time_limit_fails()
    {
        var database = FreshDatabase();
        var generator = new DataGenerator(database, new DataGenOptions { Scale = data.Scale, MaxDuration = TimeSpan.FromSeconds(3) }, _ => { })
        {
            BeforeChunkLoad = _ => Thread.Sleep(TimeSpan.FromSeconds(4)),
        };

        await Assert.ThrowsAsync<TimeoutException>(() => generator.RunAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct));
    }

    private string FreshDatabase()
    {
        var database = $"DocHub_Gen_{Guid.NewGuid():N}";
        DacpacDeployer.Deploy(server.MasterConnectionString, database);
        return DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, database);
    }

    [Fact]
    public async Task The_top_queries_report_includes_parameterized_statements()
    {
        // EF Core sends every query through sp_executesql: the report must see those (their sql_text has no dbid).
        await using (var connection = new SqlConnection(data.AdminConnectionString))
        {
            await connection.OpenAsync(Ct);
            for (var i = 0; i < 3; i++)
            {
                await using var command = new SqlCommand("SELECT COUNT(*) FROM app.Document WHERE Title LIKE @p /* top-queries-probe */", connection);
                command.Parameters.AddWithValue("@p", "Load document%");
                await command.ExecuteScalarAsync(Ct);
            }
        }

        Assert.Contains("top-queries-probe", await LoadRunTests.TopQueriesAsync(data.AdminConnectionString, count: 100_000), StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_rows_get_ids_after_the_generated_ones()
    {
        var owner = await ScalarAsync<int>("SELECT MIN(Id) FROM app.[User] WHERE Login LIKE N'load%'");
        using var client = data.ClientFor(owner);
        using var created = await client.PostAsJsonAsync("/api/documents", new { folderId = 1, title = "After the bulk load" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var draft = document.GetProperty("draftVersionId").GetInt32();
        using var node = await client.PostAsJsonAsync($"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "New" }, Ct);
        Assert.Equal(HttpStatusCode.Created, node.StatusCode);
        using var comment = await client.PostAsJsonAsync($"/api/versions/{draft}/comments", new { body = "New" }, Ct);
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        using var grant = await client.PostAsJsonAsync($"/api/documents/{document.GetProperty("id").GetInt32()}/permissions", new { userId = owner + 1, role = "Approver" }, Ct);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        Assert.True(document.GetProperty("id").GetInt32() > await ScalarAsync<int>("SELECT MAX(Id) FROM app.Document WHERE Title <> N'After the bulk load'"));
    }

    [Fact]
    public async Task The_ledger_reconciliation_finds_nothing_after_the_baseline()
    {
        using var client = data.ClientFor(1);
        using var response = await client.PostAsync(new Uri("/api/admin/audit/reconcile", UriKind.Relative), null, Ct);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0, 0), (run.GetProperty("newFindings").GetInt32(), run.GetProperty("moduleProblems").GetInt32()));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM audit.ReconciliationBaseline"));
    }

    [Fact]
    public async Task Generated_documents_work_through_the_api()
    {
        var documentId = await ScalarAsync<int>("SELECT TOP (1) d.Id FROM app.Document d WHERE d.DeletedAt IS NULL AND EXISTS (SELECT 1 FROM app.DocumentVersion v WHERE v.DocumentId = d.Id AND v.Status = 1) ORDER BY d.Id");
        var folderId = await ScalarAsync<int>($"SELECT FolderId FROM app.Document WHERE Id = {documentId}");
        var reader = await ScalarAsync<int>("SELECT MAX(Id) FROM app.[User] WHERE Login LIKE N'load%'");
        var owner = await ScalarAsync<int>($"SELECT OwnerUserId FROM app.Document WHERE Id = {documentId}");

        var list = await GetAsync(reader, $"/api/folders/{folderId}/documents?Search=Load&PageSize=100");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetInt32() == documentId);

        var document = await GetAsync(reader, $"/api/documents/{documentId}");
        var versions = document.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(5, versions.Count);
        // Every approver's signature matches the tree hash of its version.
        Assert.All(versions.Where(v => v.GetProperty("status").GetString() == "Signed"), v => Assert.Equal(3, v.GetProperty("signedBy").GetArrayLength()));
        Assert.DoesNotContain(versions, v => v.GetProperty("modifiedAfterSigning").GetBoolean());
        var draft = versions.Single(v => v.GetProperty("status").GetString() == "Draft");
        Assert.True(draft.GetProperty("isCurrent").GetBoolean());

        var draftId = draft.GetProperty("id").GetInt32();
        var tree = await GetAsync(reader, $"/api/versions/{draftId}/tree");
        Assert.True(tree.GetArrayLength() > 0);
        var nodeId = await ScalarAsync<int>($"SELECT TOP (1) c.NodeId FROM app.NodeContent c WHERE c.DocumentVersionId = {draftId} ORDER BY c.NodeId");
        var logical = await ScalarAsync<Guid>($"SELECT LogicalNodeId FROM app.DocumentNode WHERE Id = {nodeId}");
        var content = await GetAsync(reader, $"/api/nodes/{nodeId}/content?format=both");
        Assert.StartsWith("<", content.GetProperty("contentHtml").GetString(), StringComparison.Ordinal);

        var history = await GetAsync(reader, $"/api/documents/{documentId}/nodes/{logical}/history");
        Assert.Contains(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("kind").GetString() == "CopiedToNewDraft");
        await GetAsync(reader, $"/api/documents/{documentId}/compare?base=latestSigned&target=draft");
        await GetAsync(owner, $"/api/versions/{draftId}/change-summary?since=latestSigned");
        await GetAsync(reader, $"/api/versions/{draftId}/comments");
    }

    [Fact]
    public async Task A_database_that_already_has_a_baseline_is_refused()
    {
        var generator = new DataGenerator(data.AdminConnectionString, new DataGenOptions { Scale = 0.0001 }, _ => { });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.RunAsync(Ct));
        Assert.Contains("freshly deployed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_seed_generates_the_same_data()
    {
        var database = $"DocHub_Seed_{Guid.NewGuid():N}";
        DacpacDeployer.Deploy(server.MasterConnectionString, database);
        await new DataGenerator(DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, database), new DataGenOptions { Scale = data.Scale }, _ => { }).RunAsync(Ct);

        var fingerprint = Generated("SELECT CHECKSUM_AGG(CHECKSUM(n.Id, n.LogicalNodeId, n.ParentNodeId, n.Title, n.SortOrder)) FROM app.DocumentNode n");
        var contents = Generated("SELECT CHECKSUM_AGG(CHECKSUM(c.NodeId, CONVERT(NVARCHAR(64), c.ContentHash, 2))) FROM app.NodeContent c JOIN app.DocumentNode n ON n.Id = c.NodeId");
        await using var other = new SqlConnection(DacpacDeployer.DatabaseConnectionString(server.MasterConnectionString, database));
        await other.OpenAsync(Ct);
        async Task<int> OtherAsync(string sql)
        {
            await using var command = new SqlCommand(sql, other);
            return (int)(await command.ExecuteScalarAsync(Ct))!;
        }

        Assert.Equal(await ScalarAsync<int>(fingerprint), await OtherAsync(fingerprint));
        Assert.Equal(await ScalarAsync<int>(contents), await OtherAsync(contents));
    }
}
