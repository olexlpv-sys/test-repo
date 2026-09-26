using System.Net;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;
using Microsoft.AspNetCore.Hosting;

namespace DocHub.Api.Tests;

public sealed class HostingTests(DocHubApiFactory factory, SqlServerContainerFixture server) : IClassFixture<DocHubApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_endpoint_reports_healthy_including_the_database()
    {
        using var client = factory.CreateClientFor(null);

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Telemetry_is_registered_only_when_configured()
    {
        Assert.Null(factory.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));

        await using var traced = new TelemetryApiFactory(server);
        await traced.InitializeAsync();
        using var client = traced.CreateClientFor(TestUsers.Alice);
        using var response = await client.GetAsync(new Uri("/api/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(traced.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
        Assert.NotNull(traced.Services.GetService(typeof(OpenTelemetry.Metrics.MeterProvider)));
    }

    private sealed class TelemetryApiFactory(SqlServerContainerFixture server) : DocHubApiFactory(server)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("OpenTelemetry:Enabled", "true");
            // No collector in tests: the exporter just fails to send, which it does in the background.
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9");
        }
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task Api_documentation_is_available_anonymously_in_development(string path)
    {
        using var client = factory.CreateClientFor(null);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task Api_documentation_is_not_exposed_in_production(string path)
    {
        await using var production = new DocHubApiFactory(server) { EnvironmentName = "Production" };
        await production.InitializeAsync();
        using var client = production
            .WithWebHostBuilder(b => b.UseSetting("Auth:AllowTestModeInProduction", "true"))
            .CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "2");

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Test_mode_in_production_without_explicit_opt_in_fails_at_startup()
    {
        await using var production = new DocHubApiFactory(server) { EnvironmentName = "Production" };
        await production.InitializeAsync();

        var exception = Assert.Throws<InvalidOperationException>(() => production.CreateClient());

        Assert.Contains("not allowed in Production", exception.Message, StringComparison.Ordinal);
    }
}
