using System.Reflection;
using DocHub.Api.Auth;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Endpoints;

internal sealed class SystemEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/info", async (IOptions<AuthOptions> auth, IHostEnvironment environment, DocHubDbContext db, CancellationToken ct) =>
            {
                // Test mode only: the user the SPA acts as before one is chosen (the first active user; every other call needs one).
                int? defaultUserId = auth.Value.Mode == AuthModes.Test
                    ? await db.Users.AsNoTracking().Where(u => u.IsActive && u.Id != User.SystemUserId).OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct)
                    : null;
                return TypedResults.Ok(new SystemInfo(auth.Value.Mode, environment.EnvironmentName, Version, defaultUserId));
            })
            .AllowAnonymous()
            .WithTags("System")
            .WithName("GetSystemInfo")
            .WithSummary("Auth mode and environment; the SPA shows the test-mode user switcher when authMode is Test (defaultUserId: who acts until a user is chosen).");
    }

    private static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public sealed record SystemInfo(string AuthMode, string Environment, string Version, int? DefaultUserId);
}
