using System.Text.Json.Serialization;
using DocHub.Api.Auth;
using DocHub.Api.Endpoints;
using DocHub.Api.Errors;
using DocHub.Api.OpenApi;
using DocHub.Infrastructure;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;

namespace DocHub.Api;

/// <summary>Composition of the API: services and the request pipeline (kept out of Program.cs so tests can reason about it).</summary>
internal static class ApiSetup
{
    public const string SpaCorsPolicy = "Spa";

    public static WebApplicationBuilder AddDocHubApi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        var connectionString = configuration.GetConnectionString("DocHub");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'ConnectionStrings:DocHub' is not configured.");
        }

        services.AddDocHubPersistence(connectionString);
        services.AddHttpContextAccessor();
        services.AddMemoryCache();

        // Current user & audit session context.
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<HttpDbSessionContext>();
        services.AddScoped<IDbSessionContext>(sp => sp.GetRequiredService<HttpDbSessionContext>());
        AddAuthentication(builder);

        // Errors & JSON conventions.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            if (context.ProblemDetails.Status == StatusCodes.Status404NotFound && context.ProblemDetails.Type is null or "https://tools.ietf.org/html/rfc9110#section-15.5.5")
            {
                context.ProblemDetails.Type = Domain.Errors.ErrorCodes.NotFound;
            }
        });
        services.AddExceptionHandler<ExceptionToProblemHandler>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddCors(options => options.AddPolicy(SpaCorsPolicy, policy => policy
            .WithOrigins(configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .AllowAnyHeader()
            .AllowAnyMethod()));

        services.AddHealthChecks().AddDbContextCheck<DocHubDbContext>("database");
        services.AddOpenApi(options => options.AddDocumentTransformer<TestModeSecuritySchemeTransformer>());

        services.AddSingleton<IEndpointModule, SystemEndpoints>();
        services.AddSingleton<IEndpointModule, MeEndpoints>();
        return builder;
    }

    public static WebApplication UseDocHubApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors(SpaCorsPolicy);
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        app.MapHealthChecks("/health").AllowAnonymous();
        foreach (var module in app.Services.GetServices<IEndpointModule>())
        {
            module.Map(app);
        }

        return app;
    }

    private static void AddAuthentication(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AuthOptions.SectionName);
        var auth = section.Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.Configure<AuthOptions>(section);

        if (!string.Equals(auth.Mode, AuthModes.Test, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported Auth:Mode '{auth.Mode}'. Supported: '{AuthModes.Test}'.");
        }

        if (builder.Environment.IsProduction() && !auth.AllowTestModeInProduction)
        {
            throw new InvalidOperationException(
                "Test-mode authentication is not allowed in Production. Set Auth:AllowTestModeInProduction=true only for demo environments.");
        }

        builder.Services
            .AddAuthentication(TestModeAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestModeAuthenticationHandler>(TestModeAuthenticationHandler.SchemeName, null);

        // Everything requires an authenticated user unless an endpoint opts out with AllowAnonymous().
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }
}
