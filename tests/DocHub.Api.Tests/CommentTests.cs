using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>T13: comments on documents and nodes, replies, edit/delete, resolve/reopen, counts.</summary>
public sealed class CommentTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    /// <summary>Alice's draft with two nodes; carol approver, erin editor.</summary>
    private async Task<(int DocumentId, int Draft, Guid Node, Guid Other)> ArrangeAsync()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var nodes = await _arrange.AddNodesAsync(draft);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.GrantAsync(documentId, TestUsers.Erin, role: 1);
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/tree", null, HttpStatusCode.OK);
        return (documentId, draft, tree[0].GetProperty("logicalNodeId").GetGuid(), tree[1].GetProperty("logicalNodeId").GetGuid());
    }

    private Task<JsonElement> CommentAsync(int user, int version, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/versions/{version}/comments", body, expected);

    [Fact]
    public async Task Owner_editors_and_approvers_comment_others_get_403()
    {
        var (_, draft, node, _) = await ArrangeAsync();
        var onDocument = await CommentAsync(TestUsers.Carol, draft, new { body = "  Looks good overall.  " });
        Assert.Equal("Looks good overall.", onDocument.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, onDocument.GetProperty("logicalNodeId").ValueKind);
        var onNode = await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = node, body = "Fix this section." });
        Assert.Equal("Chapter 1", onNode.GetProperty("nodeTitle").GetString());
        await CommentAsync(TestUsers.Alice, draft, new { body = "Owner" });
        await CommentAsync(TestUsers.Erin, draft, new { logicalNodeId = node, body = "Editor" });
        foreach (var user in new[] { TestUsers.Bob, TestUsers.Admin, TestUsers.Dave })
        {
            await CommentAsync(user, draft, new { body = "No role" }, HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Replies_are_one_level_on_the_same_node_and_bodies_are_validated()
    {
        var (_, draft, node, other) = await ArrangeAsync();
        var top = await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = node, body = "Question?" });
        var reply = await CommentAsync(TestUsers.Alice, draft, new { logicalNodeId = node, parentCommentId = top.GetProperty("id").GetInt32(), body = "Answer." });

        await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = node, parentCommentId = reply.GetProperty("id").GetInt32(), body = "Nested" }, HttpStatusCode.BadRequest);
        await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = other, parentCommentId = top.GetProperty("id").GetInt32(), body = "Other node" }, HttpStatusCode.BadRequest);
        await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = Guid.NewGuid(), body = "Unknown node" }, HttpStatusCode.BadRequest);
        await CommentAsync(TestUsers.Carol, draft, new { body = "   " }, HttpStatusCode.BadRequest);
        await CommentAsync(TestUsers.Carol, draft, new { body = new string('x', 4001) }, HttpStatusCode.BadRequest);

        var threads = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments?logicalNodeId={node}", null, HttpStatusCode.OK);
        var thread = Assert.Single(threads.EnumerateArray());
        Assert.Equal(["Answer."], thread.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Only_the_author_edits_the_owner_deletes_any_and_owner_or_approver_resolve()
    {
        var (_, draft, node, _) = await ArrangeAsync();
        var comment = await CommentAsync(TestUsers.Erin, draft, new { logicalNodeId = node, body = "Typo here." });
        var id = comment.GetProperty("id").GetInt32();
        var rowVersion = comment.GetProperty("rowVersion").GetString();

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/comments/{id}", new { body = "Owner edit", rowVersion }, HttpStatusCode.Forbidden);
        var edited = await ApiClient.ExpectAsync(factory, TestUsers.Erin, HttpMethod.Put, $"/api/comments/{id}", new { body = "Typo in line 2.", rowVersion }, HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        await ApiClient.ExpectAsync(factory, TestUsers.Erin, HttpMethod.Put, $"/api/comments/{id}", new { body = "Stale", rowVersion }, HttpStatusCode.Conflict);

        foreach (var user in new[] { TestUsers.Erin, TestUsers.Bob })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/comments/{id}/resolve", null, HttpStatusCode.Forbidden);
        }

        var resolved = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/comments/{id}/resolve", null, HttpStatusCode.OK);
        Assert.Equal(TestUsers.Carol, resolved.GetProperty("resolvedBy").GetProperty("id").GetInt32());
        Assert.Empty((await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments?includeResolved=false", null, HttpStatusCode.OK)).EnumerateArray());
        var reopened = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/comments/{id}/reopen", null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedAt").ValueKind);

        var reply = await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = node, parentCommentId = id, body = "Agreed." });
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/comments/{reply.GetProperty("id").GetInt32()}/resolve", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Delete, $"/api/comments/{id}?rowVersion={Uri.EscapeDataString(reopened.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.Forbidden);

        // The owner deletes someone else's comment; with a reply it stays as "deleted".
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/comments/{id}?rowVersion={Uri.EscapeDataString(reopened.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.NoContent);
        var thread = Assert.Single((await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments", null, HttpStatusCode.OK)).EnumerateArray());
        Assert.True(thread.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, thread.GetProperty("body").ValueKind);
        Assert.Single(thread.GetProperty("replies").EnumerateArray());

        // The reply's author deletes it: nothing is left.
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Delete, $"/api/comments/{reply.GetProperty("id").GetInt32()}?rowVersion={Uri.EscapeDataString(reply.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.NoContent);
        Assert.Empty((await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments", null, HttpStatusCode.OK)).EnumerateArray());
        await ApiClient.ExpectAsync(factory, TestUsers.Erin, HttpMethod.Put, $"/api/comments/{id}", new { body = "Gone", rowVersion }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Signed_versions_take_comments_discarded_drafts_and_deleted_documents_do_not()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        await CommentAsync(TestUsers.Carol, v1, new { body = "On the signed version." });
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftComment = await CommentAsync(TestUsers.Carol, draft, new { body = "On the draft." });
        var draftRowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(draft)).GetProperty("rowVersion").GetString()!);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{draft}?rowVersion={draftRowVersion}", null, HttpStatusCode.NoContent);

        var discarded = await CommentAsync(TestUsers.Carol, draft, new { body = "Too late" }, HttpStatusCode.Conflict);
        Assert.Equal("version-not-editable", discarded.GetProperty("type").GetString());
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/comments/{draftComment.GetProperty("id").GetInt32()}/resolve", null, HttpStatusCode.Conflict);

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        var deleted = await CommentAsync(TestUsers.Alice, v1, new { body = "Deleted doc" }, HttpStatusCode.Conflict);
        Assert.Equal("document-deleted", deleted.GetProperty("type").GetString());
        foreach (var (user, expected) in new[] { (TestUsers.Carol, HttpStatusCode.NotFound), (TestUsers.Bob, HttpStatusCode.NotFound), (TestUsers.Alice, HttpStatusCode.OK), (TestUsers.Admin, HttpStatusCode.OK) })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/versions/{v1}/comments", null, expected);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{documentId}/comments/counts?versionId={v1}", null, expected);
        }
    }

    [Fact]
    public async Task Counts_match_the_list_and_previous_versions_are_opt_in()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        var v1Tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{v1}/tree", null, HttpStatusCode.OK);
        var node = v1Tree[0].GetProperty("logicalNodeId").GetGuid();
        await CommentAsync(TestUsers.Carol, v1, new { logicalNodeId = node, body = "v1 remark" });
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var top = await CommentAsync(TestUsers.Carol, draft, new { logicalNodeId = node, body = "v2 remark" });
        await CommentAsync(TestUsers.Alice, draft, new { logicalNodeId = node, parentCommentId = top.GetProperty("id").GetInt32(), body = "Reply" });
        await CommentAsync(TestUsers.Dave, draft, new { body = "Document level" });

        var list = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments", null, HttpStatusCode.OK);
        var counts = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/comments/counts", null, HttpStatusCode.OK);
        Assert.Equal(draft, counts.GetProperty("versionId").GetInt32());
        Assert.Equal(list.EnumerateArray().Count(t => t.GetProperty("logicalNodeId").ValueKind == JsonValueKind.Null), counts.GetProperty("document").GetInt32());
        var nodeThreads = list.EnumerateArray().Where(t => t.GetProperty("logicalNodeId").ValueKind != JsonValueKind.Null).ToList();
        Assert.Equal(nodeThreads.Sum(t => 1 + t.GetProperty("replies").GetArrayLength()), counts.GetProperty("nodes").GetProperty(node.ToString()).GetInt32());
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("body").GetString() == "v1 remark");

        var withPrevious = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments?includePreviousVersions=true&scope=node", null, HttpStatusCode.OK);
        var old = Assert.Single(withPrevious.EnumerateArray(), t => t.GetProperty("body").GetString() == "v1 remark");
        Assert.Equal("v1", old.GetProperty("versionLabel").GetString());
        Assert.DoesNotContain(withPrevious.EnumerateArray(), t => t.GetProperty("logicalNodeId").ValueKind == JsonValueKind.Null);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/comments?scope=bogus", null, HttpStatusCode.BadRequest);
    }
}
