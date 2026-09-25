using System.Text.Json;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Errors;
using DocHub.Api.History;
using DocHub.Domain.Content;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content.Diff;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

/// <summary>
/// Change history (T11, FR-H1/H3/H4): <c>audit.ChangeLog</c> as human-readable entries per node and per document, content diffs
/// and historical content of entries, and the raw log for admins. Reads need view rights on the document (FR-P5).
/// </summary>
internal sealed class HistoryEndpoints : IEndpointModule
{
    public const int MaxAuditDays = 31;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents/{id:int}/nodes/{logicalNodeId:guid}/history", async Task<Ok<PagedResult<HistoryEntry>>> (
                int id, Guid logicalNodeId, [AsParameters] PageRequest paging, IDocumentAuthorization authorization, ChangeHistory history, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var entries = await history.EntriesAsync(await history.NodeRowsAsync(id, logicalNodeId, ct), id, ct);
                return TypedResults.Ok(Page(entries, paging));
            })
            .WithValidation<PageRequest>()
            .WithTags("History")
            .WithName("GetNodeHistory")
            .WithSummary("History of one node across all versions (with the document's signings as context), newest first.");

        endpoints.MapGet("/api/documents/{id:int}/history", async Task<Ok<PagedResult<HistoryEntry>>> (
                int id, int? versionId, DateTime? from, DateTime? to, int? userId, string? source, [AsParameters] PageRequest paging,
                IDocumentAuthorization authorization, ChangeHistory history, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                if (source is not (null or "App" or "Script"))
                {
                    throw DomainException.Validation("source must be App or Script.");
                }

                var rows = await history.DocumentRowsAsync(id, versionId, from?.ToUniversalTime(), to?.ToUniversalTime(), userId, source, ct);
                return TypedResults.Ok(Page(await history.EntriesAsync(rows, id, ct), paging));
            })
            .WithValidation<PageRequest>()
            .WithTags("History")
            .WithName("GetDocumentHistory")
            .WithSummary("Document-wide activity (nodes, content, versions, permissions, signatures, comments), newest first; filters are optional.");

        endpoints.MapGet("/api/history/entries/{entryId:long}/diff", async Task<Ok<ContentDiff>> (
                long entryId, IDocumentAuthorization authorization, ChangeHistory history, IContentDiffService diff, CancellationToken ct) =>
            {
                var row = await VisibleRowAsync(entryId, authorization, history, ct);
                if (row.TableName != "app.NodeContent" || (row.Operation == "U" && !row.Columns.Contains("ContentJson")))
                {
                    throw DomainException.NotFound("Content change", entryId);
                }

                return TypedResults.Ok(diff.Diff(row.Old("ContentJson") ?? ContentSchema.EmptyDocument, row.New("ContentJson") ?? ContentSchema.EmptyDocument));
            })
            .WithTags("History")
            .WithName("GetEntryDiff")
            .WithSummary("Diff of a content entry (old → new): blocks with ops, ready-to-render HTML, word counts.");

        endpoints.MapGet("/api/history/entries/{entryId:long}/content", async Task<Ok<HistoricalContent>> (
                long entryId, IDocumentAuthorization authorization, ChangeHistory history, CancellationToken ct) =>
            {
                var row = await VisibleRowAsync(entryId, authorization, history, ct);
                var versionId = row.DocumentVersionId ?? (int.TryParse(row.Old("DocumentVersionId"), out var v) ? v : null);
                if (row.LogicalNodeId is not { } logicalNodeId || versionId is null || row.TableName is not ("app.DocumentNode" or "app.NodeContent"))
                {
                    throw DomainException.NotFound("Node entry", entryId);
                }

                // The node's header and content as of this entry: the latest rows of each at or before it (a delete shows what was deleted).
                var node = row.TableName == "app.DocumentNode" ? row : await history.LatestAsync("app.DocumentNode", logicalNodeId, versionId.Value, entryId, ct);
                var content = row.TableName == "app.NodeContent" ? row : await history.LatestAsync("app.NodeContent", logicalNodeId, versionId.Value, entryId, ct);
                string? Image(ChangeLogRow? r, string column) => r is null ? null : r.Operation == "D" ? r.Old(column) : r.New(column);
                var json = Image(content, "ContentJson") ?? ContentSchema.EmptyDocument;
                using var document = JsonDocument.Parse(ContentDiffService.ParseDocument(json).ToJsonString(), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
                return TypedResults.Ok(new HistoricalContent(
                    entryId, logicalNodeId, versionId.Value, row.ChangedAt, Image(node, "Title"), int.TryParse(Image(node, "NodeTypeId"), out var type) ? type : null,
                    document.RootElement.Clone(), ContentHtmlRenderer.Render(json).Html));
            })
            .WithTags("History")
            .WithName("GetEntryContent")
            .WithSummary("A node's content and header as of a history entry (for viewing and restoring an earlier state).");

        endpoints.MapGet("/api/versions/{versionId:int}/change-summary", async Task<Ok<ChangeSummary>> (
                int versionId, string? since, DocHubDbContext db, IDocumentAuthorization authorization, ChangeTracking tracking, CancellationToken ct) =>
            {
                var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct) ?? throw DomainException.NotFound("Version", versionId);
                await authorization.EnsureCanViewAsync(version.DocumentId, ct);
                return TypedResults.Ok(await tracking.SummaryAsync(version, await tracking.BaselineAsync(version, since, ct), ct));
            })
            .WithTags("History")
            .WithName("GetChangeSummary")
            .WithSummary("One call for all section badges: per node change counts, last change, script/after-signing flags and structural changes since a baseline (latestSigned, v:{id}, e:{entryId}, d:{isoDate}), plus removed nodes.");

        endpoints.MapGet("/api/documents/{id:int}/nodes/{logicalNodeId:guid}/changes", async Task<Ok<ContentDiff>> (
                int id, Guid logicalNodeId, string? since, string? until, DocHubDbContext db, IDocumentAuthorization authorization, ChangeTracking tracking,
                Microsoft.Extensions.Caching.Hybrid.HybridCache cache, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                long? untilEntry = until is null or "current" ? null
                    : until.StartsWith("e:", StringComparison.Ordinal) && long.TryParse(until.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e
                    : throw DomainException.Validation("until must be current or e:{entryId}.");

                // The state being tracked: the draft, else the current version.
                var target = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == id && (v.Status == Domain.Entities.VersionStatus.Draft || v.IsCurrent))
                    .OrderBy(v => v.Status).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("Current version of document", id);
                var baseline = await tracking.BaselineAsync(target, since, ct);

                // Cached per node, baseline and last change (NFR-L9): any new change of the node gives a new key.
                var last = (await db.Database.SqlQueryRaw<long>(
                        "SELECT COALESCE(MAX([Id]), 0) AS [Value] FROM [audit].[ChangeLog] WHERE [LogicalNodeId] = @l AND [DocumentId] = @d",
                        ChangeHistory.Parameter("@l", logicalNodeId), ChangeHistory.Parameter("@d", id)).ToListAsync(ct))[0];
                var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"changes:{id}:{logicalNodeId}:{target.Id}:{baseline.Key}:{baseline.Point}:{untilEntry}:{last}");
                return TypedResults.Ok(await cache.GetOrCreateAsync(key, async token => await tracking.ChangesAsync(target, logicalNodeId, baseline, untilEntry, token), cancellationToken: ct));
            })
            .WithTags("History")
            .WithName("GetNodeChanges")
            .WithSummary("Track changes: the diff of a node's text since a baseline, each insert/delete/format run attributed to the change (user or script + ticket) that made it.");

        endpoints.MapGet("/api/admin/audit", async Task<Ok<PagedResult<AuditRow>>> (
                DateTime? from, DateTime? to, string? table, string? operation, string? source, int? userId, string? dbLogin, string? ticket,
                [AsParameters] PageRequest paging, DocHubDbContext db, CancellationToken ct) =>
            {
                if (from is null || to is null || to <= from || to.Value - from.Value > TimeSpan.FromDays(MaxAuditDays))
                {
                    throw DomainException.Validation($"from and to are required and at most {MaxAuditDays} days apart.");
                }

                object[] Parameters() =>
                [
                    ChangeHistory.Parameter("@from", from.Value.ToUniversalTime()), ChangeHistory.Parameter("@to", to.Value.ToUniversalTime()),
                    ChangeHistory.Parameter("@table", table), ChangeHistory.Parameter("@op", operation), ChangeHistory.Parameter("@source", source),
                    ChangeHistory.Parameter("@user", userId), ChangeHistory.Parameter("@login", dbLogin), ChangeHistory.Parameter("@ticket", ticket),
                    ChangeHistory.Parameter("@skip", paging.Skip), ChangeHistory.Parameter("@take", paging.PageSize),
                ];
                const string Filter = """
                    FROM [audit].[ChangeLog]
                    WHERE [ChangedAt] >= @from AND [ChangedAt] < @to
                      AND (@table IS NULL OR [TableName] = @table) AND (@op IS NULL OR [Operation] = @op) AND (@source IS NULL OR [Source] = @source)
                      AND (@user IS NULL OR [UserId] = @user) AND (@login IS NULL OR [DbLogin] = @login) AND (@ticket IS NULL OR [Ticket] = @ticket)
                    """;
                // Not composed by EF (ToList, no Single/Take), so the OPTION clause stays at the statement's end; RECOMPILE fits the optional filters.
                var total = (await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS [Value] {Filter} OPTION (RECOMPILE)", Parameters()).ToListAsync(ct))[0];
                var rows = await db.Database.SqlQueryRaw<AuditRow>(
                        $"""
                        SELECT [Id], [ChangedAt], [TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],
                               [ChangedColumns], [UserId], [Source], [DbLogin], [AppName], [CorrelationId], [OperationContext], [Ticket], [Reason]
                        {Filter}
                        ORDER BY [ChangedAt] DESC, [Id] DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY OPTION (RECOMPILE)
                        """,
                        Parameters())
                    .ToListAsync(ct);
                return TypedResults.Ok(new PagedResult<AuditRow>(rows, paging.Page, paging.PageSize, total));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<PageRequest>()
            .WithTags("Admin")
            .WithName("ListAuditLog")
            .WithSummary($"Admin: raw audit.ChangeLog rows of every table, newest first; from/to required (≤ 31 days), other filters optional.");
    }

    /// <summary>An audit row whose document the caller may view (rows without a document: admins only); otherwise 404.</summary>
    private static async Task<ChangeLogRow> VisibleRowAsync(long entryId, IDocumentAuthorization authorization, ChangeHistory history, CancellationToken ct)
    {
        var row = await history.RowAsync(entryId, ct) ?? throw DomainException.NotFound("History entry", entryId);
        if (row.DocumentId is not { } documentId)
        {
            throw DomainException.NotFound("History entry", entryId);
        }

        await authorization.EnsureCanViewAsync(documentId, ct);
        return row;
    }

    private static PagedResult<HistoryEntry> Page(List<HistoryEntry> entries, PageRequest paging) =>
        new(entries.Skip(paging.Skip).Take(paging.PageSize).ToList(), paging.Page, paging.PageSize, entries.Count);

    public sealed record HistoricalContent(
        long EntryId, Guid LogicalNodeId, int VersionId, DateTime ChangedAt, string? Title, int? NodeTypeId, JsonElement ContentJson, string ContentHtml);

    public sealed class AuditRow
    {
        public long Id { get; set; }

        public DateTime ChangedAt { get; set; }

        public string TableName { get; set; } = "";

        public string Operation { get; set; } = "";

        public int EntityId { get; set; }

        public int? DocumentId { get; set; }

        public int? DocumentVersionId { get; set; }

        public Guid? LogicalNodeId { get; set; }

        public string? OldValues { get; set; }

        public string? NewValues { get; set; }

        public string? ChangedColumns { get; set; }

        public int? UserId { get; set; }

        public string Source { get; set; } = "";

        public string DbLogin { get; set; } = "";

        public string? AppName { get; set; }

        public string? CorrelationId { get; set; }

        public string? OperationContext { get; set; }

        public string? Ticket { get; set; }

        public string? Reason { get; set; }
    }
}
