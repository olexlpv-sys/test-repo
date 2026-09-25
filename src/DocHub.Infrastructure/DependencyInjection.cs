using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocHub.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the EF Core context (with the audit session-context interceptor) and the stored-procedure layer.</summary>
    public static IServiceCollection AddDocHubPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped<SessionContextConnectionInterceptor>();
        services.AddDbContext<DocHubDbContext>((provider, options) => options
            .UseAzureSql(connectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(provider.GetRequiredService<SessionContextConnectionInterceptor>()));
        services.AddScoped<IDbProcedures, DbProcedures>();
        return services;
    }
}
