using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>T12: comparing two versions of a document.</summary>
public sealed class CompareTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    private static string Paragraph(string text) => $$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}]}""";

    [Fact]
    public async Task Summary_and_statuses_match_the_changes_between_two_signed_versions()
    {
        // v1: A, B (with child B1), C, D, E; contents on A and C.
        var (documentId, v1) = await _arrange.CreateAsync();
        var ids = new Dictionary<string, int>();
        foreach (var title in new[] { "A", "B", "C", "D", "E" })
        {
            ids[title] = await AddAsync(v1, null, title);
        }

        ids["B1"] = await AddAsync(v1, ids["B"], "B1");
        await SaveAsync(ids["A"], "alpha text");
        await SaveAsync(ids["C"], "gamma text");
        var logical = new Dictionary<string, Guid>();
        foreach (var (title, id) in ids)
        {
            logical[title] = await LogicalAsync(id);
        }

        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(v1, TestUsers.Carol);

        // v2: added F, removed subtree B/B1, moved E under D, renamed C, content of A and D edited.
        var v2 = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var n = await NodesAsync(v2);
        await AddAsync(v2, null, "F");
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/nodes/{n[logical["B"]]}?rowVersion={Uri.EscapeDataString(await RowVersionAsync(n[logical["B"]]))}", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/nodes/{n[logical["E"]]}/move", new { newParentNodeId = n[logical["D"]], rowVersion = await RowVersionAsync(n[logical["E"]]) }, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{n[logical["C"]]}", new { title = "C renamed", rowVersion = await RowVersionAsync(n[logical["C"]]) }, HttpStatusCode.OK);
        await SaveAsync(n[logical["A"]], "alpha text changed");
        await SaveAsync(n[logical["D"]], "delta new");
        await _arrange.SignAsync(v2, TestUsers.Carol);

        var comparison = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target={v2}&includeUnchanged=true", null, HttpStatusCode.OK);
        var summary = comparison.GetProperty("summary");
        Assert.Equal((1, 2, 1, 1, 0, 2, 0),
            (summary.GetProperty("added").GetInt32(), summary.GetProperty("removed").GetInt32(), summary.GetProperty("moved").GetInt32(), summary.GetProperty("renamed").GetInt32(),
             summary.GetProperty("typeChanged").GetInt32(), summary.GetProperty("contentChanged").GetInt32(), summary.GetProperty("unchanged").GetInt32()));
        Assert.Equal("v1", comparison.GetProperty("base").GetProperty("label").GetString());
        Assert.Equal("v2", comparison.GetProperty("target").GetProperty("label").GetString());

        var nodes = Flatten(comparison.GetProperty("tree")).ToDictionary(x => x.GetProperty("logicalNodeId").GetGuid());
        (string?, string) Status(Guid l) => (nodes[l].GetProperty("status").GetString(), string.Join(',', nodes[l].GetProperty("changes").EnumerateArray().Select(c => c.GetString())));
        Assert.Equal(("Modified", "ContentChanged"), Status(logical["A"]));
        Assert.Equal(("Removed", ""), Status(logical["B"]));
        Assert.Equal(("Removed", ""), Status(logical["B1"]));
        Assert.Equal(("Modified", "Renamed"), Status(logical["C"]));
        Assert.Equal(("Modified", "ContentChanged"), Status(logical["D"]));
        Assert.Equal(("Modified", "Moved"), Status(logical["E"]));
        Assert.Equal("Added", nodes.Values.Single(x => x.GetProperty("target").ValueKind != JsonValueKind.Null && x.GetProperty("target").GetProperty("title").GetString() == "F").GetProperty("status").GetString());
        Assert.Equal(1, nodes[logical["A"]].GetProperty("contentStats").GetProperty("inserted").GetInt32());
        Assert.Equal(("C", "C renamed"), (nodes[logical["C"]].GetProperty("base").GetProperty("title").GetString(), nodes[logical["C"]].GetProperty("target").GetProperty("title").GetString()));
        Assert.Equal(("5", "3.1"), (nodes[logical["E"]].GetProperty("base").GetProperty("number").GetString(), nodes[logical["E"]].GetProperty("target").GetProperty("number").GetString()));

        // Merged tree: target structure with the removed subtree at its base position (second root, child kept under it).
        var roots = comparison.GetProperty("tree").EnumerateArray().Select(r => r.GetProperty("logicalNodeId").GetGuid()).ToList();
        Assert.Equal(logical["B"], roots[1]);
        Assert.Equal([logical["B1"]], comparison.GetProperty("tree")[1].GetProperty("children").EnumerateArray().Select(c => c.GetProperty("logicalNodeId").GetGuid()));

        // Without unchanged nodes: nothing unchanged is left (here every node changed or is a parent of a change).
        var compact = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target={v2}", null, HttpStatusCode.OK);
        Assert.DoesNotContain(Flatten(compact.GetProperty("tree")), x => x.GetProperty("status").GetString() == "Unchanged" && x.GetProperty("children").GetArrayLength() == 0);

        var nodeDiff = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare/nodes/{logical["A"]}?base={v1}&target={v2}", null, HttpStatusCode.OK);
        Assert.Contains("changed</ins>", nodeDiff.GetProperty("html").GetString()!, StringComparison.Ordinal);
        var removedDiff = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare/nodes/{logical["B"]}?base={v1}&target={v2}", null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Array, removedDiff.GetProperty("blocks").ValueKind);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare/nodes/{Guid.NewGuid()}?base={v1}&target={v2}", null, HttpStatusCode.NotFound);

        // Same version: no changes. Cached result of two signed versions is identical on repeat.
        var same = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v2}&target={v2}", null, HttpStatusCode.OK);
        Assert.Empty(Flatten(same.GetProperty("tree")));
        var again = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target={v2}&includeUnchanged=true", null, HttpStatusCode.OK);
        Assert.Equal(comparison.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task Inserting_one_node_at_the_top_of_20_siblings_reorders_none()
    {
        var (documentId, v1) = await _arrange.CreateAsync();
        for (var i = 1; i <= 20; i++)
        {
            await AddAsync(v1, null, $"S{i}");
        }

        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "New first", position = 0 }, HttpStatusCode.Created);

        var comparison = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base=latestSigned&target=draft", null, HttpStatusCode.OK);
        Assert.Equal(["Added"], Flatten(comparison.GetProperty("tree")).Select(x => x.GetProperty("status").GetString()));
        Assert.Equal(20, comparison.GetProperty("summary").GetProperty("unchanged").GetInt32());

        // Swapping two siblings reorders one of them.
        var nodes = await NodesAsync(draft);
        var s20 = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/tree", null, HttpStatusCode.OK)).EnumerateArray()
            .Single(x => x.GetProperty("title").GetString() == "S20").GetProperty("id").GetInt32();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/nodes/{s20}/move", new { position = 1, rowVersion = await RowVersionAsync(s20) }, HttpStatusCode.OK);
        var swapped = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base=latestSigned&target=draft", null, HttpStatusCode.OK);
        var reordered = Flatten(swapped.GetProperty("tree")).Where(x => x.GetProperty("changes").EnumerateArray().Any(c => c.GetString() == "Reordered")).ToList();
        Assert.Equal(["S20"], reordered.Select(x => x.GetProperty("target").GetProperty("title").GetString()));
        _ = nodes;
    }

    [Fact]
    public async Task Removed_nodes_keep_their_base_order_among_added_and_later_siblings()
    {
        var (documentId, v1) = await _arrange.CreateAsync();
        for (var i = 1; i <= 11; i++)
        {
            await AddAsync(v1, null, $"R{i}");
        }

        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/tree", null, HttpStatusCode.OK);
        int Id(string title) => tree.EnumerateArray().Single(n => n.GetProperty("title").GetString() == title).GetProperty("id").GetInt32();
        foreach (var title in new[] { "R2", "R10" })
        {
            await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/nodes/{Id(title)}?rowVersion={Uri.EscapeDataString(await RowVersionAsync(Id(title)))}", null, HttpStatusCode.OK);
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "X", position = 0 }, HttpStatusCode.Created);
        var comparison = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base=latestSigned&target=draft&includeUnchanged=true", null, HttpStatusCode.OK);
        Assert.Equal(["X", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R11"],
            comparison.GetProperty("tree").EnumerateArray().Select(n => (n.GetProperty("target").ValueKind == JsonValueKind.Null ? n.GetProperty("base") : n.GetProperty("target")).GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Cached_comparisons_show_current_node_type_codes()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        var typeId = (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types", new { code = $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant(), name = "Temp", sortOrder = 99, isActive = true }, HttpStatusCode.Created));
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{v1}/tree", null, HttpStatusCode.OK);
        _ = tree;
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.DocumentNode SET NodeTypeId = @t WHERE DocumentVersionId = @v;", ("@t", typeId.GetProperty("id").GetInt32()), ("@v", v1));
        }

        var path = $"/api/documents/{documentId}/compare?base={v1}&target={v1}&includeUnchanged=true";
        var first = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
        Assert.Equal(typeId.GetProperty("code").GetString(), first.GetProperty("tree")[0].GetProperty("base").GetProperty("nodeType").GetString());
        var renamed = $"R{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{typeId.GetProperty("id").GetInt32()}",
            new { code = renamed, name = "Temp", sortOrder = 99, isActive = true, rowVersion = typeId.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        var second = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
        Assert.Equal(renamed, second.GetProperty("tree")[0].GetProperty("base").GetProperty("nodeType").GetString());
    }

    [Fact]
    public async Task Thousands_of_changed_and_script_edited_nodes_compare()
    {
        var (documentId, v1) = await _arrange.CreateAsync();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync(
                """
                WITH n AS (SELECT TOP (2200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                SELECT @v, NEWID(), 1, CONCAT(N'Node ', i), i * 1024, 2, 2 FROM n;
                INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHash, ModifiedByUserId)
                SELECT Id, DocumentVersionId, LogicalNodeId, N'{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"old"}]}]}', HASHBYTES('SHA2_256', N'old'), 2
                FROM app.DocumentNode WHERE DocumentVersionId = @v;
                """,
                ("@v", v1));
        }

        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("""UPDATE app.NodeContent SET ContentJson = N'{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"new"}]}]}' WHERE DocumentVersionId = @d;""", ("@d", draft));
        }

        var comparison = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target=draft", null, HttpStatusCode.OK);
        Assert.Equal(2200, comparison.GetProperty("summary").GetProperty("contentChanged").GetInt32());
    }

    [Fact]
    public async Task Parameters_are_validated_and_deleted_documents_hidden()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        var (_, otherDraft) = await _arrange.CreateAsync();
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target=draft", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target={otherDraft}", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base=bogus&target={v1}", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?target={v1}", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/compare?base=latestSigned&target={v1}", null, HttpStatusCode.OK);

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        foreach (var (user, expected) in new[] { (TestUsers.Bob, HttpStatusCode.NotFound), (TestUsers.Carol, HttpStatusCode.NotFound), (TestUsers.Alice, HttpStatusCode.OK), (TestUsers.Admin, HttpStatusCode.OK) })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{documentId}/compare?base={v1}&target={v1}", null, expected);
            var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{v1}/tree", null, HttpStatusCode.OK);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{documentId}/compare/nodes/{tree[0].GetProperty("logicalNodeId").GetGuid()}?base={v1}&target={v1}", null, expected);
        }
    }

    internal static IEnumerable<JsonElement> Flatten(JsonElement level) =>
        level.EnumerateArray().SelectMany(n => new[] { n }.Concat(Flatten(n.GetProperty("children"))));

    private async Task<int> AddAsync(int versionId, int? parent, string title) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{versionId}/nodes", new { parentNodeId = parent, nodeTypeId = 1, title }, HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

    private async Task<Guid> LogicalAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("logicalNodeId").GetGuid();

    private async Task<string> RowVersionAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString()!;

    private async Task<Dictionary<Guid, int>> NodesAsync(int versionId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        return (await dbo.QueryAsync("SELECT Id, LogicalNodeId FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId)))
            .ToDictionary(r => (Guid)r["LogicalNodeId"]!, r => (int)r["Id"]!);
    }

    private async Task SaveAsync(int nodeId, string text)
    {
        var rowVersion = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{nodeId}/content", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{nodeId}/content", new { contentJson = JsonNode.Parse(Paragraph(text)), rowVersion }, HttpStatusCode.OK);
    }
}

/// <summary>T12: compare of two 2 000-node versions &lt; 1 s (tagged, run alone).</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class ComparePerformanceTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Fact]
    public async Task Comparing_two_2000_node_versions_takes_under_1_s()
    {
        var arrange = new DocumentArrange(factory);
        var (documentId, v1) = await arrange.CreateAsync();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync(
                """
                DECLARE @parent INT = NULL, @i INT = 1;
                DECLARE @chain TABLE (Level INT NOT NULL PRIMARY KEY, Id INT NOT NULL);
                WHILE @i <= 14
                BEGIN
                    INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, ParentNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                    VALUES (@v, NEWID(), @parent, 1, CONCAT(N'Level ', @i), 1024, 2, 2);
                    SET @parent = SCOPE_IDENTITY();
                    INSERT INTO @chain (Level, Id) VALUES (@i, @parent);
                    SET @i += 1;
                END;
                WITH n AS (SELECT TOP (1986) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, ParentNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                SELECT @v, NEWID(), c.Id, 1, CONCAT(N'Node ', n.i), n.i * 1024, 2, 2 FROM n JOIN @chain c ON c.Level = 1 + n.i % 14;
                INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHtml, PlainText, ContentHash, ModifiedByUserId)
                SELECT Id, DocumentVersionId, LogicalNodeId, N'{"type":"doc","content":[]}', N'', N'', HASHBYTES('SHA2_256', CAST(Id AS NVARCHAR (20))), 2
                FROM app.DocumentNode WHERE DocumentVersionId = @v;
                """,
                ("@v", v1));
        }

        await arrange.GrantAsync(documentId, TestUsers.Carol);
        await arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE TOP (50) app.DocumentNode SET Title = CONCAT(Title, N' (renamed)') WHERE DocumentVersionId = @d;", ("@d", draft));
        }

        var path = $"/api/documents/{documentId}/compare?base={v1}&target=draft";
        for (var i = 0; i < 3; i++)
        {
            await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
            Assert.Equal(50, result.GetProperty("summary").GetProperty("renamed").GetInt32());
        }

        var median = times.Order().ElementAt(2);
        Assert.True(median < PerformanceBudget.For(TimeSpan.FromSeconds(1)), $"median {median.TotalMilliseconds:F0} ms");
    }
}
