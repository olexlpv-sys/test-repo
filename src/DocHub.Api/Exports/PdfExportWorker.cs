using DocHub.Api.Auth;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Export;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Exports;

/// <summary>
/// Renders queued PDF exports (T20 "Worker") as the <c>system</c> user: claims jobs with <c>READPAST, UPDLOCK</c> (so several
/// instances share the queue), runs at most <see cref="ExportWorkerOptions.MaxConcurrentRenders"/> at a time, advances the progress
/// per stage, stores the file and marks the job Succeeded or Failed. A job interrupted by shutdown goes back to the queue; jobs left
/// Running beyond the timeout (a crashed instance) are failed every minute, and expired draft files are deleted periodically.
/// </summary>
internal sealed partial class PdfExportWorker(
    IServiceScopeFactory scopes,
    IPdfRenderer renderer,
    IExportStorage storage,
    ExportQueueSignal signal,
    IOptions<ExportWorkerOptions> options,
    TimeProvider clock,
    ILogger<PdfExportWorker> logger) : BackgroundService
{
    public const string OperationContext = "PdfExport";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        using var slots = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentRenders));
        var running = new List<Task>();
        var nextCleanup = DateTime.MinValue;
        var nextSweep = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                int? jobId = null;
                try
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    if (now >= nextSweep)
                    {
                        nextSweep = now + TimeSpan.FromMinutes(1);
                        await FailStaleAsync(stoppingToken).ConfigureAwait(false);
                    }

                    if (now >= nextCleanup)
                    {
                        nextCleanup = now + settings.CleanupInterval;
                        await storage.DeleteExpiredAsync(stoppingToken).ConfigureAwait(false);
                    }

                    jobId = await ClaimAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogQueueFailed(exception);
                }

                if (jobId is not { } id)
                {
                    slots.Release();
                    await signal.WaitAsync(settings.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessAsync(id, stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Claims the oldest queued job (skipping rows other instances hold) and marks it Running.</summary>
    public async Task<int?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = SystemScope(out var db);
        // OUTPUT needs INTO: the table has the audit trigger.
        var claimed = await db.Database.SqlQuery<int>($"""
            DECLARE @claimed TABLE ([Id] INT NOT NULL);
            WITH [next] AS (
                SELECT TOP (1) * FROM [app].[ExportJob] WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE [Status] = 0
                ORDER BY [RequestedAt], [Id])
            UPDATE [next] SET [Status] = 1, [StartedAt] = SYSUTCDATETIME(), [Progress] = 5
            OUTPUT [inserted].[Id] INTO @claimed;
            SELECT [Id] AS [Value] FROM @claimed;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <summary>Renders one claimed job; failures mark the job Failed and never stop the worker.</summary>
    public async Task ProcessAsync(int jobId, CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(settings.JobTimeout);
        try
        {
            await using var scope = SystemScope(out var db);
            var job = await db.ExportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, timeout.Token).ConfigureAwait(false);
            var exportOptions = ExportSource.Deserialize(job.OptionsJson);
            var document = await scope.ServiceProvider.GetRequiredService<ExportSource>()
                .LoadAsync(job.DocumentVersionId, job.LogicalNodeId, timeout.Token).ConfigureAwait(false);
            await SetProgressAsync(db, jobId, 20, timeout.Token).ConfigureAwait(false);

            // Progress updates run one after another on the job's context while Chromium renders.
            var updates = Task.CompletedTask;
            var progress = new SyncProgress(p => updates = updates.ContinueWith(_ => SetProgressAsync(db, jobId, p, CancellationToken.None), TaskScheduler.Default).Unwrap());
            var result = await renderer.RenderAsync(document, exportOptions, progress, timeout.Token).ConfigureAwait(false);
            await updates.ConfigureAwait(false);

            var path = ExportPaths.Blob(job.DocumentId, job.DocumentVersionId, job.CacheKey);
            var expires = document.IsDraft ? clock.GetUtcNow().UtcDateTime + settings.DraftRetention : (DateTime?)null;
            await storage.UploadAsync(path, result.Pdf, expires, timeout.Token).ConfigureAwait(false);
            var finished = clock.GetUtcNow().UtcDateTime;
            await db.ExportJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, ExportStatus.Succeeded)
                .SetProperty(j => j.Progress, (byte)100)
                .SetProperty(j => j.BlobPath, path)
                .SetProperty(j => j.FileSize, result.Pdf.LongLength)
                .SetProperty(j => j.FinishedAt, finished), timeout.Token).ConfigureAwait(false);
            LogRendered(jobId, result.PageCount, result.Passes, result.BlockedRequests.Count);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The instance is stopping: back to the queue, so another instance (or this one after its restart) renders it.
            await RequeueAsync(jobId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var message = exception is OperationCanceledException or TimeoutException
                ? $"The export took longer than {settings.JobTimeout.TotalMinutes:0.#} minutes."
                : exception is Domain.Errors.DomainException { Kind: Domain.Errors.ErrorKind.NotFound } domain
                    ? domain.Message
                    : "The PDF could not be rendered.";
            LogFailed(exception, jobId);
            await FailAsync(jobId, message).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(int jobId, string message)
    {
        try
        {
            await using var scope = SystemScope(out var db);
            var now = clock.GetUtcNow().UtcDateTime;
            await db.ExportJobs.Where(j => j.Id == jobId && j.Status != ExportStatus.Succeeded).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, ExportStatus.Failed)
                .SetProperty(j => j.Error, message)
                .SetProperty(j => j.FinishedAt, now)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogQueueFailed(exception);
        }
    }

    private async Task RequeueAsync(int jobId)
    {
        try
        {
            await using var scope = SystemScope(out var db);
            await db.ExportJobs.Where(j => j.Id == jobId && j.Status == ExportStatus.Running).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, ExportStatus.Queued)
                .SetProperty(j => j.StartedAt, (DateTime?)null)
                .SetProperty(j => j.Progress, (byte)0)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Not requeued (e.g. the database is gone too): the stale-job sweep fails it after the timeout.
            LogQueueFailed(exception);
        }
    }

    /// <summary>
    /// Fails jobs left Running past the timeout (their instance stopped without requeueing them). Renders are cancelled at the
    /// timeout, so a job older than that plus a margin is never still rendering.
    /// </summary>
    public async Task<int> FailStaleAsync(CancellationToken cancellationToken)
    {
        await using var scope = SystemScope(out var db);
        var now = clock.GetUtcNow().UtcDateTime;
        var limit = now - options.Value.JobTimeout - TimeSpan.FromMinutes(1);
        return await db.ExportJobs.Where(j => j.Status == ExportStatus.Running && j.StartedAt < limit).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, ExportStatus.Failed)
            .SetProperty(j => j.Error, "The export was interrupted; export again.")
            .SetProperty(j => j.FinishedAt, now), cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> SetProgressAsync(DocHubDbContext db, int jobId, int progress, CancellationToken cancellationToken) =>
        db.ExportJobs.Where(j => j.Id == jobId && j.Status == ExportStatus.Running && j.Progress < progress)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Progress, (byte)Math.Clamp(progress, 0, 99)), cancellationToken);

    private AsyncServiceScope SystemScope(out DocHubDbContext db)
    {
        var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<HttpDbSessionContext>();
        session.UserIdOverride = User.SystemUserId;
        session.OperationContext = OperationContext;
        db = scope.ServiceProvider.GetRequiredService<DocHubDbContext>();
        return scope;
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PDF export {JobId} rendered: {Pages} pages, {Passes} passes, {Blocked} blocked requests.")]
    private partial void LogRendered(int jobId, int pages, int passes, int blocked);

    [LoggerMessage(Level = LogLevel.Error, Message = "PDF export {JobId} failed.")]
    private partial void LogFailed(Exception exception, int jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The PDF export queue could not be read or updated.")]
    private partial void LogQueueFailed(Exception exception);
}
