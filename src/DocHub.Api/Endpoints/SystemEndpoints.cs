using System.Reflection;
using DocHub.Api.Auth;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Endpoints;

internal sealed class SystemEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/info", (IOptions<AuthOptions> auth, IHostEnvironment environment) =>
                TypedResults.Ok(new SystemInfo(auth.Value.Mode, environment.EnvironmentName, Version)))
            .AllowAnonymous()
            .WithTags("System")
            .WithName("GetSystemInfo")
            .WithSummary("Auth mode and environment; the SPA shows the test-mode user switcher when authMode is Test.");
    }

    private static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public sealed record SystemInfo(string AuthMode, string Environment, string Version);
}
