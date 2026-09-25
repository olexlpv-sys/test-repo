using Microsoft.AspNetCore.Mvc;

namespace DocHub.Api.Errors;

/// <summary>
/// Fallback writer: the default ProblemDetails writer refuses requests whose <c>Accept</c> excludes JSON (e.g. a browser
/// sending <c>text/html</c>), which would turn every problem into an empty 500. The API always answers problems as
/// <c>application/problem+json</c> (NFR-3).
/// </summary>
internal sealed class JsonProblemDetailsWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProblemDefaults.Apply(context);
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(context.ProblemDetails, typeof(ProblemDetails), options: null, contentType: "application/problem+json"));
    }
}
