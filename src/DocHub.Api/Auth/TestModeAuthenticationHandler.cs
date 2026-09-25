using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Auth;

/// <summary>
/// Test-mode authentication (ADR-06): the caller is the seeded, active user whose id is in the <c>X-User-Id</c> header.
/// Missing, malformed, unknown or inactive users get <c>401 unauthenticated</c>.
/// </summary>
internal sealed class TestModeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DocHubDbContext db,
    IMemoryCache cache,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestMode";
    public const string HeaderName = "X-User-Id";
    public const string AdminRole = "admin";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            return AuthenticateResult.Fail($"{HeaderName} must be a single user id.");
        }

        var user = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new CachedUser(u.Id, u.DisplayName, u.IsAdmin, u.IsActive))
                .SingleOrDefaultAsync(Context.RequestAborted)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            return AuthenticateResult.Fail("Unknown or inactive user.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.DisplayName),
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication required",
            $"Send the {HeaderName} header with the id of an active user.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Forbidden", "You are not allowed to perform this operation.");

    private async Task WriteProblemAsync(int status, string type, string title, string detail)
    {
        Response.StatusCode = status;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = status, Type = type, Title = title, Detail = detail },
        }).ConfigureAwait(false);
    }

    private static string CacheKey(int userId) => $"auth:user:{userId.ToString(CultureInfo.InvariantCulture)}";

    private sealed record CachedUser(int Id, string DisplayName, bool IsAdmin, bool IsActive);
}
