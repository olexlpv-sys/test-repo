using System.Diagnostics;
using DocHub.Testing;
using DocHub.Testing.Database;
using Microsoft.Data.SqlClient;

namespace DocHub.Database.Tests;

/// <summary>T07/T10 procedures: usp_CheckPermission, usp_GetEffectivePermissions, usp_CopyVersionToDraft, usp_ListDocuments (FR-D2, FR-D3, FR-D5).</summary>
public sealed class DocumentProcedureTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private const int Admin = TestData.AdminUserId;
    private const int Owner = TestData.AliceUserId;
    private const int Other = TestData.BobUserId;
    private const int Approver = TestData.CarolUserId;
    private const int NodeEditor = 5; // dave
    private const int DocEditor = 6;  // erin

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int, bool, bool> PermissionMatrix()
    {
        var data = new TheoryData<string, int, bool, bool>();
        void Row(string action, int user, bool active, bool deleted) => data.Add(action, user, active, deleted);
        Row("View", Owner, true, true); Row("View", Other, true, false); Row("View", Admin, true, true); Row("View", Approver, true, false);
        Row("Manage", Owner, true, false); Row("Manage", Admin, false, false); Row("Manage", Approver, false, false); Row("Manage", DocEditor, false, false);
        Row("EditStructure", Owner, true, false); Row("EditStructure", DocEditor, false, false);
        Row("EditContent", Owner, true, false); Row("EditContent", DocEditor, true, false); Row("EditContent", Approver, false, false); Row("EditContent", Other, false, false);
        Row("Comment", Owner, true, false); Row("Comment", Approver, true, false); Row("Comment", DocEditor, true, false); Row("Comment", Other, false, false);
        Row("Resolve", Owner, true, false); Row("Resolve", Approver, true, false); Row("Resolve", DocEditor, false, false);
        Row("Sign", Approver, true, false); Row("Sign", Owner, false, false); Row("Sign", DocEditor, false, false); Row("Sign", Admin, false, false);
        Row("Move", Owner, true, false); Row("Move", Admin, true, true); Row("Move", Other, false, false);
        Row("Restore", Owner, true, true); Row("Restore", Admin, true, true); Row("Restore", Other, false, false);
        return data;
    }

    [Theory]
    [MemberData(nameof(PermissionMatrix))]
    public async Task Check_permission_follows_the_role_matrix(string action, int user, bool allowedWhenActive, bool allowedWhenDeleted)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (documentId, _) = await DocumentWithGrantsAsync(db);

        Assert.Equal(allowedWhenActive, await CheckAsync(db, documentId, user, action));
        await db.ExecuteAsync("UPDATE app.Document SET DeletedAt = SYSUTCDATETIME(), DeletedByUserId = @u WHERE Id = @d;", ("@u", Owner), ("@d", documentId));
        Assert.Equal(allowedWhenDeleted, await CheckAsync(db, documentId, user, action));
    }

    [Fact]
    public async Task Node_editors_edit_their_node_and_its_descendants_only()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (documentId, versionId) = await DocumentWithGrantsAsync(db);
        var granted = await db.ScalarAsync<Guid>("SELECT LogicalNodeId FROM app.DocumentPermission WHERE DocumentId = @d AND UserId = @u;", ("@d", documentId), ("@u", NodeEditor));
        var nodes = await db.QueryAsync("SELECT Id, LogicalNodeId, ParentNodeId FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId));
        var grantedId = (int)nodes.Single(n => (Guid)n["LogicalNodeId"]! == granted)["Id"]!;
        var child = (Guid)nodes.Single(n => (int?)n["ParentNodeId"] == grantedId)["LogicalNodeId"]!;
        var sibling = (Guid)nodes.First(n => n["ParentNodeId"] is null && (Guid)n["LogicalNodeId"]! != granted)["LogicalNodeId"]!;

        Assert.True(await CheckAsync(db, documentId, NodeEditor, "EditContent", versionId, granted));
        Assert.True(await CheckAsync(db, documentId, NodeEditor, "EditContent", versionId, child));
        Assert.False(await CheckAsync(db, documentId, NodeEditor, "EditContent", versionId, sibling));
        Assert.False(await CheckAsync(db, documentId, NodeEditor, "EditContent"));
    }

    public static TheoryData<int> MatrixUsers() => new(Owner, Other, Approver, NodeEditor, DocEditor, Admin, 0, 999999);

    [Theory]
    [MemberData(nameof(MatrixUsers))]
    public async Task Effective_permissions_agree_with_every_single_check(int user)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (documentId, versionId) = await DocumentWithGrantsAsync(db);
        foreach (var deleted in new[] { false, true })
        {
            if (deleted)
            {
                await db.ExecuteAsync("UPDATE app.Document SET DeletedAt = SYSUTCDATETIME(), DeletedByUserId = @u WHERE Id = @d;", ("@u", Owner), ("@d", documentId));
            }

            var (flags, nodes) = await EffectiveAsync(db, documentId, user, versionId);
            foreach (var (flag, action) in new[]
            {
                ("CanView", "View"), ("CanEditStructure", "EditStructure"), ("CanEditAllContent", "EditContent"), ("CanComment", "Comment"),
                ("CanResolve", "Resolve"), ("CanSign", "Sign"), ("CanManage", "Manage"), ("CanMove", "Move"), ("CanRestore", "Restore"),
            })
            {
                Assert.True(await CheckAsync(db, documentId, user, action) == (bool)flags[flag]!, $"{flag} of user {user}, deleted = {deleted}");
            }

            // Every node the set lists is editable by the single check, and no other node of the version is (unless all are).
            var all = await db.QueryAsync("SELECT LogicalNodeId FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId));
            foreach (var node in all.Select(n => (Guid)n["LogicalNodeId"]!))
            {
                var single = await CheckAsync(db, documentId, user, "EditContent", versionId, node);
                Assert.Equal(single, (bool)flags["CanEditAllContent"]! || nodes.Contains(node));
            }

            Assert.Equal(versionId, flags["DocumentVersionId"]);
        }
    }

    [Fact]
    public async Task Effective_permissions_expand_node_grants_in_the_given_version_and_default_to_the_draft()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (documentId, versionId) = await DocumentWithGrantsAsync(db);
        var nodes = await db.QueryAsync("SELECT Id, LogicalNodeId, ParentNodeId FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId));
        var root = nodes.Single(n => n["ParentNodeId"] is null && nodes.Any(c => (int?)c["ParentNodeId"] == (int)n["Id"]!));
        var child = nodes.Single(n => n["ParentNodeId"] is not null);
        var other = nodes.Single(n => n["ParentNodeId"] is null && n != root);

        var (flags, editable) = await EffectiveAsync(db, documentId, NodeEditor, null);
        Assert.False((bool)flags["CanEditAllContent"]!);
        Assert.True((bool)flags["CanComment"]!);
        Assert.Equal(new[] { (Guid)root["LogicalNodeId"]!, (Guid)child["LogicalNodeId"]! }.Order(), editable.Order());

        // A draft where the child moved under "Other": the grant no longer covers it there, the old version is unaffected.
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME(), SignedContentHash = HASHBYTES('SHA2_256', N'x') WHERE Id = @v;", ("@v", versionId));
        var draft = await db.Data.VersionAsync(documentId, isCurrent: false);
        var draftRoot = await db.Data.NodeAsync(draft, title: "Root", sortOrder: 1024, logicalNodeId: (Guid)root["LogicalNodeId"]!);
        var draftOther = await db.Data.NodeAsync(draft, title: "Other", sortOrder: 2048, logicalNodeId: (Guid)other["LogicalNodeId"]!);
        await db.Data.NodeAsync(draft, draftOther, "Child", 1024, logicalNodeId: (Guid)child["LogicalNodeId"]!);
        Assert.Equal([(Guid)root["LogicalNodeId"]!], (await EffectiveAsync(db, documentId, NodeEditor, null)).Nodes);
        Assert.Equal(draft, (await EffectiveAsync(db, documentId, NodeEditor, null)).Flags["DocumentVersionId"]);
        Assert.Equal(2, (await EffectiveAsync(db, documentId, NodeEditor, versionId)).Nodes.Count);
        Assert.False(await CheckAsync(db, documentId, NodeEditor, "EditContent", draft, (Guid)child["LogicalNodeId"]!));
        Assert.True(await CheckAsync(db, documentId, NodeEditor, "EditContent", versionId, (Guid)child["LogicalNodeId"]!));
        _ = draftRoot;

        // A version of another document is ignored (no nodes), and editors of all content get no node list.
        var (_, otherVersion) = await DocumentWithGrantsAsync(db);
        Assert.Empty((await EffectiveAsync(db, documentId, NodeEditor, otherVersion)).Nodes);
        Assert.Empty((await EffectiveAsync(db, documentId, DocEditor, null)).Nodes);
    }

    [Fact]
    public async Task Check_permission_rejects_unknown_actions_and_missing_documents()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        var error = await Assert.ThrowsAsync<SqlException>(() => CheckAsync(db, 1, Owner, "Delete"));
        Assert.Equal(50010, error.Number);
        Assert.False(await CheckAsync(db, 999999, Admin, "View"));
    }

    [Fact]
    public async Task Copy_to_draft_keeps_logical_ids_remaps_parents_and_copies_content_and_style_usage()
    {
        await using var db = await SqlSession.OpenAsync(database.ConnectionString);
        var (documentId, source) = await DocumentWithGrantsAsync(db);
        await db.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) SELECT 'Strong', NodeId FROM app.NodeContent WHERE DocumentVersionId = @v;", ("@v", source));
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;", ("@v", source));
        await db.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'OperationContext', @value = N'Caller';");

        var copy = await db.ScalarAsync<int>("EXEC app.usp_CopyVersionToDraft @SourceVersionId = @s, @UserId = @u;", ("@s", source), ("@u", Owner));

        const string Tree = """
            SELECT CONCAT(n.LogicalNodeId, '|', p.LogicalNodeId, '|', n.NodeTypeId, '|', n.Title, '|', n.SortOrder, '|', c.ContentJson, '|', c.ContentHtml, '|',
                          CONVERT(VARCHAR (64), c.ContentHash, 2), '|', (SELECT COUNT(*) FROM app.ContentStyleUsage u WHERE u.NodeId = n.Id)) AS Row
            FROM app.DocumentNode n LEFT JOIN app.DocumentNode p ON p.Id = n.ParentNodeId JOIN app.NodeContent c ON c.NodeId = n.Id
            WHERE n.DocumentVersionId = @v ORDER BY n.LogicalNodeId;
            """;
        Assert.Equal((await db.QueryAsync(Tree, ("@v", source))).Select(r => r["Row"]), (await db.QueryAsync(Tree, ("@v", copy))).Select(r => r["Row"]));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentNode n JOIN app.DocumentNode p ON p.Id = n.ParentNodeId WHERE n.DocumentVersionId = @v AND p.DocumentVersionId <> @v;", ("@v", copy)));

        var version = Assert.Single(await db.QueryAsync("SELECT Status, BasedOnVersionId, IsCurrent, CreatedByUserId FROM app.DocumentVersion WHERE Id = @v;", ("@v", copy)));
        Assert.Equal((byte)1, version["Status"]);
        Assert.Equal(source, version["BasedOnVersionId"]);
        Assert.Equal(true, version["IsCurrent"]);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentVersion WHERE DocumentId = @d AND IsCurrent = 1;", ("@d", documentId)));

        // Audit rows of the copy are labelled; the caller's context is restored.
        var contexts = await db.QueryAsync("SELECT DISTINCT OperationContext FROM audit.ChangeLog WHERE DocumentVersionId = @v AND TableName IN (N'app.DocumentNode', N'app.NodeContent');", ("@v", copy));
        Assert.Equal(["CopyVersion"], contexts.Select(r => (string)r["OperationContext"]!));
        Assert.Equal("Caller", await db.ScalarAsync<string>("SELECT CAST(SESSION_CONTEXT(N'OperationContext') AS NVARCHAR (50));"));

        // A second draft fails and leaves the context as it was.
        var second = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync("EXEC app.usp_CopyVersionToDraft @SourceVersionId = @s, @UserId = @u;", ("@s", source), ("@u", Owner)));
        Assert.Contains("UX_DocumentVersion_OneDraft", second.Message, StringComparison.Ordinal);
        Assert.Equal("Caller", await db.ScalarAsync<string>("SELECT CAST(SESSION_CONTEXT(N'OperationContext') AS NVARCHAR (50));"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentVersion WHERE DocumentId = @d AND Status = 1;", ("@d", documentId)));
    }

    [Fact]
    public async Task Delete_subtree_removes_the_node_its_descendants_and_their_content()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, versionId) = await DocumentWithGrantsAsync(db);
        var root = await db.ScalarAsync<int>("SELECT Id FROM app.DocumentNode WHERE DocumentVersionId = @v AND Title = N'Root';", ("@v", versionId));

        Assert.Equal(2, await db.ScalarAsync<int>("EXEC app.usp_DeleteSubtree @NodeId = @n;", ("@n", root)));

        Assert.Equal(["Other"], (await db.QueryAsync("SELECT Title FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId))).Select(r => (string)r["Title"]!));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.NodeContent WHERE DocumentVersionId = @v;", ("@v", versionId)));
        var error = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync("EXEC app.usp_DeleteSubtree @NodeId = 999999;"));
        Assert.Equal(50050, error.Number);
    }

    [Theory]
    [InlineData("@SortBy = 'x'", 50021)]
    [InlineData("@Status = 'x'", 50020)]
    [InlineData("@SortDir = 'up'", 50022)]
    [InlineData("@PageSize = 0", 50023)]
    public async Task List_documents_validates_its_parameters(string parameter, int error)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync($"EXEC app.usp_ListDocuments @FolderId = 1, @UserId = 2, {parameter};"));
        Assert.Equal(error, exception.Number);
    }

    [Fact]
    public async Task Every_document_with_versions_has_exactly_one_current_version()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        Assert.Equal(0, await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM app.Document d WHERE EXISTS (SELECT 1 FROM app.DocumentVersion v WHERE v.DocumentId = d.Id AND v.Status <> 3) AND (SELECT COUNT(*) FROM app.DocumentVersion v WHERE v.DocumentId = d.Id AND v.IsCurrent = 1) <> 1;"));
    }

    private static async Task<(IReadOnlyDictionary<string, object?> Flags, List<Guid> Nodes)> EffectiveAsync(RolledBackScope db, int documentId, int userId, int? versionId)
    {
        await using var command = new SqlCommand("app.usp_GetEffectivePermissions", db.Connection, db.Transaction) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@DocumentId", documentId);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@DocumentVersionId", (object?)versionId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var flags = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
        Assert.True(await reader.NextResultAsync(Ct));
        var nodes = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            nodes.Add(reader.GetGuid(0));
        }

        return (flags, nodes);
    }

    private static async Task<bool> CheckAsync(RolledBackScope db, int documentId, int userId, string action, int? versionId = null, Guid? logicalNodeId = null) =>
        await db.ScalarAsync<bool>("EXEC app.usp_CheckPermission @DocumentId = @d, @UserId = @u, @Action = @a, @DocumentVersionId = @v, @LogicalNodeId = @l;",
            ("@d", documentId), ("@u", userId), ("@a", action), ("@v", versionId), ("@l", logicalNodeId));

    /// <summary>Alice's document: two root nodes (the first with a child), approver carol, document editor erin, node editor dave.</summary>
    private static async Task<(int DocumentId, int VersionId)> DocumentWithGrantsAsync(ISqlCommands db)
    {
        var data = new TestData(db);
        var documentId = await data.DocumentAsync(ownerUserId: Owner);
        var versionId = await data.VersionAsync(documentId, isCurrent: true);
        var root = await data.NodeAsync(versionId, title: "Root", sortOrder: 1024);
        var child = await data.NodeAsync(versionId, root, "Child", 1024);
        var other = await data.NodeAsync(versionId, title: "Other", sortOrder: 2048);
        foreach (var node in new[] { root, child, other })
        {
            await data.ContentAsync(node);
        }

        var rootLogical = await db.ScalarAsync<Guid>("SELECT LogicalNodeId FROM app.DocumentNode WHERE Id = @n;", ("@n", root));
        await db.ExecuteAsync(
            """
            INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId) VALUES
                (@d, @approver, 2, NULL, @owner), (@d, @docEditor, 1, NULL, @owner), (@d, @nodeEditor, 1, @root, @owner);
            """,
            ("@d", documentId), ("@approver", Approver), ("@docEditor", DocEditor), ("@nodeEditor", NodeEditor), ("@root", rootLogical), ("@owner", Owner));
        return (documentId, versionId);
    }
}

