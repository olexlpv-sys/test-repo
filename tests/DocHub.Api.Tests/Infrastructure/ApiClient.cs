using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>JSON requests as a test user, returning the status and the parsed body (if any).</summary>
internal static class ApiClient
{
    public static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> SendAsync(
        DocHubApiFactory factory, int? userId, HttpMethod method, string path, object? body = null)
    {
        using var client = factory.CreateClientFor(userId);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true
            ? JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 }).RootElement.Clone()
            : default;
        return (response.StatusCode, json, response);
    }

    /// <summary>Sends and asserts the status; returns the body.</summary>
    public static async Task<JsonElement> ExpectAsync(DocHubApiFactory factory, int? userId, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        var (status, json, response) = await SendAsync(factory, userId, method, path, body);
        using (response)
        {
            Assert.True(status == expected, $"{method} {path}: expected {(int)expected}, got {(int)status}: {json}");
        }

        return json;
    }
}
