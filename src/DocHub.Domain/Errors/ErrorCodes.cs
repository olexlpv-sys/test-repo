namespace DocHub.Domain.Errors;

/// <summary>Stable error codes (ProblemDetails <c>type</c>), docs/architecture.md §3.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation-failed";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string ConcurrencyConflict = "concurrency-conflict";
    public const string DuplicateName = "duplicate-name";
    public const string DuplicateGrant = "duplicate-grant";
    public const string Conflict = "conflict";
    public const string InternalError = "internal-error";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string PayloadTooLarge = "payload-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string RequestFailed = "request-failed";
}