/// <summary>T07: deep copy of a large tree (tagged performance test, run alone).</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class DeepCopyPerformanceTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    [Fact]
    public async Task Deep_copy_of_a_2000_node_depth_15_tree_takes_under_2_s()
    {
        await using var db = await SqlSession.OpenAsync(database.ConnectionString);
        var documentId = await db.Data.DocumentAsync();
        var source = await db.Data.VersionAsync(documentId, TestData.StatusSigned, versionNumber: 1);
        await db.ExecuteAsync(
            """
            -- Depth 15: a chain of 14 nodes, the other 1 986 nodes spread as children over the chain (set-based).
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
            INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHash, ModifiedByUserId)
            SELECT Id, DocumentVersionId, LogicalNodeId, N'{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"' + REPLICATE(N'x', 2000) + N'"}]}]}',
                   HASHBYTES('SHA2_256', CAST(Id AS NVARCHAR (20))), 2
            FROM app.DocumentNode WHERE DocumentVersionId = @v;
            """,
            ("@v", source));

        // As the API calls it: with the acting user in the session context (Source = 'App').
        await db.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'UserId', @value = 2;");
        var watch = Stopwatch.StartNew();
        var copy = await db.ScalarAsync<int>("EXEC app.usp_CopyVersionToDraft @SourceVersionId = @s, @UserId = 2;", ("@s", source));
        watch.Stop();

        Assert.Equal(2000, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.NodeContent WHERE DocumentVersionId = @v;", ("@v", copy)));
        Assert.True(watch.Elapsed < PerformanceBudget.For(TimeSpan.FromSeconds(2)), $"copy took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }
}

/// <summary>T10 / FR-D5: permission check &lt; 10 ms and effective permissions &lt; 50 ms on the NFR-6 tree (tagged, run alone).</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class PermissionPerformanceTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    [Fact]
    public async Task Checks_on_a_2000_node_depth_15_tree_meet_their_targets()
    {
        await using var db = await SqlSession.OpenAsync(database.ConnectionString);
        var documentId = await db.Data.DocumentAsync();
        var version = await db.Data.VersionAsync(documentId, isCurrent: true);
        await db.ExecuteAsync(
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
            -- Node editor dave: the root (covers all 2 000 nodes) and a deep chain node; approver carol.
            INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId)
            SELECT @d, 5, 1, LogicalNodeId, 2 FROM app.DocumentNode WHERE DocumentVersionId = @v AND Title IN (N'Level 1', N'Level 10');
            INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, GrantedByUserId) VALUES (@d, 4, 2, 2);
            """,
            ("@v", version), ("@d", documentId));
        var deepest = await db.ScalarAsync<Guid>(
            "SELECT TOP (1) n.LogicalNodeId FROM app.DocumentNode n JOIN app.DocumentNode p ON p.Id = n.ParentNodeId WHERE n.DocumentVersionId = @v AND p.Title = N'Level 14';", ("@v", version));

        var check = await MedianAsync(() => db.ScalarAsync<bool>(
            "EXEC app.usp_CheckPermission @DocumentId = @d, @UserId = 6, @Action = 'EditContent', @DocumentVersionId = @v, @LogicalNodeId = @l;",
            ("@d", documentId), ("@v", version), ("@l", deepest)));
        var effective = await MedianAsync(async () =>
        {
            await using var command = new SqlCommand("app.usp_GetEffectivePermissions", db.Connection) { CommandType = System.Data.CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@DocumentId", documentId);
            command.Parameters.AddWithValue("@UserId", 5);
            command.Parameters.AddWithValue("@DocumentVersionId", version);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            await reader.NextResultAsync(TestContext.Current.CancellationToken);
            var nodes = 0;
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                nodes++;
            }

            Assert.Equal(2000, nodes);
            return nodes;
        });

        // A user without a grant walks the whole ancestor chain (the slowest single check).
        Assert.True(check < PerformanceBudget.For(TimeSpan.FromMilliseconds(10)), $"check {check.TotalMilliseconds:F1} ms");
        Assert.True(effective < PerformanceBudget.For(TimeSpan.FromMilliseconds(50)), $"effective {effective.TotalMilliseconds:F1} ms");
    }

    /// <summary>Median of 5 runs after 3 warm-ups.</summary>
    private static async Task<TimeSpan> MedianAsync<T>(Func<Task<T>> run)
    {
        for (var i = 0; i < 3; i++)
        {
            await run();
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            await run();
            times.Add(watch.Elapsed);
        }

        return times.Order().ElementAt(2);
    }
}
