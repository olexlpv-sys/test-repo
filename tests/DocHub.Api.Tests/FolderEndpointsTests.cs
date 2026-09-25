using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>T06: virtual folders through the API (FR-F1…F5).</summary>
public sealed class FolderEndpointsTests(DocHubApiWithTestEndpointsFactory factory) : IClassFixture<DocHubApiWithTestEndpointsFactory>
{
    [Fact]
    public async Task A_support_script_cannot_create_a_folder_cycle()
    {
        var a = await CreateAsync(null, $"Cycle A {Guid.NewGuid():N}");
        var b = await CreateAsync(a, "B");
        var c = await CreateAsync(b, "C");
        await using var support = await SqlSession.OpenAsync(factory.AdminConnectionString);

        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            support.ExecuteAsync("UPDATE app.Folder SET ParentFolderId = @c WHERE Id = @a;", ("@c", c), ("@a", a)));

        Assert.Equal(50040, error.Number);
        // The tree stays intact and moves keep working.
        var x = await CreateAsync(null, $"Cycle X {Guid.NewGuid():N}");
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{x}/move",
            new { newParentFolderId = c, rowVersion = await RowVersionAsync(x) }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lost_races_with_a_deleted_or_referenced_folder_map_to_not_found_and_in_use()
    {
        var parent = await CreateAsync(null, $"Race {Guid.NewGuid():N}");
        await CreateAsync(parent, "Child");

        var inUse = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/__test/folders/raw/{parent}", null, HttpStatusCode.Conflict);
        Assert.Equal("in-use", inUse.GetProperty("type").GetString());

        var missing = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/__test/folders/raw/999999", null, HttpStatusCode.NotFound);
        Assert.Equal("not-found", missing.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_five_level_hierarchy_is_returned_nested_and_ordered()
    {
        var root = await CreateAsync(null, $"Root {Guid.NewGuid():N}");
        int? parent = root;
        var chain = new List<int> { root };
        for (var level = 2; level <= 5; level++)
        {
            parent = await CreateAsync(parent, $"Level {level}");
            chain.Add(parent.Value);
        }

        var b = await CreateAsync(root, "B sibling");
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, "/api/folders/tree", null, HttpStatusCode.OK);
        var node = tree.EnumerateArray().Single(f => f.GetProperty("id").GetInt32() == root);
        Assert.Equal([chain[1], b], node.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetInt32())); // creation order = append
        for (var level = 1; level < 5; level++)
        {
            node = node.GetProperty("children").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == chain[level]);
        }

        Assert.Empty(node.GetProperty("children").EnumerateArray());

