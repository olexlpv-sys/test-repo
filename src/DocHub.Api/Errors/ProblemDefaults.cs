using DocHub.Domain.Errors;
using Microsoft.AspNetCore.WebUtilities;

namespace DocHub.Api.Errors;

/// <summary>
/// Makes every problem response carry a stable <c>type</c> (architecture §3), a title and the <c>traceId</c> — whichever
/// writer produces it (the default JSON writer or <see cref="JsonProblemDetailsWriter"/>).
/// </summary>
internal static class ProblemDefaults
{
    public static void Apply(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.ProblemDetails;
        problem.Status ??= context.HttpContext.Response.StatusCode;
        if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/", StringComparison.Ordinal))
        {
            problem.Type = TypeFor(problem.Status.Value);
        }

        problem.Title ??= ReasonPhrases.GetReasonPhrase(problem.Status.Value);
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    }

    private static string TypeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.ValidationFailed,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.MethodNotAllowed,
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        StatusCodes.Status413PayloadTooLarge => ErrorCodes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.UnsupportedMediaType,
        >= 500 => ErrorCodes.InternalError,
        _ => ErrorCodes.RequestFailed,
    };
}
