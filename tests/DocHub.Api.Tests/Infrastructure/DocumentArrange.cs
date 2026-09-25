using System.Net;
using System.Text.Json;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>
/// Arranging documents for T07 tests: documents through the API; nodes, contents and approver grants directly in the
/// database until T08/T09/T10 add their endpoints.
/// </summary>
internal sealed class DocumentArrange(DocHubApiFactory factory)
{
    public async Task<(int DocumentId, int DraftId)> CreateAsync(int ownerId = TestUsers.Alice, int folderId = 1, string? title = null)
    {
        var created = await ApiClient.ExpectAsync(factory, ownerId, HttpMethod.Post, "/api/documents",
            new { folderId, title = title ?? $"Doc {Guid.NewGuid():N}" }, HttpStatusCode.Created);
        return (created.GetProperty("id").GetInt32(), created.GetProperty("draftVersionId").GetInt32());
    }

    /// <summary>A small tree: two root nodes, one with a child; every node with content.</summary>
    public async Task<IReadOnlyList<int>> AddNodesAsync(int versionId, string text = "hello")
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        var root1 = await dbo.Data.NodeAsync(versionId, title: "Chapter 1", sortOrder: 1024);
        var root2 = await dbo.Data.NodeAsync(versionId, title: "Chapter 2", sortOrder: 2048);
        var child = await dbo.Data.NodeAsync(versionId, root1, "Section 1.1", 1024);
        foreach (var node in new[] { root1, root2, child })
        {
            await dbo.Data.ContentAsync(node, $$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{text}} {{node}}"}]}]}""");
        }

        return [root1, root2, child];
    }

    public async Task GrantAsync(int documentId, int userId, byte role = 2)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync("INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, GrantedByUserId) VALUES (@d, @u, @r, 2);", ("@d", documentId), ("@u", userId), ("@r", role));
    }

    public async Task RevokeAsync(int documentId, int userId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync("DELETE FROM app.DocumentPermission WHERE DocumentId = @d AND UserId = @u;", ("@d", documentId), ("@u", userId));
    }

    /// <summary>A content change by a support script (audited, advances the version stamp).</summary>
    public async Task EditContentBySqlAsync(int nodeId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.edited', CONVERT(NVARCHAR (36), NEWID())) WHERE NodeId = @n;", ("@n", nodeId));
    }

    public Task<JsonElement> SignAsync(int versionId, int userId, HttpStatusCode expected = HttpStatusCode.OK) =>
        ApiClient.ExpectAsync(factory, userId, HttpMethod.Post, $"/api/versions/{versionId}/signatures", new { comment = "ok" }, expected);

    public Task<JsonElement> DocumentAsync(int documentId, int userId = TestUsers.Alice, HttpStatusCode expected = HttpStatusCode.OK) =>
        ApiClient.ExpectAsync(factory, userId, HttpMethod.Get, $"/api/documents/{documentId}", null, expected);

    public async Task<string> RowVersionAsync(int documentId) =>
        (await DocumentAsync(documentId, TestUsers.Admin)).GetProperty("rowVersion").GetString()!;

    public async Task<JsonElement> VersionAsync(int versionId, int userId = TestUsers.Alice) =>
        await ApiClient.ExpectAsync(factory, userId, HttpMethod.Get, $"/api/versions/{versionId}", null, HttpStatusCode.OK);

    /// <summary>A signed v1 with approvers carol and dave.</summary>
    public async Task<(int DocumentId, int VersionId, IReadOnlyList<int> Nodes)> SignedAsync(int ownerId = TestUsers.Alice)
    {
        var (documentId, draftId) = await CreateAsync(ownerId);
        var nodes = await AddNodesAsync(draftId);
        await GrantAsync(documentId, TestUsers.Carol);
        await GrantAsync(documentId, TestUsers.Dave);
        await SignAsync(draftId, TestUsers.Carol);
        await SignAsync(draftId, TestUsers.Dave);
        return (documentId, draftId, nodes);
    }
}
