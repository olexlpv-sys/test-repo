namespace DocHub.Infrastructure.Persistence;

/// <summary>
/// Who is acting — written into SQL SESSION_CONTEXT on every opened connection so the audit triggers can record it
/// (T03 §3 session-context contract). Implemented by the API from the current request.
/// </summary>
public interface IDbSessionContext
{
    int? UserId { get; }

    string? CorrelationId { get; }

    /// <summary>Label for bulk operations (e.g. <c>CopyVersion</c>); recorded only, never changes what is audited.</summary>
    string? OperationContext { get; }
}
