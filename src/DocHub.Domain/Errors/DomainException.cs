namespace DocHub.Domain.Errors;

/// <summary>Kind of a domain error; the API maps it to an HTTP status (architecture §3).</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,
}

/// <summary>
/// A business rule violation with a stable error code (e.g. <c>version-not-editable</c>) that becomes the ProblemDetails
/// <c>type</c>. See docs/architecture.md §3 for the list of codes.
/// </summary>
public class DomainException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static DomainException NotFound(string entity, object id) =>
        new(ErrorKind.NotFound, ErrorCodes.NotFound, $"{entity} '{id}' was not found.");

    public static DomainException Forbidden(string message) => new(ErrorKind.Forbidden, ErrorCodes.Forbidden, message);

    public static DomainException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static DomainException Validation(string message) => new(ErrorKind.Validation, ErrorCodes.ValidationFailed, message);
}
