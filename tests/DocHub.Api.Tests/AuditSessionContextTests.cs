using System.Net.Http.Json;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>The API's writes carry the acting user and request id into the audit log (T04 §3, T03 session-context contract).</summary>
public sealed class AuditSessionContextTests(DocHubApiWithTestEndpointsFactory factory) : IClassFixture<DocHubApiWithTestEndpointsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Change_made_in_a_request_is_audited_as_App_with_user_and_correlation_id()
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);

        var traceId = await TouchAsync(client, folderId: 1);

        var row = Assert.Single(await ChangesAsync(folderId: 1, traceId));
        Assert.Equal("App", row["Source"]);
        Assert.Equal(TestUsers.Alice, row["UserId"]);
        Assert.Equal("SortOrder", row["ChangedColumns"]);
    }

    [Fact]
    public async Task Consecutive_requests_of_different_users_on_pooled_connections_are_attributed_correctly()
    {
        using var alice = factory.CreateClientFor(TestUsers.Alice);
        using var bob = factory.CreateClientFor(TestUsers.Bob);

        var first = await TouchAsync(alice, folderId: 2);
        var second = await TouchAsync(bob, folderId: 2);
        var third = await TouchAsync(alice, folderId: 2);

        Assert.Equal(TestUsers.Alice, Assert.Single(await ChangesAsync(2, first))["UserId"]);
        Assert.Equal(TestUsers.Bob, Assert.Single(await ChangesAsync(2, second))["UserId"]);
        Assert.Equal(TestUsers.Alice, Assert.Single(await ChangesAsync(2, third))["UserId"]);
    }

    private static async Task<string> TouchAsync(HttpClient client, int folderId)
    {
        using var response = await client.PostAsync(new Uri($"/__test/folders/{folderId}/touch", UriKind.Relative), null, Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("traceId").GetString()!;
    }

    private Task<IReadOnlyList<Dictionary<string, object?>>> ChangesAsync(int folderId, string traceId) =>
        Db.QueryAsync(factory,
            "SELECT Source, UserId, ChangedColumns FROM audit.ChangeLog WHERE TableName = N'app.Folder' AND EntityId = @f AND CorrelationId = @c;",
            ("@f", folderId), ("@c", traceId));
}