        var details = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, $"/api/folders/{chain[4]}", null, HttpStatusCode.OK);
        Assert.Equal(chain, details.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()));
        Assert.Equal(chain[3], details.GetProperty("parentFolderId").GetInt32());
    }

    [Fact]
    public async Task Document_count_counts_non_deleted_documents_directly_in_the_folder()
    {
        var folder = await CreateAsync(null, $"Counted {Guid.NewGuid():N}");
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.Data.DocumentAsync("a", folder);
            await dbo.Data.DocumentAsync("b", folder);
            var deleted = await dbo.Data.DocumentAsync("c", folder);
            await dbo.ExecuteAsync("UPDATE app.Document SET DeletedAt = SYSUTCDATETIME(), DeletedByUserId = 1 WHERE Id = @d;", ("@d", deleted));
        }

        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/folders/tree", null, HttpStatusCode.OK);
        Assert.Equal(2, tree.EnumerateArray().Single(f => f.GetProperty("id").GetInt32() == folder).GetProperty("documentCount").GetInt32());
    }

    [Fact]
    public async Task Rename_with_a_stale_row_version_is_a_concurrency_conflict()
    {
        var id = await CreateAsync(null, $"Rename {Guid.NewGuid():N}");
        var original = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/folders/{id}", null, HttpStatusCode.OK);
        var rowVersion = original.GetProperty("rowVersion").GetString();

        var renamed = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/folders/{id}", new { name = "  Renamed  ", rowVersion }, HttpStatusCode.OK);
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());

        var stale = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/folders/{id}", new { name = "Again", rowVersion }, HttpStatusCode.Conflict);
        Assert.Equal("concurrency-conflict", stale.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Moving_into_itself_or_a_descendant_is_invalid_and_a_valid_move_changes_the_tree()
    {
        var a = await CreateAsync(null, $"Move A {Guid.NewGuid():N}");
        var child = await CreateAsync(a, "Child");
        var grandchild = await CreateAsync(child, "Grandchild");
        var target = await CreateAsync(null, $"Move T {Guid.NewGuid():N}");
        var first = await CreateAsync(target, "First");
        var second = await CreateAsync(target, "Second");

        foreach (var into in new[] { a, grandchild })
        {
            var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{a}/move",
                new { newParentFolderId = into, rowVersion = await RowVersionAsync(a) }, HttpStatusCode.Conflict);
            Assert.Equal("invalid-move", problem.GetProperty("type").GetString());
        }

        // Child moves between First and Second of the target.
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{child}/move",
            new { newParentFolderId = target, position = 1, rowVersion = await RowVersionAsync(child) }, HttpStatusCode.OK);
        Assert.Equal([first, child, second], await ChildrenAsync(target));
        Assert.Empty(await ChildrenAsync(a));

        // Moving to the root (no parent), first position; the grandchild comes along.
        var moved = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{child}/move",
            new { newParentFolderId = (int?)null, position = 0, rowVersion = await RowVersionAsync(child) }, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("parentFolderId").ValueKind);
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/folders/tree", null, HttpStatusCode.OK);
        Assert.Equal(child, tree[0].GetProperty("id").GetInt32());
        Assert.Equal(grandchild, tree[0].GetProperty("children")[0].GetProperty("id").GetInt32());

        var stale = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{first}/move",
            new { newParentFolderId = a, rowVersion = "AAAAAAAAAAA=" }, HttpStatusCode.Conflict);
        Assert.Equal("concurrency-conflict", stale.GetProperty("type").GetString());
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{first}/move",
            new { newParentFolderId = 999999, rowVersion = await RowVersionAsync(first) }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reordering_without_a_gap_renumbers_the_siblings()
    {
        var parent = await CreateAsync(null, $"Renumber {Guid.NewGuid():N}");
        var x = await CreateAsync(parent, "X");
        var y = await CreateAsync(parent, "Y");
        var z = await CreateAsync(parent, "Z");
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.Folder SET SortOrder = CASE Id WHEN @x THEN 1 WHEN @y THEN 2 ELSE 3 END WHERE ParentFolderId = @p;", ("@x", x), ("@y", y), ("@p", parent));
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/folders/{z}/move",
            new { newParentFolderId = parent, position = 1, rowVersion = await RowVersionAsync(z) }, HttpStatusCode.OK);

        Assert.Equal([x, z, y], await ChildrenAsync(parent));
    }

    [Fact]
    public async Task Folders_with_sub_folders_or_any_documents_cannot_be_deleted()
    {
        var parent = await CreateAsync(null, $"Delete {Guid.NewGuid():N}");
        var sub = await CreateAsync(parent, "Sub");
        Assert.Equal("in-use", (await DeleteAsync(parent, HttpStatusCode.Conflict)).GetProperty("type").GetString());

        int document;
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            document = await dbo.Data.DocumentAsync("only deleted", sub);
            await dbo.ExecuteAsync("UPDATE app.Document SET DeletedAt = SYSUTCDATETIME(), DeletedByUserId = 1 WHERE Id = @d;", ("@d", document));
        }

        Assert.Equal("in-use", (await DeleteAsync(sub, HttpStatusCode.Conflict)).GetProperty("type").GetString());

        // The admin moves the deleted document elsewhere (T07 will offer this through the API).
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.Document SET FolderId = 1 WHERE Id = @d;", ("@d", document));
        }

        await DeleteAsync(sub, HttpStatusCode.NoContent);
        await DeleteAsync(parent, HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/folders/{parent}", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/api/folders/{sub}", null, HttpStatusCode.BadRequest); // rowVersion missing
    }

    [Fact]
    public async Task Sibling_names_are_unique_case_insensitively_but_may_repeat_under_other_parents()
    {
        var p1 = await CreateAsync(null, $"Names 1 {Guid.NewGuid():N}");
        var p2 = await CreateAsync(null, $"Names 2 {Guid.NewGuid():N}");
        await CreateAsync(p1, "Reports");
        await CreateAsync(p2, "Reports");

        var duplicate = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { parentFolderId = p1, name = "REPORTS" }, HttpStatusCode.Conflict);
        Assert.Equal("duplicate-name", duplicate.GetProperty("type").GetString());
        var root = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name = "general" }, HttpStatusCode.Conflict);
        Assert.Equal("duplicate-name", root.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task Invalid_names_are_field_errors(string name)
    {
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name }, HttpStatusCode.BadRequest);

        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Too_long_names_and_unknown_parents_are_rejected()
    {
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name = new string('x', 201) }, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { parentFolderId = 999999, name = "x" }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Folder_changes_are_audited()
    {
        var id = await CreateAsync(null, $"Audited {Guid.NewGuid():N}");
        var log = await Db.QueryAsync(factory, "SELECT Operation, Source, UserId FROM audit.ChangeLog WHERE TableName = N'app.Folder' AND EntityId = @id;", ("@id", id));

        var row = Assert.Single(log);
        Assert.Equal("I", row["Operation"]);
        Assert.Equal("App", row["Source"]);
        Assert.Equal(TestUsers.Admin, row["UserId"]);
    }

    private async Task<int> CreateAsync(int? parentFolderId, string name) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { parentFolderId, name }, HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private async Task<string> RowVersionAsync(int id) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/folders/{id}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString()!;

    private async Task<List<int>> ChildrenAsync(int parent)
    {
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/folders/tree", null, HttpStatusCode.OK);
        static IEnumerable<JsonElement> All(JsonElement nodes) => nodes.EnumerateArray().SelectMany(n => new[] { n }.Concat(All(n.GetProperty("children"))));
        return All(tree).Single(n => n.GetProperty("id").GetInt32() == parent).GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToList();
    }

    private async Task<JsonElement> DeleteAsync(int id, HttpStatusCode expected) =>
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/api/folders/{id}?rowVersion={Uri.EscapeDataString(await RowVersionAsync(id))}", null, expected);
}
