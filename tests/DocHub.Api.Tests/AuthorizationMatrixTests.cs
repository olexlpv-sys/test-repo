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

    /// <summary>T05/T06: dictionaries and folders are readable by every user; writes are admin-only (non-existing ids/invalid bodies: no side effects).</summary>
    [Fact]
    public async Task Dictionary_endpoints_follow_the_matrix()
    {
        var updateType = new { code = "NOPE", name = "Nope", sortOrder = 0, isActive = true, rowVersion = "AAAAAAAAAAA=" };
        var cases = AllCallers.SelectMany(user =>
        {
            var authenticated = user is not (null or TestUsers.System);
            var read = authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
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
            };
        });

        await AuthorizationMatrix.AssertAsync(factory, cases, TestContext.Current.CancellationToken);
    }
}
