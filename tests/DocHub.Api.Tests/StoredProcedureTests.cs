using System.Net.Http.Json;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>The stored-procedure layer runs on the DbContext connection, so the audit session context applies (ADR-09).</summary>
public sealed class StoredProcedureTests(DocHubApiWithTestEndpointsFactory factory) : IClassFixture<DocHubApiWithTestEndpointsFactory>
{
    [Fact]
    public async Task Procedures_see_the_session_context_and_multiple_result_sets_are_read()
    {
        using var client = factory.CreateClientFor(TestUsers.Carol);

        var ping = await client.GetFromJsonAsync<JsonElement>(new Uri("/__test/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(TestUsers.Carol, ping.GetProperty("userId").GetInt32());
        Assert.False(string.IsNullOrEmpty(ping.GetProperty("correlationId").GetString()));
        Assert.Equal("App", ping.GetProperty("source").GetString());
        Assert.True(ping.GetProperty("serverTimeUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
    }
}
