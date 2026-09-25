using DocHub.Infrastructure.Persistence;

namespace DocHub.Api.Auth;

/// <summary>Supplies the audit session context (T03 §3) from the current request: acting user and HTTP trace id.</summary>
internal sealed class HttpDbSessionContext(IHttpContextAccessor accessor, ICurrentUser currentUser) : IDbSessionContext
{
    public int? UserId => currentUser.IsAuthenticated ? currentUser.UserId : null;

    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    public string? OperationContext { get; set; }
}
