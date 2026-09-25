using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>CORS for the Vite dev server (T04 §4), configured via Cors:AllowedOrigins.</summary>
public sealed class CorsTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://evil.example", false)]
    public async Task Preflight_is_allowed_only_for_configured_origins(string origin, bool allowed)
    {
        using var client = factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/me", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-user-id");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Contains(origin));
    }
}
