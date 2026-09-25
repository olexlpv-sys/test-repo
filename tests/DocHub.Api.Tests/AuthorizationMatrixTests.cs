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
}
