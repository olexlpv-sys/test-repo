using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>T10: document roles through the API — the role matrix for every role, grants and effective permissions.</summary>
public sealed class PermissionsTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private const string Paragraph = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"edited"}]}]}""";

    // Roles on the arranged document: alice owner, erin document editor, bob editor of "Chapter 2", carol approver.
    private const int Owner = TestUsers.Alice;
    private const int DocEditor = TestUsers.Erin;
    private const int NodeEditor = TestUsers.Bob;
    private const int Approver = TestUsers.Carol;
    private const int Other = TestUsers.Dave;
    private const int Admin = TestUsers.Admin;

    private readonly DocumentArrange _arrange = new(factory);

    public static TheoryData<int> Roles() => new(Owner, DocEditor, NodeEditor, Approver, Other, Admin);

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Every_row_of_the_role_matrix_holds_for_every_role(int user)
    {
        var doc = await ArrangeAsync();
        HttpStatusCode Allowed(HttpStatusCode ok, params int[] who) => who.Contains(user) ? ok : HttpStatusCode.Forbidden;

        // View document, versions, tree, content: everyone.
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/versions/{doc.Draft}/tree", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/nodes/{doc.Section}/content", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}/permissions", null, HttpStatusCode.OK);

        // Structure: owner only (also inside the node editor's chapter).
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/versions/{doc.Draft}/nodes", new { parentNodeId = doc.Chapter2, nodeTypeId = 1, title = "New" }, Allowed(HttpStatusCode.Created, Owner));
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Patch, $"/api/nodes/{doc.Section}", new { title = "Renamed", rowVersion = await NodeRowVersionAsync(doc.Section) }, Allowed(HttpStatusCode.OK, Owner));

        // Text of any node: owner, document editor. Text of the chapter and its descendants: also its node editor.
        await SaveAsync(user, doc.Chapter1, Allowed(HttpStatusCode.OK, Owner, DocEditor));
        await SaveAsync(user, doc.Chapter2, Allowed(HttpStatusCode.OK, Owner, DocEditor, NodeEditor));
        await SaveAsync(user, doc.Subsection, Allowed(HttpStatusCode.OK, Owner, DocEditor, NodeEditor));

        // Comment / resolve (T13 endpoints use these rights): effective permissions.
        var mine = await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}/my-permissions", null, HttpStatusCode.OK);
        Assert.Equal(user is Owner or DocEditor or NodeEditor or Approver, mine.GetProperty("canComment").GetBoolean());
        Assert.Equal(user is Owner or Approver, mine.GetProperty("canResolve").GetBoolean());
        Assert.Equal(user is Owner, mine.GetProperty("canEditStructure").GetBoolean());
        Assert.Equal(user is Owner or DocEditor, mine.GetProperty("canEditAllContent").GetBoolean());
        Assert.Equal(user is Approver, mine.GetProperty("canSign").GetBoolean());
        Assert.Equal(user is Owner, mine.GetProperty("canManage").GetBoolean());
        Assert.Equal(user is Owner or Admin, mine.GetProperty("canMove").GetBoolean());
        var editable = mine.GetProperty("editableLogicalNodeIds").EnumerateArray().Select(e => e.GetGuid()).ToHashSet();
        Assert.Equal(user == NodeEditor ? new[] { doc.Chapter2Logical, doc.SectionLogical, doc.SubsectionLogical }.ToHashSet() : [], editable);
        var details = await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}", null, HttpStatusCode.OK);
        Assert.Equal(mine.GetRawText(), details.GetProperty("myRoles").GetRawText());

        // Grant/revoke roles, rename: owner only.
        var granted = await ApiClient.SendAsync(factory, user, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Other == user ? Admin : Other, role = "Editor" });
        Assert.Equal(Allowed(HttpStatusCode.Created, Owner), granted.Status);
        granted.Response.Dispose();
        var anyGrant = (await ApiClient.ExpectAsync(factory, Admin, HttpMethod.Get, $"/api/documents/{doc.Id}/permissions", null, HttpStatusCode.OK)).GetProperty("grants")[0].GetProperty("id").GetInt32();
        if (user != Owner)
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Delete, $"/api/documents/{doc.Id}/permissions/{anyGrant}", null, HttpStatusCode.Forbidden);
        }

        await ApiClient.ExpectAsync(factory, user, HttpMethod.Put, $"/api/documents/{doc.Id}", new { title = "Renamed doc", rowVersion = await _arrange.RowVersionAsync(doc.Id) }, Allowed(HttpStatusCode.OK, Owner));

        // Sign: approvers only (the owner never is one).
        var signed = await ApiClient.SendAsync(factory, user, HttpMethod.Post, $"/api/versions/{doc.Draft}/signatures", new { });
        Assert.Equal(Allowed(HttpStatusCode.OK, Approver), signed.Status);
        signed.Response.Dispose();

        // Move to another folder: owner or admin.
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/documents/{doc.Id}/move", new { folderId = 1, rowVersion = await _arrange.RowVersionAsync(doc.Id) }, Allowed(HttpStatusCode.OK, Owner, Admin));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Deleted_documents_are_visible_movable_and_restorable_per_the_matrix(int user)
    {
        var doc = await ArrangeAsync();
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Delete, $"/api/documents/{doc.Id}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(doc.Id))}", null, HttpStatusCode.NoContent);
        var sees = user is Owner or Admin;
        var hidden = sees ? HttpStatusCode.OK : HttpStatusCode.NotFound;
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}", null, hidden);
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}/permissions", null, hidden);
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{doc.Id}/my-permissions", null, hidden);

        // Grant/revoke on a deleted document: 409 for the owner (who sees it), 404 for users who don't.
        var grant = await ApiClient.SendAsync(factory, user, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Other, role = "Editor" });
        Assert.Equal(sees ? HttpStatusCode.Conflict : HttpStatusCode.NotFound, grant.Status);
        grant.Response.Dispose();

        var move = await ApiClient.SendAsync(factory, user, HttpMethod.Post, $"/api/documents/{doc.Id}/move", new { folderId = 1, rowVersion = await _arrange.RowVersionAsync(doc.Id) });
        Assert.Equal(user switch { Admin => HttpStatusCode.OK, Owner => HttpStatusCode.Conflict, _ => HttpStatusCode.NotFound }, move.Status);
        if (user == Owner)
        {
            Assert.Equal("document-deleted", move.Body.GetProperty("type").GetString());
        }

        move.Response.Dispose();
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/documents/{doc.Id}/restore", new { }, hidden);
    }

    [Fact]
    public async Task Grants_are_validated_and_audited()
    {
        var doc = await ArrangeAsync();
        var foreignLogical = Guid.NewGuid();
        foreach (var (body, key) in new (object Body, string Key)[]
        {
            (new { userId = Other, role = "Approver", logicalNodeId = doc.Chapter2Logical }, "logicalNodeId"),
            (new { userId = Other, role = "Editor", logicalNodeId = foreignLogical }, "logicalNodeId"),
            (new { userId = 999999, role = "Editor" }, "userId"),
            (new { userId = TestUsers.System, role = "Editor" }, "userId"),
            (new { userId = Other, role = "7" }, "role"),
        })
        {
            var problem = await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", body, HttpStatusCode.BadRequest);
            Assert.True(problem.TryGetProperty("errors", out var errors) ? errors.TryGetProperty(key, out _) : key == "role", problem.GetRawText());
        }

        var owner = await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Owner, role = "Approver" }, HttpStatusCode.BadRequest);
        Assert.Equal("owner-cannot-have-role", owner.GetProperty("type").GetString());
        var duplicate = await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Approver, role = "Approver" }, HttpStatusCode.Conflict);
        Assert.Equal("duplicate-grant", duplicate.GetProperty("type").GetString());

        var created = await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Other, role = "Editor", logicalNodeId = doc.SectionLogical }, HttpStatusCode.Created);
        Assert.Equal("Editor", created.GetProperty("role").GetString());
        Assert.Equal("Section 2.1", created.GetProperty("nodeTitle").GetString());
        Assert.True(created.GetProperty("nodeInCurrentVersion").GetBoolean());
        Assert.Equal(Owner, created.GetProperty("grantedBy").GetProperty("id").GetInt32());
        var grantId = created.GetProperty("id").GetInt32();
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Delete, $"/api/documents/{doc.Id}/permissions/{grantId}", null, HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Delete, $"/api/documents/{doc.Id}/permissions/{grantId}", null, HttpStatusCode.NotFound);

        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        var audit = await dbo.QueryAsync(
            "SELECT Operation, UserId FROM audit.ChangeLog WHERE TableName = N'app.DocumentPermission' AND EntityId = @g ORDER BY Id;", ("@g", grantId));
        Assert.Equal(["I", "D"], audit.Select(a => (string)a["Operation"]!));
        Assert.All(audit, a => Assert.Equal(Owner, (int)a["UserId"]!));
    }

    [Fact]
    public async Task Node_grants_follow_the_tree_of_the_draft_and_survive_new_drafts()
    {
        var doc = await ArrangeAsync();
        await ApiClient.ExpectAsync(factory, Approver, HttpMethod.Post, $"/api/versions/{doc.Draft}/signatures", new { }, HttpStatusCode.OK);
        var draft = (await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var nodes = await NodesByLogicalIdAsync(draft);

        // Same logical ids in the new draft: the grant still covers Chapter 2 and its descendants.
        await SaveAsync(NodeEditor, nodes[doc.SubsectionLogical], HttpStatusCode.OK);
        await SaveAsync(NodeEditor, nodes[doc.Chapter1Logical], HttpStatusCode.Forbidden);

        // Moving the section out of Chapter 2 in the draft ends the editor's rights on it there; moving Chapter 1 in grants them.
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/nodes/{nodes[doc.SectionLogical]}/move", new { newParentNodeId = nodes[doc.Chapter1Logical], rowVersion = await NodeRowVersionAsync(nodes[doc.SectionLogical]) }, HttpStatusCode.OK);
        await SaveAsync(NodeEditor, nodes[doc.SectionLogical], HttpStatusCode.Forbidden);
        await SaveAsync(NodeEditor, nodes[doc.SubsectionLogical], HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/nodes/{nodes[doc.Chapter1Logical]}/move", new { newParentNodeId = nodes[doc.Chapter2Logical], rowVersion = await NodeRowVersionAsync(nodes[doc.Chapter1Logical]) }, HttpStatusCode.OK);
        await SaveAsync(NodeEditor, nodes[doc.SectionLogical], HttpStatusCode.OK);
        var mine = await ApiClient.ExpectAsync(factory, NodeEditor, HttpMethod.Get, $"/api/documents/{doc.Id}/my-permissions", null, HttpStatusCode.OK);
        Assert.Contains(doc.Chapter1Logical, mine.GetProperty("editableLogicalNodeIds").EnumerateArray().Select(e => e.GetGuid()));

        // v1 is unaffected: its tree still has the section under Chapter 2 (checked by the procedure on v1's tree).
        var v1 = await EffectiveOnVersionAsync(doc.Id, NodeEditor, doc.Draft);
        Assert.Contains(doc.SectionLogical, v1);
        Assert.DoesNotContain(doc.Chapter1Logical, v1);

        // A node deleted in the draft keeps its grant, shown as not in the current version.
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Delete, $"/api/nodes/{nodes[doc.Chapter2Logical]}?rowVersion={Uri.EscapeDataString(await NodeRowVersionAsync(nodes[doc.Chapter2Logical]))}", null, HttpStatusCode.OK);
        var grants = (await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Get, $"/api/documents/{doc.Id}/permissions", null, HttpStatusCode.OK)).GetProperty("grants");
        var nodeGrant = grants.EnumerateArray().Single(g => g.GetProperty("user").GetProperty("id").GetInt32() == NodeEditor);
        Assert.False(nodeGrant.GetProperty("nodeInCurrentVersion").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nodeGrant.GetProperty("nodeTitle").ValueKind);
    }

    [Fact]
    public async Task Adding_an_approver_makes_the_draft_wait_and_revoking_one_finalizes_it()
    {
        var doc = await ArrangeAsync();
        var dave = await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Other, role = "Approver" }, HttpStatusCode.Created);
        await ApiClient.ExpectAsync(factory, Approver, HttpMethod.Post, $"/api/versions/{doc.Draft}/signatures", new { }, HttpStatusCode.OK);
        Assert.Equal("Draft", (await _arrange.VersionAsync(doc.Draft)).GetProperty("status").GetString());

        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Delete, $"/api/documents/{doc.Id}/permissions/{dave.GetProperty("id").GetInt32()}", null, HttpStatusCode.NoContent);
        Assert.Equal("Signed", (await _arrange.VersionAsync(doc.Draft)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_signature_racing_the_revocation_of_its_approver_is_never_recorded()
    {
        for (var round = 0; round < 10; round++)
        {
            var doc = await ArrangeAsync();
            await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{doc.Id}/permissions", new { userId = Other, role = "Approver" }, HttpStatusCode.Created);
            var carol = (await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Get, $"/api/documents/{doc.Id}/permissions", null, HttpStatusCode.OK))
                .GetProperty("grants").EnumerateArray().Single(g => g.GetProperty("user").GetProperty("id").GetInt32() == Approver).GetProperty("id").GetInt32();

            var results = await Task.WhenAll(
                ApiClient.SendAsync(factory, Approver, HttpMethod.Post, $"/api/versions/{doc.Draft}/signatures", new { }),
                ApiClient.SendAsync(factory, Owner, HttpMethod.Delete, $"/api/documents/{doc.Id}/permissions/{carol}", null));
            Assert.Equal(HttpStatusCode.NoContent, results[1].Status);
            Assert.Contains(results[0].Status, new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden });
            await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
            var signatures = await dbo.ScalarAsync<int>("SELECT COUNT(*) FROM app.VersionSignature WHERE DocumentVersionId = @v AND UserId = @u AND WithdrawnAt IS NULL;", ("@v", doc.Draft), ("@u", Approver));
            Assert.Equal(results[0].Status == HttpStatusCode.OK ? 1 : 0, signatures);
            foreach (var r in results)
            {
                r.Response.Dispose();
            }
        }
    }

    private sealed record ArrangedDocument(
        int Id, int Draft, int Chapter1, int Chapter2, int Section, int Subsection, Guid Chapter1Logical, Guid Chapter2Logical, Guid SectionLogical, Guid SubsectionLogical);

    /// <summary>Chapter 1; Chapter 2 / Section 2.1 / Subsection 2.1.1 — with the roles of the matrix.</summary>
    private async Task<ArrangedDocument> ArrangeAsync()
    {
        var (documentId, draft) = await _arrange.CreateAsync(Owner);
        async Task<int> Add(int? parent, string title) =>
            (await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { parentNodeId = parent, nodeTypeId = 1, title }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var chapter1 = await Add(null, "Chapter 1");
        var chapter2 = await Add(null, "Chapter 2");
        var section = await Add(chapter2, "Section 2.1");
        var subsection = await Add(section, "Subsection 2.1.1");
        var logical = await NodesByIdAsync(draft);
        await _arrange.GrantAsync(documentId, Approver, role: 2);
        await _arrange.GrantAsync(documentId, DocEditor, role: 1);
        await ApiClient.ExpectAsync(factory, Owner, HttpMethod.Post, $"/api/documents/{documentId}/permissions", new { userId = NodeEditor, role = "Editor", logicalNodeId = logical[chapter2] }, HttpStatusCode.Created);
        return new ArrangedDocument(documentId, draft, chapter1, chapter2, section, subsection, logical[chapter1], logical[chapter2], logical[section], logical[subsection]);
    }

    private async Task SaveAsync(int user, int nodeId, HttpStatusCode expected)
    {
        var rowVersion = (await ApiClient.ExpectAsync(factory, Admin, HttpMethod.Get, $"/api/nodes/{nodeId}/content", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        var content = JsonNode.Parse(Paragraph.Replace("edited", $"edited by {user} {Guid.NewGuid():N}", StringComparison.Ordinal));
        await ApiClient.ExpectAsync(factory, user, HttpMethod.Put, $"/api/nodes/{nodeId}/content", new { contentJson = content, rowVersion }, expected);
    }

    private async Task<string> NodeRowVersionAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, Admin, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString()!;

    private async Task<Dictionary<int, Guid>> NodesByIdAsync(int versionId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        return (await dbo.QueryAsync("SELECT Id, LogicalNodeId FROM app.DocumentNode WHERE DocumentVersionId = @v;", ("@v", versionId)))
            .ToDictionary(n => (int)n["Id"]!, n => (Guid)n["LogicalNodeId"]!);
    }

    private async Task<Dictionary<Guid, int>> NodesByLogicalIdAsync(int versionId) =>
        (await NodesByIdAsync(versionId)).ToDictionary(n => n.Value, n => n.Key);

    private async Task<List<Guid>> EffectiveOnVersionAsync(int documentId, int userId, int versionId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("app.usp_GetEffectivePermissions", dbo.Connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@DocumentId", documentId);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@DocumentVersionId", versionId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.NextResultAsync(TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
