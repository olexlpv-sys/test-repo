using DocHub.Api.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DocHub.Api.OpenApi;

/// <summary>Declares the <c>X-User-Id</c> header as the API's security scheme so Scalar can send it (ADR-06).</summary>
internal sealed class TestModeSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[TestModeAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = TestModeAuthenticationHandler.HeaderName,
            Description = "Test mode: id of a seeded, active user (e.g. 2 = alice).",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(TestModeAuthenticationHandler.SchemeName, document)] = [],
        });
        return Task.CompletedTask;
    }
}
