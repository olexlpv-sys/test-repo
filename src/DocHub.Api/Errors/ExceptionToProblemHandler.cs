using System.Text.RegularExpressions;
using DocHub.Domain.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Errors;

/// <summary>
/// Maps exceptions to RFC 9457 ProblemDetails with the stable error codes of docs/architecture.md §3 as <c>type</c>.
/// </summary>
internal sealed partial class ExceptionToProblemHandler(IProblemDetailsService problemDetails, ILogger<ExceptionToProblemHandler> logger)
    : IExceptionHandler
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int ConstraintViolation = 547;
    private const int FolderCycle = 50040; // TR_Folder_NoCycle
    private const int LockTimeoutFolders = 50041;
    private const int LockTimeoutDocument = 50042;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, type, title, detail) = Map(exception);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Type = type, Title = title, Detail = detail },
        }).ConfigureAwait(false);
    }

    internal static (int Status, string Type, string Title, string? Detail) Map(Exception exception) => exception switch
    {
        DomainException e => (StatusFor(e.Kind), e.Code, TitleFor(e.Kind), e.Message),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.ConcurrencyConflict, "Concurrency conflict",
            "The item was changed by someone else. Reload it and try again."),
        DbUpdateException { InnerException: SqlException sql } when sql.Number is UniqueIndexViolation or UniqueConstraintViolation =>
            (StatusCodes.Status409Conflict, UniqueConstraintCodes.For(IndexName(sql.Message)), "Duplicate", "An item with the same key already exists."),
        DbUpdateException { InnerException: SqlException { Number: ConstraintViolation } fk } when InUseReference().IsMatch(fk.Message) =>
            (StatusCodes.Status409Conflict, ErrorCodes.InUse, "In use", "The item is referenced by other data; deactivate it instead."),
        DbUpdateException { InnerException: SqlException { Number: ConstraintViolation } fk } when MissingParent().IsMatch(fk.Message) =>
            (StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "The referenced folder no longer exists."),
        DbUpdateException { InnerException: SqlException { Number: ConstraintViolation } } =>
            (StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Conflict", "The change conflicts with related data."),
        DbUpdateException { InnerException: SqlException { Number: FolderCycle } } =>
            (StatusCodes.Status409Conflict, ErrorCodes.InvalidMove, "Invalid move", "A folder can't be moved into itself or into one of its sub-folders."),
        SqlException { Number: LockTimeoutFolders or LockTimeoutDocument } or DbUpdateException { InnerException: SqlException { Number: LockTimeoutFolders or LockTimeoutDocument } } =>
            (StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Busy", "The item is being changed by another request; try again."),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            (StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge, "Payload too large", "The request body is too large."),
        BadHttpRequestException { StatusCode: StatusCodes.Status415UnsupportedMediaType } =>
            (StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaType, "Unsupported media type", "Send the request body as application/json."),
        BadHttpRequestException bad => (bad.StatusCode, ErrorCodes.ValidationFailed, "Invalid request", "The request could not be read."),
        _ => (StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "Internal error", null),
    };

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status409Conflict,
    };

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Validation failed",
        ErrorKind.NotFound => "Not found",
        ErrorKind.Forbidden => "Forbidden",
        _ => "Conflict",
    };

    private static string? IndexName(string message) => IndexNamePattern().Match(message) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"'((?:UX|UQ|PK)_[A-Za-z0-9_]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex IndexNamePattern();

    // A DELETE that lost a race with a new reference (node → node type, content → style, style → base style, folder → parent, document → folder).
    [GeneratedRegex(@"DELETE statement conflicted with the (?:SAME TABLE )?REFERENCE constraint ""(FK_DocumentNode_NodeType|FK_ContentStyleUsage_Style|FK_ContentStyle_BasedOn|FK_Folder_Parent|FK_Document_Folder)""", RegexOptions.CultureInvariant)]
    private static partial Regex InUseReference();

    // A create/move/document that lost a race with the deletion of the folder it points to.
    [GeneratedRegex(@"(INSERT|UPDATE) statement conflicted with the FOREIGN KEY (?:SAME TABLE )?constraint ""(FK_Folder_Parent|FK_Document_Folder)""", RegexOptions.CultureInvariant)]
    private static partial Regex MissingParent();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
