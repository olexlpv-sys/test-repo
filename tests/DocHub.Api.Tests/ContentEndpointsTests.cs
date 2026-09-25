using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Api.Documents;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Tests;

/// <summary>T09: styled node content through the API.</summary>
public sealed class ContentEndpointsTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private const string Paragraph = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"TEXT"}]}]}""";

    private readonly DocumentArrange _arrange = new(factory);

    public static TheoryData<string> Fixtures() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ContentFixtures"), "*.json").Select(Path.GetFileNameWithoutExtension).Order()!);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixtures_round_trip_identically(string fixture)
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ContentFixtures", fixture + ".json"));

        var saved = await SaveAsync(node, json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(saved.GetProperty("contentJson").GetRawText())));

        var read = await ReadAsync(node, "both");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(read.GetProperty("contentJson").GetRawText())));
        Assert.Equal(saved.GetProperty("rowVersion").GetString(), read.GetProperty("rowVersion").GetString());
        Assert.StartsWith("<", read.GetProperty("contentHtml").GetString(), StringComparison.Ordinal);
        Assert.Equal(TestUsers.Alice, read.GetProperty("modifiedBy").GetProperty("id").GetInt32());
        Assert.Equal(1, read.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("""{"type":"doc","content":[{"type":"video"}]}""", "contentJson.content[0].type")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textStyle","attrs":{"color":"red; background:url(https://x)"}}]}]}]}""", "contentJson.content[0].content[0].marks[0].attrs.color")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"javascript:alert(1)"}}]}]}]}""", "contentJson.content[0].content[0].marks[0].attrs.href")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textStyle","attrs":{"fontSize":1000}}]}]}]}""", "contentJson.content[0].content[0].marks[0].attrs.fontSize")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"styleId":"NoSuchStyle"}}]}""", "contentJson.content[0].attrs.styleId")]
    [InlineData("""[1,2,3]""", "contentJson")]
    public async Task Invalid_content_is_rejected_with_the_json_path(string json, string path)
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{node}/content",
            new { contentJson = JsonNode.Parse(json), rowVersion = await ContentRowVersionAsync(node) }, HttpStatusCode.BadRequest);
        Assert.Equal("validation-failed", problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(path, out _), problem.GetRawText());
    }

    [Fact]
    public async Task Content_larger_than_2_mb_is_rejected()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var huge = Paragraph.Replace("TEXT", new string('x', 2 * 1024 * 1024), StringComparison.Ordinal);
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{node}/content",
            new { contentJson = JsonNode.Parse(huge), rowVersion = await ContentRowVersionAsync(node) }, HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty("contentJson", out _));
    }

    [Fact]
    public async Task Saving_identical_content_twice_creates_one_audit_row_and_derived_columns_are_stored()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var first = await SaveAsync(node, Paragraph.Replace("TEXT", "same", StringComparison.Ordinal));
        // Same content in another spelling (key order, default attributes): canonically equal, so not written.
        var second = await SaveAsync(node, """{"content":[{"type":"paragraph","attrs":{"keepWithNext":false},"content":[{"text":"same","type":"text"}]}],"type":"doc"}""",
            first.GetProperty("rowVersion").GetString());
        Assert.Equal(first.GetProperty("rowVersion").GetString(), second.GetProperty("rowVersion").GetString());

        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        Assert.Equal(1, await dbo.ScalarAsync<int>("SELECT COUNT(*) FROM audit.ChangeLog WHERE TableName = N'app.NodeContent' AND EntityId = @n AND Operation = 'U';", ("@n", node)));
        var row = (await dbo.QueryAsync("SELECT ContentHtml, PlainText, DerivedStale, CONVERT(VARCHAR (64), ContentHash, 2) AS Hash FROM app.NodeContent WHERE NodeId = @n;", ("@n", node))).Single();
        Assert.Equal("<p>same</p>", row["ContentHtml"]);
        Assert.Equal("same", row["PlainText"]);
        Assert.Equal(false, row["DerivedStale"]);
        Assert.Equal(Convert.ToHexString(Domain.Content.CanonicalJson.Hash(first.GetProperty("contentJson").GetRawText())), row["Hash"]);
        Assert.True((await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}", null, HttpStatusCode.OK)).GetProperty("hasContent").GetBoolean());
    }

    [Fact]
    public async Task Signed_versions_forbidden_users_and_stale_row_versions_are_rejected()
    {
        var (documentId, _, nodes) = await _arrange.SignedAsync();
        var signedProblem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{nodes[0]}/content",
            new { contentJson = JsonNode.Parse(Paragraph), rowVersion = await ContentRowVersionAsync(nodes[0]) }, HttpStatusCode.Conflict);
        Assert.Equal("version-not-editable", signedProblem.GetProperty("type").GetString());

        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftNodes = await NodeIdsAsync(draft);
        foreach (var user in new[] { TestUsers.Bob, TestUsers.Carol, TestUsers.Admin })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Put, $"/api/nodes/{draftNodes[0]}/content",
                new { contentJson = JsonNode.Parse(Paragraph), rowVersion = await ContentRowVersionAsync(draftNodes[0]) }, HttpStatusCode.Forbidden);
        }

        var stale = await ContentRowVersionAsync(draftNodes[0]);
        await SaveAsync(draftNodes[0], Paragraph.Replace("TEXT", "first", StringComparison.Ordinal), stale);
        var conflict = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{draftNodes[0]}/content",
            new { contentJson = JsonNode.Parse(Paragraph.Replace("TEXT", "second", StringComparison.Ordinal)), rowVersion = stale }, HttpStatusCode.Conflict);
        Assert.Equal("concurrency-conflict", conflict.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Document_and_node_editors_edit_content_within_their_scope()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var chapter = await AddNodeAsync(draft);
        var section = await AddNodeAsync(draft, chapter);
        var other = await AddNodeAsync(draft);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync(
                "INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId) SELECT @d, @u, 1, LogicalNodeId, 2 FROM app.DocumentNode WHERE Id = @n;",
                ("@d", documentId), ("@u", TestUsers.Erin), ("@n", chapter));
        }

        await SaveAsync(chapter, Paragraph, userId: TestUsers.Erin);
        await SaveAsync(section, Paragraph, userId: TestUsers.Erin);
        await ApiClient.ExpectAsync(factory, TestUsers.Erin, HttpMethod.Put, $"/api/nodes/{other}/content",
            new { contentJson = JsonNode.Parse(Paragraph), rowVersion = await ContentRowVersionAsync(other) }, HttpStatusCode.Forbidden);

        await _arrange.GrantAsync(documentId, TestUsers.Bob, role: 1);
        var saved = await SaveAsync(other, Paragraph, userId: TestUsers.Bob);
        Assert.Equal(TestUsers.Bob, saved.GetProperty("modifiedBy").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Content_using_a_deactivated_style_can_still_be_saved()
    {
        var styleId = $"Old{Guid.NewGuid():N}"[..20];
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("INSERT INTO app.ContentStyle (StyleId, Name, Kind, PropertiesJson, IsActive) VALUES (@s, @s, 1, N'{\"bold\":true}', 0);", ("@s", styleId));
        }

        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var saved = await SaveAsync(node, $$"""{"type":"doc","content":[{"type":"paragraph","attrs":{"styleId":"{{styleId.ToUpperInvariant()}}"},"content":[{"type":"text","text":"x"}]}]}""");
        // Stored in the catalog's spelling (the stylesheet's class) and counted as a usage of the style.
        Assert.Equal(styleId, saved.GetProperty("contentJson").GetProperty("content")[0].GetProperty("attrs").GetProperty("styleId").GetString());
        await using var check = await SqlSession.OpenAsync(factory.AdminConnectionString);
        Assert.Equal(1, await check.ScalarAsync<int>("SELECT COUNT(*) FROM app.ContentStyleUsage WHERE StyleId = @s AND NodeId = @n;", ("@s", styleId), ("@n", node)));

        // Changing the content maintains the usage rows.
        await SaveAsync(node, Paragraph, saved.GetProperty("rowVersion").GetString());
        Assert.Equal(0, await check.ScalarAsync<int>("SELECT COUNT(*) FROM app.ContentStyleUsage WHERE NodeId = @n;", ("@n", node)));
    }

    [Fact]
    public async Task Script_edits_render_immediately_and_the_refresher_persists_derived_columns()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        var original = await ReadAsync(nodes[0], "json");
        await RefreshUntilCleanAsync(nodes[0]);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;",
                ("@j", Paragraph.Replace("TEXT", "<b>edited by script</b>", StringComparison.Ordinal)), ("@n", nodes[0]));
            Assert.True(await dbo.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n;", ("@n", nodes[0])));
        }

        Assert.Equal("<p>&lt;b&gt;edited by script&lt;/b&gt;</p>", (await ReadAsync(nodes[0], "html")).GetProperty("contentHtml").GetString());
        var batch = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{v1}/content?nodeIds={nodes[0]}&format=html", null, HttpStatusCode.OK);
        Assert.Equal("<p>&lt;b&gt;edited by script&lt;/b&gt;</p>", batch[0].GetProperty("contentHtml").GetString());

        long stamp;
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            stamp = await dbo.ScalarAsync<long>("SELECT LastChangeLogId FROM app.VersionStamp WHERE DocumentVersionId = @v;", ("@v", v1));
        }

        await RefreshUntilCleanAsync(nodes[0]);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            var row = (await dbo.QueryAsync("SELECT ContentHtml, PlainText, CONVERT(VARCHAR (64), ContentHash, 2) AS Hash FROM app.NodeContent WHERE NodeId = @n;", ("@n", nodes[0]))).Single();
            Assert.Equal("<p>&lt;b&gt;edited by script&lt;/b&gt;</p>", row["ContentHtml"]);
            Assert.Equal("<b>edited by script</b>", row["PlainText"]);
            Assert.Equal(Convert.ToHexString(Domain.Content.CanonicalJson.Hash(Paragraph.Replace("TEXT", "<b>edited by script</b>", StringComparison.Ordinal))), row["Hash"]);
            // Not audited and not a change of the version (the stamp stays).
            Assert.Equal(stamp, await dbo.ScalarAsync<long>("SELECT LastChangeLogId FROM app.VersionStamp WHERE DocumentVersionId = @v;", ("@v", v1)));
        }

        Assert.True((await _arrange.VersionAsync(v1)).GetProperty("modifiedAfterSigning").GetBoolean());

        // An API save of the pre-script content in a new draft is not skipped as "unchanged".
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftNode = (await NodeIdsAsync(draft))[0];
        var copied = await ReadAsync(draftNode, "json");
        Assert.Contains("edited by script", copied.GetProperty("contentJson").GetRawText(), StringComparison.Ordinal);
        var restored = await SaveAsync(draftNode, original.GetProperty("contentJson").GetRawText(), copied.GetProperty("rowVersion").GetString());
        Assert.NotEqual(copied.GetProperty("rowVersion").GetString(), restored.GetProperty("rowVersion").GetString());
        Assert.DoesNotContain("edited by script", (await ReadAsync(draftNode, "html")).GetProperty("contentHtml").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_after_a_script_edit_of_the_same_draft_is_not_skipped()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var content = Paragraph.Replace("TEXT", "api", StringComparison.Ordinal);
        await SaveAsync(node, content);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", Paragraph.Replace("TEXT", "script", StringComparison.Ordinal)), ("@n", node));
        }

        var saved = await SaveAsync(node, content);
        Assert.Contains("\"api\"", saved.GetProperty("contentJson").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("<p>api</p>", (await ReadAsync(node, "html")).GetProperty("contentHtml").GetString());
        // The derived columns still describe "api", so the save leaves the hash unchanged and the trigger keeps the row
        // flagged (it can't tell a forged hash from an equal one); the refresher confirms and clears it.
        await RefreshUntilCleanAsync(node);
        Assert.Equal("<p>api</p>", (await ReadAsync(node, "html")).GetProperty("contentHtml").GetString());
    }

    [Fact]
    public async Task Reads_follow_the_version_stamp_and_deleted_documents_are_hidden_from_others()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        var (status, _, response) = await ApiClient.SendAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/nodes/{nodes[0]}/content", null);
        Assert.Equal(HttpStatusCode.OK, status);
        var etag = response.Headers.ETag!;
        Assert.True(response.Headers.CacheControl is { Private: true, NoCache: true });
        response.Dispose();

        using (var client = factory.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-User-Id", TestUsers.Bob.ToString(System.Globalization.CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.IfNoneMatch.Add(etag);
            using var notModified = await client.GetAsync(new Uri($"/api/nodes/{nodes[0]}/content", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            using var batchNotModified = await client.GetAsync(new Uri($"/api/versions/{v1}/content?nodeIds={nodes[0]},{nodes[1]}", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotModified, batchNotModified.StatusCode);
            await _arrange.EditContentBySqlAsync(nodes[1]);
            using var changed = await client.GetAsync(new Uri($"/api/nodes/{nodes[0]}/content", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        var batch = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{v1}/content?nodeIds={nodes[2]},{nodes[0]}&format=html", null, HttpStatusCode.OK);
        Assert.Equal([nodes[2], nodes[0]], batch.EnumerateArray().Select(c => c.GetProperty("nodeId").GetInt32()));
        Assert.All(batch.EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("contentJson").ValueKind));

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        foreach (var (user, expected) in new[] { (TestUsers.Bob, HttpStatusCode.NotFound), (TestUsers.Carol, HttpStatusCode.NotFound), (TestUsers.Alice, HttpStatusCode.OK), (TestUsers.Admin, HttpStatusCode.OK) })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/nodes/{nodes[0]}/content", null, expected);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/versions/{v1}/content?nodeIds={nodes[0]}", null, expected);
        }
    }

    [Fact]
    public async Task Batch_reads_validate_their_node_list()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft);
        var (_, other) = await _arrange.CreateAsync();
        var foreign = await AddNodeAsync(other);
        foreach (var query in new[] { "", "?nodeIds=", "?nodeIds=abc", "?nodeIds=-1", $"?nodeIds={string.Join(',', Enumerable.Range(1, 201))}", $"?nodeIds={node}&format=pdf" })
        {
            await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/content{query}", null, HttpStatusCode.BadRequest);
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/content?nodeIds={node},{foreign}", null, HttpStatusCode.NotFound);
        var both = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/content?nodeIds={node},{node}&format=both", null, HttpStatusCode.OK);
        Assert.Equal(1, both.GetArrayLength());
        Assert.Equal("", both[0].GetProperty("contentHtml").GetString());
    }

    [Fact]
    public async Task The_content_schema_is_machine_readable()
    {
        var schema = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/content-schema", null, HttpStatusCode.OK);
        Assert.Equal(1, schema.GetProperty("version").GetInt32());
        Assert.Equal(20, schema.GetProperty("maxDepth").GetInt32());
        Assert.Contains("Georgia", schema.GetProperty("fontFamilies").EnumerateArray().Select(f => f.GetString()));
        var fontSize = schema.GetProperty("marks").GetProperty("textStyle").GetProperty("attributes").GetProperty("fontSize");
        Assert.Equal((2, 400), (fontSize.GetProperty("min").GetInt32(), fontSize.GetProperty("max").GetInt32()));
        Assert.Equal(["tableRow"], schema.GetProperty("nodes").GetProperty("table").GetProperty("children").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void The_refresher_runs_at_least_twice_a_minute_by_default()
    {
        var defaults = new DerivedRefreshOptions();
        Assert.True(defaults.Enabled);
        Assert.True(defaults.Interval <= TimeSpan.FromSeconds(30));
        Assert.Equal(500, defaults.BatchSize);
        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<DerivedContentRefresher>());
        Assert.False(factory.Services.GetRequiredService<IOptions<DerivedRefreshOptions>>().Value.Enabled); // tests run it explicitly
    }

    private async Task RefreshUntilCleanAsync(int nodeId)
    {
        var refresher = factory.Services.GetServices<IHostedService>().OfType<DerivedContentRefresher>().Single();
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        for (var round = 0; round < 20 && await dbo.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n;", ("@n", nodeId)); round++)
        {
            await refresher.RunOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(await dbo.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n;", ("@n", nodeId)));
    }

    private async Task<int> AddNodeAsync(int versionId, int? parentNodeId = null) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{versionId}/nodes", new { parentNodeId, nodeTypeId = 1, title = "Node" }, HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

    private async Task<string> ContentRowVersionAsync(int nodeId) => (await ReadAsync(nodeId, "json", TestUsers.Admin)).GetProperty("rowVersion").GetString()!;

    private Task<JsonElement> ReadAsync(int nodeId, string format, int userId = TestUsers.Alice) =>
        ApiClient.ExpectAsync(factory, userId, HttpMethod.Get, $"/api/nodes/{nodeId}/content?format={format}", null, HttpStatusCode.OK);

    private async Task<JsonElement> SaveAsync(int nodeId, string contentJson, string? rowVersion = null, int userId = TestUsers.Alice) =>
        await ApiClient.ExpectAsync(factory, userId, HttpMethod.Put, $"/api/nodes/{nodeId}/content",
            new { contentJson = JsonNode.Parse(contentJson), rowVersion = rowVersion ?? await ContentRowVersionAsync(nodeId) }, HttpStatusCode.OK);

    private async Task<List<int>> NodeIdsAsync(int versionId)
    {
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{versionId}/tree", null, HttpStatusCode.OK);
        var ids = new List<int>();
        void Walk(JsonElement level)
        {
            foreach (var n in level.EnumerateArray())
            {
                ids.Add(n.GetProperty("id").GetInt32());
                Walk(n.GetProperty("children"));
            }
        }

        Walk(tree);
        return ids;
    }
}
