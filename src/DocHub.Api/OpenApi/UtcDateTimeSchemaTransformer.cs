using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DocHub.Api.OpenApi;

/// <summary>
/// Keeps <see cref="DateTime"/> a <c>string</c> / <c>date-time</c> in the contract: with the custom
/// <see cref="Common.UtcDateTimeConverter"/> the schema generator no longer knows the JSON type and would leave it out.
/// </summary>
internal sealed class UtcDateTimeSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(DateTime) || type == typeof(DateTime?))
        {
            // Nullable properties list null; parameters don't (as generated without the converter).
            schema.Type = type == typeof(DateTime?) && context.ParameterDescription is null ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
            schema.Format = "date-time";
        }

        return Task.CompletedTask;
    }
}
