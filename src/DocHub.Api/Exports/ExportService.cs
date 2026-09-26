using DocHub.Api.Auth;
using DocHub.Api.Documents;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Export;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Exports;

/// <summary>Configuration section <c>Export:Worker</c>.</summary>
public sealed class ExportWorkerOptions
{
    public const string Section = "Export:Worker";

    /// <summary>Runs the worker in this instance (the API endpoints work either way; jobs then wait for another instance).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Renders at the same time in this instance.</summary>
    public int MaxConcurrentRenders { get; set; } = 2;

    /// <summary>A job running longer than this fails (a render is cancelled; a job left Running by a stopped instance is failed).</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often an idle worker looks for queued jobs (new jobs of this instance wake it at once).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long files of drafts are kept.</summary>
    public TimeSpan DraftRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often expired files are deleted.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Wakes this instance's worker when a job is queued.</summary>
public sealed class ExportQueueSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

public sealed record ExportJobStatus(int JobId, ExportStatus Status, int Progress, string? Error, string? FileName, long? FileSize);

/// <summary>The export API (T20): request (with pending-job reuse and cache hits), status and download.</summary>
public sealed class ExportService(
    DocHubDbContext db, ExportSource source, IExportStorage storage, IDocumentAuthorization authorization, ICurrentUser user, ExportQueueSignal signal,
    TimeProvider clock)
{
    public async Task<ExportJobStatus> RequestAsync(int versionId, Guid? logicalNodeId, PdfExportOptions options, CancellationToken cancellationToken)
    {
        var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => (int?)v.DocumentId).SingleOrDefaultAsync(cancellationToken)
            ?? throw DomainException.NotFound("Version", versionId);
        await authorization.EnsureCanViewAsync(documentId, cancellationToken);
        var key = await source.KeyAsync(versionId, logicalNodeId, options, cancellationToken);

        // The caller's own pending job for the same file.
        var pending = await db.ExportJobs.AsNoTracking()
            .Where(j => j.RequestedByUserId == user.UserId && j.CacheKey == key.CacheKey && (j.Status == ExportStatus.Queued || j.Status == ExportStatus.Running))
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(cancellationToken);
        if (pending is not null)
        {
            return Status(pending);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var job = new ExportJob
        {
            DocumentId = key.DocumentId,
            DocumentVersionId = versionId,
            LogicalNodeId = logicalNodeId,
            OptionsJson = ExportSource.Serialize(options),
            RequestedByUserId = user.UserId,
            RequestedAt = now,
            CacheKey = key.CacheKey,
            FileName = key.FileName,
            Status = ExportStatus.Queued,
        };

        // A file rendered for the same key (by anyone) that is still stored: this job is done at once.
        var cached = await db.ExportJobs.AsNoTracking()
            .Where(j => j.CacheKey == key.CacheKey && j.Status == ExportStatus.Succeeded && j.BlobPath != null)
            .OrderByDescending(j => j.Id).Select(j => new { j.BlobPath, j.FileSize }).FirstOrDefaultAsync(cancellationToken);
        if (cached is not null && await storage.ExistsAsync(cached.BlobPath!, cancellationToken))
        {
            job.Status = ExportStatus.Succeeded;
            job.Progress = 100;
            job.StartedAt = job.FinishedAt = now;
            job.BlobPath = cached.BlobPath;
            job.FileSize = cached.FileSize;
        }

        db.ExportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        if (job.Status == ExportStatus.Queued)
        {
            signal.Notify();
        }

        return Status(job);
    }

    public async Task<ExportJobStatus> StatusAsync(int jobId, CancellationToken cancellationToken) => Status(await VisibleJobAsync(jobId, cancellationToken));

    /// <summary>The file of a succeeded job; <c>409</c> while it is not ready, <c>404</c> once a draft's file has expired.</summary>
    public async Task<(Stream Content, string FileName)> FileAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await VisibleJobAsync(jobId, cancellationToken);
        if (job.Status != ExportStatus.Succeeded || job.BlobPath is null)
        {
            throw DomainException.Conflict(ErrorCodes.ExportNotReady, job.Status == ExportStatus.Failed ? "The export failed." : "The export is not finished yet.");
        }

        var content = await storage.OpenReadAsync(job.BlobPath, cancellationToken)
            ?? throw DomainException.NotFound("Export file (expired; export again)", jobId);
        return (content, job.FileName ?? "export.pdf");
    }

    /// <summary>A job of the caller (or any job for an admin) whose document the caller can still view; otherwise <c>404</c>.</summary>
    private async Task<ExportJob> VisibleJobAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await db.ExportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || (job.RequestedByUserId != user.UserId && !user.IsAdmin))
        {
            throw DomainException.NotFound("Export job", jobId);
        }

        await authorization.EnsureCanViewAsync(job.DocumentId, cancellationToken);
        return job;
    }

    private static ExportJobStatus Status(ExportJob job) =>
        new(job.Id, job.Status, job.Progress, job.Error, job.FileName, job.Status == ExportStatus.Succeeded ? job.FileSize : null);
}
