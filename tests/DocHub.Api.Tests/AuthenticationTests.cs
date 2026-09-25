using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>Test-mode authentication (ADR-06, T04 §2).</summary>
public sealed class AuthenticationTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_returns_the_user_from_the_header()
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);

        var me = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/me", UriKind.Relative), Ct);

        Assert.Equal(TestUsers.Alice, me.GetProperty("id").GetInt32());
        Assert.Equal("alice", me.GetProperty("login").GetString());
        Assert.Equal("Alice Anderson", me.GetProperty("displayName").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Admin_flag_is_reported_for_admins()
    {
        using var client = factory.CreateClientFor(TestUsers.Admin);

        var me = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/me", UriKind.Relative), Ct);

        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("999999")]
    [InlineData("0")]      // the seeded system user is inactive
    [InlineData("abc")]
    [InlineData("-2")]
    [InlineData("2, 3")]
    public async Task Missing_unknown_inactive_or_malformed_user_gets_401_problem(string? header)
    {
        using var client = factory.CreateClient();
        if (header is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", header);
        }

        using var response = await client.GetAsync(new Uri("/api/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("unauthenticated", problem.GetProperty("type").GetString());
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task System_info_is_anonymous_and_reports_test_mode()
    {
        using var client = factory.CreateClientFor(null);

        var info = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/system/info", UriKind.Relative), Ct);

        Assert.Equal("Test", info.GetProperty("authMode").GetString());
        Assert.Equal("Development", info.GetProperty("environment").GetString());
        Assert.False(string.IsNullOrEmpty(info.GetProperty("version").GetString()));
    }
}
