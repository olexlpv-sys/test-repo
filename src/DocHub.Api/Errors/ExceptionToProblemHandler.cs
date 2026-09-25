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
        DbUpdateException { InnerException: SqlException { Number: ConstraintViolation } } =>
            (StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Conflict", "The change conflicts with related data."),
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
