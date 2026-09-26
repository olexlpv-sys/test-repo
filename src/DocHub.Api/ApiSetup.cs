using System.Text.Json.Serialization;
using DocHub.Api.Audit;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Endpoints;
using DocHub.Api.Errors;
using DocHub.Api.OpenApi;
using DocHub.Infrastructure;
using DocHub.Infrastructure.Content;
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
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDefaults.Apply);
        // Registered after the default writer: used when the client's Accept header excludes JSON.
        services.AddSingleton<Microsoft.AspNetCore.Http.IProblemDetailsWriter, JsonProblemDetailsWriter>();
        services.AddExceptionHandler<ExceptionToProblemHandler>();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Numbers are numbers (the web defaults also accept "12"): a clean contract for the generated SPA client.
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            // Trees nest two levels per node (object + children) and may be 100 levels deep (TR_DocumentNode_Tree).
            options.SerializerOptions.MaxDepth = 256;
        });

        services.AddCors(options => options.AddPolicy(SpaCorsPolicy, policy => policy
            .WithOrigins(configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .AllowAnyHeader()
            .AllowAnyMethod()));

        services.AddHealthChecks().AddDbContextCheck<DocHubDbContext>("database");
        services.AddOpenApi(options => options.AddDocumentTransformer<TestModeSecuritySchemeTransformer>());

        services.AddSingleton<IEndpointModule, SystemEndpoints>();
        services.AddSingleton<IEndpointModule, MeEndpoints>();
        services.AddSingleton<IEndpointModule, AuditEndpoints>();
        services.AddSingleton<IEndpointModule, UserEndpoints>();
        services.AddSingleton<IEndpointModule, NodeTypeEndpoints>();
        services.AddSingleton<IEndpointModule, ContentStyleEndpoints>();
        services.AddSingleton<IEndpointModule, FolderEndpoints>();
        services.AddSingleton<IEndpointModule, DocumentEndpoints>();
        services.AddSingleton<IEndpointModule, VersionEndpoints>();
        services.AddSingleton<IEndpointModule, NodeEndpoints>();
        services.AddSingleton<IEndpointModule, ContentEndpoints>();
        services.AddSingleton<IEndpointModule, PermissionEndpoints>();
        services.AddSingleton<IEndpointModule, HistoryEndpoints>();
        services.AddSingleton<IEndpointModule, CommentEndpoints>();
        services.AddSingleton<IEndpointModule, CompareEndpoints>();

        // Documents and versions (T07): the authorization seam, guards, signing and read models.
        services.AddScoped<IDocumentAuthorization, DocumentAuthorization>();
        services.AddScoped<IVersionGuard, VersionGuard>();
        services.AddScoped<SigningService>();
        services.AddScoped<DocumentViews>();
        services.AddScoped<NodeRules>();
        services.AddScoped<NodeContents>();
        services.AddSingleton<DocHub.Infrastructure.Content.Diff.IContentDiffService, DocHub.Infrastructure.Content.Diff.ContentDiffService>();
        services.AddScoped<History.ChangeHistory>();
        services.AddScoped<History.ChangeTracking>();

        // Signed-version trees and contents (NFR-L9).
        // Values are serialized (also for the in-memory copy) with the API's depth limit: trees are up to 100 levels deep.
        services.AddHybridCache().AddSerializerFactory(new DeepJsonCacheSerializerFactory());

        // Style catalog schema (docs/content-format.md §2); the font list is configurable.
        services.AddSingleton(new StyleProperties(configuration.GetSection("Content:FontFamilies").Get<string[]>() is { Length: > 0 } fonts ? fonts : StyleProperties.DefaultFontFamilies));

        // Script-edited content: derived columns re-rendered in the background (T09 rule 8).
        services.Configure<DerivedRefreshOptions>(configuration.GetSection(DerivedRefreshOptions.SectionName));
        services.AddHostedService<DerivedContentRefresher>();

        // Tamper evidence (T21 §4): nightly ledger reconciliation.
        services.Configure<ReconciliationOptions>(configuration.GetSection(ReconciliationOptions.SectionName));
        services.AddHostedService<LedgerReconciliationService>();
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
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(TestModeAuthenticationHandler.AdminRole));
    }
}
