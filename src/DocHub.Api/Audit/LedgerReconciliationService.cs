using DocHub.Api.Auth;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Audit;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Audit;

/// <summary>
/// Nightly ledger reconciliation (T21 §4) as the <c>system</c> user: the module-integrity check and
/// <c>audit.usp_ReconcileLedger</c> over the configured window. Failures are logged and retried the next night.
/// </summary>
internal sealed partial class LedgerReconciliationService(
    IServiceScopeFactory scopes,
    IOptions<ReconciliationOptions> options,
    TimeProvider time,
    ILogger<LedgerReconciliationService> logger) : BackgroundService
{
    public const string OperationContext = "Reconciliation";

    /// <summary>The next run strictly after <paramref name="now"/>.</summary>
    public static DateTime NextRun(DateTime now, TimeOnly runAtUtc)
    {
        var today = DateOnly.FromDateTime(now).ToDateTime(runAtUtc, DateTimeKind.Utc);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>One run, as the system user.</summary>
    public async Task<ReconciliationRun> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<HttpDbSessionContext>();
        session.UserIdOverride = User.SystemUserId;
        session.OperationContext = OperationContext;
        var from = time.GetUtcNow().UtcDateTime - options.Value.Window;
        var run = await scope.ServiceProvider.GetRequiredService<ILedgerReconciliation>().RunAsync(from, cancellationToken).ConfigureAwait(false);
        LogRun(run.FromUtc, run.ToUtc, run.NewFindings, run.ModuleProblems);
        return run;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            await Task.Delay(NextRun(now, options.Value.RunAtUtc) - now, time, stoppingToken).ConfigureAwait(false);
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ledger reconciliation {From:o}–{To:o}: {NewFindings} new findings, {ModuleProblems} module problems.")]
    private partial void LogRun(DateTime from, DateTime to, int newFindings, int moduleProblems);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ledger reconciliation failed; it runs again at the next scheduled time.")]
    private partial void LogFailed(Exception exception);
}
