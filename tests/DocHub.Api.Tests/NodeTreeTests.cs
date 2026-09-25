using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>T08: the node tree of a version through the API.</summary>
public sealed class NodeTreeTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    [Fact]
    public async Task The_example_tree_is_built_nested_ordered_and_numbered()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var chapter1 = await AddAsync(draft, null, "Chapter 1");
        await AddAsync(draft, chapter1, "Section 1");
        var section2 = await AddAsync(draft, chapter1, "Section 2");
        await AddAsync(draft, section2, "Subsection 1");
        await AddAsync(draft, section2, "Subsection 2");
        var chapter2 = await AddAsync(draft, null, "Chapter 2");
        var c2s1 = await AddAsync(draft, chapter2, "Section 1");
        await AddAsync(draft, c2s1, "Subsection 2");
        await AddAsync(draft, c2s1, "Subsection 1", position: 0);

        var tree = await TreeAsync(draft);

        Assert.Equal(
            ["1 Chapter 1", "1.1 Section 1", "1.2 Section 2", "1.2.1 Subsection 1", "1.2.2 Subsection 2", "2 Chapter 2", "2.1 Section 1", "2.1.1 Subsection 1", "2.1.2 Subsection 2"],
            Flatten(tree).Select(n => $"{n.GetProperty("number").GetString()} {n.GetProperty("title").GetString()}"));
        Assert.All(Flatten(tree), n => Assert.False(n.GetProperty("hasContent").GetBoolean()));

        var details = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/nodes/{c2s1}", null, HttpStatusCode.OK);
        Assert.Equal("2.1", details.GetProperty("number").GetString());
        Assert.Equal([chapter2], details.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task A_random_15_level_tree_round_trips()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var random = new Random(15);
        var created = new List<(int Id, int? Parent, int Depth)>();
        for (var i = 0; i < 60; i++)
        {
            var candidates = created.Where(c => c.Depth < 15).ToList();
            (int Id, int? Parent, int Depth)? parent = i < 15 ? (i == 0 ? null : created[^1]) : candidates[random.Next(candidates.Count)];
            var id = await AddAsync(draft, parent?.Id, $"Node {i}", position: random.Next(0, 3));
            created.Add((id, parent?.Id, (parent?.Depth ?? 0) + 1));
        }

        var nodes = Flatten(await TreeAsync(draft)).ToList();
        Assert.Equal(60, nodes.Count);
        Assert.Equal(15, nodes.Max(n => n.GetProperty("number").GetString()!.Split('.').Length));
        // Every node sits under the parent it was created under.
        var parentOf = new Dictionary<int, int?>();
        void Walk(JsonElement level, int? parent)
        {
            foreach (var n in level.EnumerateArray())
            {
                parentOf[n.GetProperty("id").GetInt32()] = parent;
                Walk(n.GetProperty("children"), n.GetProperty("id").GetInt32());
            }
        }

        Walk(await TreeAsync(draft), null);
        Assert.All(created, c => Assert.Equal(c.Parent, parentOf[c.Id]));
    }

    [Fact]
    public async Task A_100_level_tree_is_served_when_signed_and_cached()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        int? parent = null;
        for (var level = 1; level <= 100; level++)
        {
            parent = await AddAsync(draft, parent, $"Level {level}");
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { parentNodeId = parent, nodeTypeId = 1, title = "Too deep" }, HttpStatusCode.BadRequest);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(draft, TestUsers.Carol);

        // Twice: the second read comes from the cache.
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(100, Flatten(await TreeAsync(draft)).Max(n => n.GetProperty("number").GetString()!.Split('.').Length));
        }
    }

    [Fact]
    public async Task Reorder_and_move_update_the_numbering_and_cycles_are_rejected()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var a = await AddAsync(draft, null, "A");
        var b = await AddAsync(draft, null, "B");
        var a1 = await AddAsync(draft, a, "A1");
        var a1x = await AddAsync(draft, a1, "A1x");

        await MoveAsync(b, null, 0);
        Assert.Equal(["1 B", "2 A", "2.1 A1", "2.1.1 A1x"], Numbered(await TreeAsync(draft)));

        await MoveAsync(a1, b, null);
        Assert.Equal(["1 B", "1.1 A1", "1.1.1 A1x", "2 A"], Numbered(await TreeAsync(draft)));

        foreach (var into in new[] { a1, a1x })
        {
            var problem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/nodes/{a1}/move",
                new { newParentNodeId = into, rowVersion = await RowVersionAsync(a1) }, HttpStatusCode.Conflict);
            Assert.Equal("invalid-move", problem.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Rename_and_type_change_with_row_versions()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddAsync(draft, null, "Old");
        var rowVersion = await RowVersionAsync(node);

        var updated = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{node}", new { title = " New ", nodeTypeId = 2, rowVersion }, HttpStatusCode.OK);
        Assert.Equal("New", updated.GetProperty("title").GetString());
        Assert.Equal(2, updated.GetProperty("nodeTypeId").GetInt32());

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Patch, $"/api/nodes/{node}", new { title = "x", rowVersion }),
                     (HttpMethod.Post, $"/api/nodes/{node}/move", new { position = 0, rowVersion }),
                     (HttpMethod.Delete, $"/api/nodes/{node}?rowVersion={Uri.EscapeDataString(rowVersion)}", null),
                 })
        {
            var stale = await ApiClient.ExpectAsync(factory, TestUsers.Alice, method, path, body, HttpStatusCode.Conflict);
            Assert.Equal("concurrency-conflict", stale.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Titles_types_and_parents_are_validated()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var (_, otherDraft) = await _arrange.CreateAsync();
        var foreign = await AddAsync(otherDraft, null, "Foreign");
        var inactive = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types", new { code = $"OLD_{Random.Shared.Next(100000)}", name = "Old" }, HttpStatusCode.Created);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{inactive.GetProperty("id").GetInt32()}",
            new { code = inactive.GetProperty("code").GetString(), name = "Old", sortOrder = 0, isActive = false, rowVersion = inactive.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);

        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes",
            new { nodeTypeId = inactive.GetProperty("id").GetInt32(), title = "x" }, HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty("nodeTypeId", out _));
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "  " }, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = new string('x', 501) }, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "x", parentNodeId = foreign }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signed_versions_are_read_only_and_only_the_owner_edits_the_structure()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        var rowVersion = await RowVersionAsync(nodes[0]);
        var signedOps = StructuralOps(v1, nodes[0], rowVersion);
        foreach (var (method, path, body) in signedOps)
        {
            var problem = await ApiClient.ExpectAsync(factory, TestUsers.Alice, method, path, body, HttpStatusCode.Conflict);
            Assert.Equal("version-not-editable", problem.GetProperty("type").GetString());
        }

        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftNode = Flatten(await TreeAsync(draft)).First().GetProperty("id").GetInt32();
        await _arrange.GrantAsync(documentId, TestUsers.Erin, role: 1);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync(
                "INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId) SELECT @d, 3, 1, LogicalNodeId, 2 FROM app.DocumentNode WHERE Id = @n;",
                ("@d", documentId), ("@n", draftNode));
        }

        foreach (var user in new[] { TestUsers.Carol, TestUsers.Erin, TestUsers.Bob, TestUsers.Admin })
        {
            foreach (var (method, path, body) in StructuralOps(draft, draftNode, await RowVersionAsync(draftNode)))
            {
                await ApiClient.ExpectAsync(factory, user, method, path, body, HttpStatusCode.Forbidden);
            }
        }
    }

    [Fact]
    public async Task Deleting_a_node_removes_its_subtree_and_audits_every_row()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var root = await AddAsync(draft, null, "Root");
        var keep = await AddAsync(draft, null, "Keep");
        var level = new List<int> { root };
        var descendants = 0;
        while (descendants < 50)
        {
            var next = new List<int>();
            foreach (var parent in level)
            {
                for (var i = 0; i < 3 && descendants < 50; i++, descendants++)
                {
                    next.Add(await AddAsync(draft, parent, $"D{descendants}"));
                }
            }

            level = next;
        }

        var result = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/nodes/{root}?rowVersion={Uri.EscapeDataString(await RowVersionAsync(root))}", null, HttpStatusCode.OK);

        Assert.Equal(51, result.GetProperty("deletedCount").GetInt32());
        Assert.Equal([keep], Flatten(await TreeAsync(draft)).Select(n => n.GetProperty("id").GetInt32()));
        var audit = await Db.QueryAsync(factory,
            "SELECT TableName, COUNT(*) AS Rows FROM audit.ChangeLog WHERE DocumentVersionId = @v AND Operation = 'D' GROUP BY TableName;", ("@v", draft));
        Assert.Equal(51, audit.Single(r => (string)r["TableName"]! == "app.DocumentNode")["Rows"]);
        Assert.Equal(51, audit.Single(r => (string)r["TableName"]! == "app.NodeContent")["Rows"]);
    }

    [Fact]
    public async Task Tree_revalidation_follows_the_stamp_and_deleted_documents_are_hidden_from_others()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        using var bob = factory.CreateClientFor(TestUsers.Bob);
        using var first = await bob.GetAsync(new Uri($"/api/versions/{v1}/tree", UriKind.Relative), TestContext.Current.CancellationToken);
        var etag = first.Headers.ETag!;
        Assert.Equal(HttpStatusCode.NotModified, (await SendAsync(bob, v1, etag)).StatusCode);

        // A script edit of the signed version: new stamp, new data (no stale 304, no stale cache).
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Tampered' WHERE Id = @n;", ("@n", nodes[0]));
        }

        using (var fresh = await SendAsync(bob, v1, etag))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Contains("Tampered", await fresh.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(bob, v1, etag)).StatusCode);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/nodes/{nodes[0]}", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{v1}/tree", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/nodes/{nodes[0]}", null, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Structural_changes_are_audited_as_the_owner()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = await AddAsync(draft, null, "Audited");

        var log = await Db.QueryAsync(factory, "SELECT TableName, Source, UserId FROM audit.ChangeLog WHERE DocumentVersionId = @v AND Operation = 'I' AND TableName <> N'app.DocumentVersion';", ("@v", draft));
        Assert.Equal(["app.DocumentNode", "app.NodeContent"], log.Select(r => (string)r["TableName"]!).Order(StringComparer.Ordinal));
        Assert.All(log, r => Assert.Equal(("App", (object?)TestUsers.Alice), ((string)r["Source"]!, r["UserId"])));
        Assert.NotEqual(0, node);
    }

    [Fact]
    public async Task Structural_edits_racing_the_last_signature_never_change_the_signed_version()
    {
        for (var round = 0; round < 8; round++)
        {
            var (documentId, draft) = await _arrange.CreateAsync();
            var nodes = await _arrange.AddNodesAsync(draft);
            await _arrange.GrantAsync(documentId, TestUsers.Carol);
            var rowVersion = await RowVersionAsync(nodes[1]);

            var edits = Enumerable.Range(0, 6).Select(i => ApiClient.SendAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = $"Late {i}" }))
                .Append(ApiClient.SendAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{nodes[1]}", new { title = "Renamed late", rowVersion }))
                .ToList();
            var sign = ApiClient.SendAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/versions/{draft}/signatures", new { });
            var results = await Task.WhenAll(edits.Append(sign));

            Assert.All(results.SkipLast(1), r => Assert.Contains(r.Status, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
            // Whatever the order, the signed content is exactly what was signed: no node/content row after finalization.
            var late = await Db.QueryAsync(factory,
                """
                SELECT COUNT(*) AS Late FROM audit.ChangeLog c
                WHERE c.DocumentVersionId = @v AND c.TableName IN (N'app.DocumentNode', N'app.NodeContent')
                  AND c.Id > (SELECT MAX(f.Id) FROM audit.ChangeLog f WHERE f.TableName = N'app.DocumentVersion' AND f.EntityId = @v AND f.ChangedColumns LIKE N'%Status%');
                """, ("@v", draft));
            Assert.Equal(0, late[0]["Late"]);
            Assert.False((await _arrange.VersionAsync(draft)).GetProperty("modifiedAfterSigning").GetBoolean());
            foreach (var r in results)
            {
                r.Response.Dispose();
            }
        }
    }

    private static (HttpMethod, string, object?)[] StructuralOps(int versionId, int nodeId, string rowVersion) =>
    [
        (HttpMethod.Post, $"/api/versions/{versionId}/nodes", new { nodeTypeId = 1, title = "x" }),
        (HttpMethod.Patch, $"/api/nodes/{nodeId}", new { title = "x", rowVersion }),
        (HttpMethod.Post, $"/api/nodes/{nodeId}/move", new { position = 0, rowVersion }),
        (HttpMethod.Delete, $"/api/nodes/{nodeId}?rowVersion={Uri.EscapeDataString(rowVersion)}", null),
    ];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, int versionId, System.Net.Http.Headers.EntityTagHeaderValue etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/versions/{versionId}/tree", UriKind.Relative));
        request.Headers.IfNoneMatch.Add(etag);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<int> AddAsync(int versionId, int? parentNodeId, string title, int? position = null) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{versionId}/nodes", new { parentNodeId, nodeTypeId = 1, title, position }, HttpStatusCode.Created))
        .GetProperty("id").GetInt32();

    private async Task MoveAsync(int nodeId, int? newParentNodeId, int? position) =>
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/nodes/{nodeId}/move", new { newParentNodeId, position, rowVersion = await RowVersionAsync(nodeId) }, HttpStatusCode.OK);

    private async Task<string> RowVersionAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString()!;

    private Task<JsonElement> TreeAsync(int versionId) => ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{versionId}/tree", null, HttpStatusCode.OK);

    private static IEnumerable<JsonElement> Flatten(JsonElement level) =>
        level.EnumerateArray().SelectMany(n => new[] { n }.Concat(Flatten(n.GetProperty("children"))));

    private static List<string> Numbered(JsonElement tree) =>
        Flatten(tree).Select(n => $"{n.GetProperty("number").GetString()} {n.GetProperty("title").GetString()}").ToList();
}

/// <summary>NFR-6: a 2 000-node, 15-level tree loads in under 500 ms (tagged, run alone).</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class TreeLoadPerformanceTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Fact]
    public async Task A_2000_node_tree_loads_in_under_500_ms()
    {
        var (_, draft) = await new DocumentArrange(factory).CreateAsync();
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
                INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, PlainText, ContentHash, ModifiedByUserId)
                SELECT Id, DocumentVersionId, LogicalNodeId, N'{"type":"doc","content":[]}', REPLICATE(N'x', 2000), HASHBYTES('SHA2_256', N''), 2
                FROM app.DocumentNode WHERE DocumentVersionId = @v;
                """,
                ("@v", draft));
        }

        var path = $"/api/versions/{draft}/tree";
        for (var i = 0; i < 3; i++)
        {
            await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, path, null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
        }

        var median = times.Order().ElementAt(2);
        Assert.True(median < PerformanceBudget.For(TimeSpan.FromMilliseconds(500)), $"median {median.TotalMilliseconds:F0} ms");
    }
}
