using System.Globalization;
using System.Security.Claims;

namespace DocHub.Api.Auth;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public int UserId => IsAuthenticated && int.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id
        : throw new InvalidOperationException("The request is not authenticated.");

    public bool IsAdmin => IsAuthenticated && Principal!.IsInRole(TestModeAuthenticationHandler.AdminRole);
}
