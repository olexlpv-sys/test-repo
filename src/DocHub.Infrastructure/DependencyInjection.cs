using DocHub.Infrastructure.Audit;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocHub.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the EF Core context (with the audit session-context interceptor), the stored-procedure layer and ledger reconciliation.</summary>
    public static IServiceCollection AddDocHubPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped<SessionContextConnectionInterceptor>();
        services.AddDbContext<DocHubDbContext>((provider, options) => options
            .UseAzureSql(connectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(provider.GetRequiredService<SessionContextConnectionInterceptor>()));
        services.AddScoped<IDbProcedures, DbProcedures>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditModuleHashes, GeneratedAuditModuleHashes>();
        services.AddScoped<ILedgerReconciliation, LedgerReconciliation>();
        return services;
    }
}
