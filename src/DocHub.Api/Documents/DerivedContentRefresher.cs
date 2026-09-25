using System.Text.Json;
using DocHub.Api.Auth;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Documents;

/// <summary><c>Content:DerivedRefresh</c> configuration (T09 rule 8).</summary>
public sealed class DerivedRefreshOptions
{
    public const string SectionName = "Content:DerivedRefresh";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; set; } = 500;
}

/// <summary>
/// Re-renders the derived columns (HTML, plain text, hash) and style usage of contents a support script changed
/// (<c>DerivedStale = 1</c>, set by the audit trigger), as the <c>system</c> user. It writes derived columns only — never
/// <c>ContentJson</c> — in one statement per row, so the trigger neither flags nor audits it; this is the only write
/// allowed to signed versions (T09 rule 8), and tamper detection is unaffected.
/// </summary>
internal sealed partial class DerivedContentRefresher(
    IServiceScopeFactory scopes,
    IOptions<DerivedRefreshOptions> options,
    TimeProvider time,
    ILogger<DerivedContentRefresher> logger) : BackgroundService
{
    public const string OperationContext = "RebuildDerived";

    /// <summary>Refreshes one batch; returns the number of rows refreshed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<HttpDbSessionContext>();
        session.UserIdOverride = User.SystemUserId;
        session.OperationContext = OperationContext;
        var db = scope.ServiceProvider.GetRequiredService<DocHubDbContext>();
        var schema = await scope.ServiceProvider.GetRequiredService<NodeContents>().SchemaAsync(cancellationToken).ConfigureAwait(false);

        var stale = await db.NodeContents.AsNoTracking()
            .Where(c => c.DerivedStale)
            .OrderBy(c => c.NodeId)
            .Take(options.Value.BatchSize)
            .Select(c => new { c.NodeId, c.ContentJson, c.RowVersion })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var refreshed = 0;
        foreach (var row in stale)
        {
            try
            {
                refreshed += await RefreshAsync(db, schema, row.NodeId, row.ContentJson, row.RowVersion, cancellationToken).ConfigureAwait(false) ? 1 : 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad row must not stop the others; it is retried next round.
                LogRowFailed(exception, row.NodeId);
            }
        }

        if (refreshed > 0)
        {
            LogRefreshed(refreshed);
        }

        return refreshed;
    }

    /// <summary>Re-renders one row in one statement, only if it is unchanged since it was read (newer edits are picked up next round).</summary>
    private static Task<bool> RefreshAsync(DocHubDbContext db, ContentDocument schema, int nodeId, string contentJson, byte[] rowVersion, CancellationToken cancellationToken)
    {
        var rendered = ContentHtmlRenderer.Render(contentJson);
        var used = UsedStyles(schema, contentJson);
        return db.InTransactionAsync(async () =>
        {
            var updated = await db.NodeContents
                .Where(c => c.NodeId == nodeId && c.RowVersion == rowVersion)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(c => c.ContentHtml, rendered.Html)
                    .SetProperty(c => c.PlainText, rendered.PlainText)
                    .SetProperty(c => c.ContentHash, rendered.ContentHash)
                    .SetProperty(c => c.DerivedStale, false), cancellationToken).ConfigureAwait(false);
            if (updated == 1)
            {
                await db.ContentStyleUsages.Where(u => u.NodeId == nodeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                db.ContentStyleUsages.AddRange(used.Select(s => new ContentStyleUsage { StyleId = s, NodeId = nodeId }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated == 1;
        }, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means more are waiting: continue at once.
                while (await RunOnceAsync(stoppingToken).ConfigureAwait(false) >= options.Value.BatchSize)
                {
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception);
            }

            await Task.Delay(options.Value.Interval, time, stoppingToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlySet<string> UsedStyles(ContentDocument schema, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(ContentSchema.RepairLoneSurrogates(json), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
            return schema.UsedStyles(document.RootElement);
        }
        catch (JsonException)
        {
            return new HashSet<string>();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed the derived content of {Count} script-edited nodes.")]
    private partial void LogRefreshed(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing the derived content of node {NodeId} failed; it is retried at the next interval.")]
    private partial void LogRowFailed(Exception exception, int nodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing derived content failed; it is retried at the next interval.")]
    private partial void LogFailed(Exception exception);
}
