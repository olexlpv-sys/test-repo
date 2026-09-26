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
    public const string OwnerCannotHaveRole = "owner-cannot-have-role";
    public const string Conflict = "conflict";
    public const string InternalError = "internal-error";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string PayloadTooLarge = "payload-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string RequestFailed = "request-failed";
    public const string InUse = "in-use";
    public const string InvalidMove = "invalid-move";
    public const string DocumentDeleted = "document-deleted";
    public const string VersionNotEditable = "version-not-editable";
    public const string DraftAlreadyExists = "draft-already-exists";
    public const string NoSignedVersion = "no-signed-version";
    public const string OnlyVersion = "only-version";
    public const string NoApprovers = "no-approvers";
    public const string NotDeleted = "not-deleted";
    public const string FolderMissing = "folder-missing";
    public const string BuiltInStyle = "built-in-style";
    public const string ExportNotReady = "export-not-ready";
}
