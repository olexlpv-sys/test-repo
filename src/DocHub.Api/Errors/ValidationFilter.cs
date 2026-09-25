using System.ComponentModel.DataAnnotations;
using DocHub.Domain.Errors;

namespace DocHub.Api.Errors;

/// <summary>Validates the request argument of type <typeparamref name="T"/> with DataAnnotations → <c>400 validation-failed</c>.</summary>
internal sealed class ValidationFilter<T> : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return Problem(new Dictionary<string, string[]> { [""] = ["A request body is required."] });
        }

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true))
        {
            return await next(context).ConfigureAwait(false);
        }

        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(m => (Member: JsonName(m), r.ErrorMessage)))
            .GroupBy(e => e.Member)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage ?? "Invalid value.").ToArray());
        return Problem(errors);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem Problem(Dictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, title: "Validation failed", type: ErrorCodes.ValidationFailed);

    private static string JsonName(string member) =>
        string.IsNullOrEmpty(member) ? member : char.ToLowerInvariant(member[0]) + member[1..];
}

internal static class ValidationFilterExtensions
{
    /// <summary>Validates the endpoint's <typeparamref name="T"/> argument with DataAnnotations before the handler runs.</summary>
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
