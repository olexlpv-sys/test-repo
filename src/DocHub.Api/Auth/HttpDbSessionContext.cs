using DocHub.Infrastructure.Persistence;

namespace DocHub.Api.Auth;

/// <summary>
/// Supplies the audit session context (T03 §3) from the current request: acting user and HTTP trace id. Background jobs set
/// <see cref="UserIdOverride"/> in their own scope.
/// </summary>
internal sealed class HttpDbSessionContext(IHttpContextAccessor accessor, ICurrentUser currentUser) : IDbSessionContext
{
    public int? UserId => UserIdOverride ?? (currentUser.IsAuthenticated ? currentUser.UserId : null);

    /// <summary>The acting user outside a request (background jobs run as the system user).</summary>
    public int? UserIdOverride { get; set; }

    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    public string? OperationContext { get; set; }
}
