using System.Net;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>Authorization matrix (testing strategy): endpoints × users. Feature tasks extend it with their endpoints.</summary>
public sealed class AuthorizationMatrixTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private static readonly int?[] AllCallers = [null, TestUsers.System, TestUsers.Admin, TestUsers.Alice, TestUsers.Bob, TestUsers.Carol, TestUsers.Dave, TestUsers.Erin];

    [Fact]
    public async Task Foundation_endpoints_follow_the_matrix()
    {
        var cases = AllCallers.SelectMany(user => new[]
        {
            new AccessCase("GET", "/health", user, HttpStatusCode.OK),
            new AccessCase("GET", "/api/system/info", user, HttpStatusCode.OK),
            new AccessCase("GET", "/api/me", user, user is null or TestUsers.System ? HttpStatusCode.Unauthorized : HttpStatusCode.OK),
            new AccessCase("GET", "/api/does-not-exist", user, user is null or TestUsers.System ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        });

        await AuthorizationMatrix.AssertAsync(factory, cases, TestContext.Current.CancellationToken);
    }

    /// <summary>T05–T11: dictionaries, folders, documents, trees, contents, permissions, history and comments are readable by every user; writes are admin-only (non-existing ids/invalid bodies: no side effects).</summary>
    [Fact]
    public async Task Dictionary_endpoints_follow_the_matrix()
    {
        var updateType = new { code = "NOPE", name = "Nope", sortOrder = 0, isActive = true, rowVersion = "AAAAAAAAAAA=" };
        var cases = AllCallers.SelectMany(user =>
        {
            var authenticated = user is not (null or TestUsers.System);
            var read = authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
            var Missing = authenticated ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized;
            HttpStatusCode Admin(HttpStatusCode forAdmin) => !authenticated ? HttpStatusCode.Unauthorized : user == TestUsers.Admin ? forAdmin : HttpStatusCode.Forbidden;
            return new[]
            {
                new AccessCase("GET", "/api/users", user, read),
                new AccessCase("GET", "/api/users/2", user, read),
                new AccessCase("GET", "/api/node-types", user, read),
                new AccessCase("GET", "/api/node-types/1", user, read),
                new AccessCase("GET", "/api/content-styles", user, read),
                new AccessCase("GET", "/api/content-styles/1", user, read),
                new AccessCase("GET", "/api/content-styles/stylesheet.css", user, HttpStatusCode.OK),
                new AccessCase("POST", "/api/node-types", user, Admin(HttpStatusCode.BadRequest), new { code = "x" }),
                new AccessCase("PUT", "/api/node-types/999999", user, Admin(HttpStatusCode.NotFound), updateType),
                new AccessCase("DELETE", "/api/node-types/999999", user, Admin(HttpStatusCode.NotFound)),
                new AccessCase("POST", "/api/content-styles", user, Admin(HttpStatusCode.BadRequest), new { styleId = "x" }),
                new AccessCase("PUT", "/api/content-styles/999999", user, Admin(HttpStatusCode.NotFound), new { name = "x", properties = new { }, rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("DELETE", "/api/content-styles/999999?rowVersion=AAAAAAAAAAA=", user, Admin(HttpStatusCode.NotFound)),
                // T06 folders.
                new AccessCase("GET", "/api/folders/tree", user, read),
                new AccessCase("GET", "/api/folders/1", user, read),
                new AccessCase("POST", "/api/folders", user, Admin(HttpStatusCode.BadRequest), new { name = "" }),
                new AccessCase("PUT", "/api/folders/999999", user, Admin(HttpStatusCode.NotFound), new { name = "x", rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("POST", "/api/folders/999999/move", user, Admin(HttpStatusCode.NotFound), new { rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("DELETE", "/api/folders/999999?rowVersion=AAAAAAAAAAA=", user, Admin(HttpStatusCode.NotFound)),
                // T07 documents and versions (non-existing ids: authenticated users get 404, no side effects).
                new AccessCase("GET", "/api/folders/1/documents", user, read),
                new AccessCase("POST", "/api/documents", user, authenticated ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, new { folderId = 1, title = "" }),
                new AccessCase("GET", "/api/documents/999999", user, Missing),
                new AccessCase("PUT", "/api/documents/999999", user, Missing, new { title = "x", rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("POST", "/api/documents/999999/move", user, Missing, new { folderId = 1, rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("DELETE", "/api/documents/999999?rowVersion=AAAAAAAAAAA=", user, Missing),
                new AccessCase("POST", "/api/documents/999999/restore", user, Missing, new { }),
                new AccessCase("POST", "/api/documents/999999/drafts", user, Missing),
                new AccessCase("GET", "/api/versions/999999", user, Missing),
                new AccessCase("GET", "/api/versions/999999/signatures", user, Missing),
                new AccessCase("POST", "/api/versions/999999/signatures", user, Missing, new { }),
                new AccessCase("DELETE", "/api/versions/999999/signatures/mine", user, Missing),
                new AccessCase("DELETE", "/api/versions/999999?rowVersion=AAAAAAAAAAA=", user, Missing),
                // T08 tree.
                new AccessCase("GET", "/api/versions/999999/tree", user, Missing),
                new AccessCase("GET", "/api/nodes/999999", user, Missing),
                new AccessCase("POST", "/api/versions/999999/nodes", user, Missing, new { nodeTypeId = 1, title = "x" }),
                new AccessCase("PATCH", "/api/nodes/999999", user, Missing, new { title = "x", rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("POST", "/api/nodes/999999/move", user, Missing, new { rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("DELETE", "/api/nodes/999999?rowVersion=AAAAAAAAAAA=", user, Missing),

                // T09 content (per-document permissions: ContentEndpointsTests).
                new AccessCase("GET", "/api/content-schema", user, read),
                new AccessCase("GET", "/api/nodes/999999/content", user, Missing),
                new AccessCase("PUT", "/api/nodes/999999/content", user, Missing, new { contentJson = new { type = "doc", content = Array.Empty<object>() }, rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("GET", "/api/versions/999999/content?nodeIds=1", user, Missing),

                // T10 permissions (per-role rules: PermissionsTests).
                new AccessCase("GET", "/api/documents/999999/permissions", user, Missing),
                new AccessCase("GET", "/api/documents/999999/my-permissions", user, Missing),
                new AccessCase("POST", "/api/documents/999999/permissions", user, Missing, new { userId = 3, role = "Editor" }),
                new AccessCase("DELETE", "/api/documents/999999/permissions/1", user, Missing),

                // T11 history (per-document visibility: HistoryTests).
                new AccessCase("GET", $"/api/documents/999999/nodes/{Guid.Empty}/history", user, Missing),
                new AccessCase("GET", "/api/documents/999999/history", user, Missing),
                new AccessCase("GET", $"/api/documents/999999/nodes/{Guid.Empty}/changes", user, Missing),
                new AccessCase("GET", "/api/versions/999999/change-summary", user, Missing),
                new AccessCase("GET", "/api/history/entries/999999999/diff", user, Missing),
                new AccessCase("GET", "/api/history/entries/999999999/content", user, Missing),
                new AccessCase("GET", "/api/admin/audit", user, Admin(HttpStatusCode.BadRequest)),

                // T13 comments (per-role rules: CommentTests).
                new AccessCase("GET", "/api/versions/999999/comments", user, Missing),
                new AccessCase("GET", "/api/documents/999999/comments/counts", user, Missing),
                new AccessCase("POST", "/api/versions/999999/comments", user, Missing, new { body = "x" }),
                new AccessCase("PUT", "/api/comments/999999", user, Missing, new { body = "x", rowVersion = "AAAAAAAAAAA=" }),
                new AccessCase("DELETE", "/api/comments/999999?rowVersion=AAAAAAAAAAA=", user, Missing),
                new AccessCase("POST", "/api/comments/999999/resolve", user, Missing),
                new AccessCase("POST", "/api/comments/999999/reopen", user, Missing),
            };
        });

        await AuthorizationMatrix.AssertAsync(factory, cases, TestContext.Current.CancellationToken);
    }
}
