using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Errors;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DocHub.Api.Endpoints;

/// <summary>
/// Styled rich-text content of nodes (T09, FR-T4): TipTap/ProseMirror JSON validated against DocHub Content Schema v1
/// (docs/content-format.md), stored canonicalized with server-rendered HTML and plain text.
/// </summary>
internal sealed class ContentEndpoints : IEndpointModule
{
    public const int MaxBatch = 200;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/content-schema", (StyleProperties styles) => TypedResults.Ok(ContentSchema.Describe(styles.FontFamilies.Order(StringComparer.Ordinal))))
            .WithTags("Content")
            .WithName("GetContentSchema")
            .WithSummary("DocHub Content Schema v1: node and mark types, attributes with ranges and values, units, the font list.");

        endpoints.MapGet("/api/nodes/{nodeId:int}/content", async (
                int nodeId, string? format, HttpContext http, DocHubDbContext db, IDocumentAuthorization authorization, NodeContents contents, HybridCache cache,
                CancellationToken ct) =>
            {
                var node = await NodeAsync(db, nodeId, ct);
                await authorization.EnsureCanViewAsync(node.DocumentId, ct);
                var parsed = Format(format);
                var stamp = await VersionETag.StampAsync(db, node.VersionId, ct);
                var rows = await RowVersionsAsync(db, [nodeId], ct);
                if (rows.Count == 0)
                {
                    throw DomainException.NotFound("Content of node", nodeId);
                }

                if (VersionETag.IsNotModified(http, stamp, rows))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                var views = await ReadAsync(contents, cache, node.VersionId, node.Status, rows, [nodeId], parsed, ct);
                return views.Count == 0 ? throw DomainException.NotFound("Content of node", nodeId) : Results.Ok(views[0]);
            })
            .Produces<NodeContentView>()
            .Produces(StatusCodes.Status304NotModified)
            .WithTags("Content")
            .WithName("GetNodeContent")
            .WithSummary("A node's content: ?format=json (default), html or both; ETag = version stamp + row version.");

        endpoints.MapGet("/api/versions/{versionId:int}/content", async (
                int versionId, string? nodeIds, string? format, HttpContext http, DocHubDbContext db, IDocumentAuthorization authorization, NodeContents contents,
                HybridCache cache, CancellationToken ct) =>
            {
                var version = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => new { v.DocumentId, v.Status }).SingleOrDefaultAsync(ct)
                    ?? throw DomainException.NotFound("Version", versionId);
                await authorization.EnsureCanViewAsync(version.DocumentId, ct);
                var parsed = Format(format);
                var ids = NodeIds(nodeIds);
                var inVersion = await db.DocumentNodes.AsNoTracking().Where(n => n.DocumentVersionId == versionId && ids.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct);
                if (ids.Except(inVersion).FirstOrDefault() is var missing and not 0)
                {
                    throw DomainException.NotFound("Node of this version", missing);
                }

                var stamp = await VersionETag.StampAsync(db, versionId, ct);
                var rows = await RowVersionsAsync(db, ids, ct);
                if (VersionETag.IsNotModified(http, stamp, rows))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Ok(await ReadAsync(contents, cache, versionId, version.Status, rows, ids, parsed, ct));
            })
            .Produces<List<NodeContentView>>()
            .Produces(StatusCodes.Status304NotModified)
            .WithTags("Content")
            .WithName("GetVersionContents")
            .WithSummary($"Contents of several nodes of a version (?nodeIds=1,2,3, at most 200) in the given order, e.g. to render a document; ETag = version stamp + row versions.");

        endpoints.MapPut("/api/nodes/{nodeId:int}/content", async Task<Results<Ok<NodeContentView>, ValidationProblem>> (
                int nodeId, SaveContent request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, NodeRules rules,
                NodeContents contents, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
            {
                var node = await NodeAsync(db, nodeId, ct);
                await authorization.EnsureCanViewAsync(node.DocumentId, ct);
                await guard.EnsureEditableAsync(node.VersionId, ct);
                await authorization.DemandAsync(node.DocumentId, PermissionAction.EditContent, ct, node.VersionId, node.LogicalNodeId);

                var json = request.ContentJson!.Value;
                var schema = await contents.SchemaAsync(ct);
                var errors = System.Text.Encoding.UTF8.GetByteCount(json.GetRawText()) > ContentSchema.MaxBytes
                    ? new Dictionary<string, string[]> { ["contentJson"] = [$"Content is larger than {ContentSchema.MaxBytes / 1024 / 1024} MB."] }
                    : schema.Validate(json).ToDictionary(e => e.Key.Length == 0 ? "contentJson" : $"contentJson.{e.Key}", e => e.Value);
                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors, title: "The content doesn't match DocHub Content Schema v1", type: ErrorCodes.ValidationFailed);
                }

                var canonical = schema.Canonicalize(json);
                // Under the document's lifecycle lock, re-checking the draft: a save never lands in a version just signed.
                await rules.MutateAsync(node.VersionId, async () =>
                {
                    var content = await db.NodeContents.SingleOrDefaultAsync(c => c.NodeId == nodeId, ct) ?? throw DomainException.NotFound("Content of node", nodeId);
                    if (!content.RowVersion.AsSpan().SequenceEqual(request.RowVersion))
                    {
                        throw new DbUpdateConcurrencyException("The content was changed by someone else.");
                    }

                    // Rule 5: unchanged content (compared with the stored JSON itself, not its hash) is not written — no audit noise.
                    if (content.ContentJson == canonical)
                    {
                        return false;
                    }

                    db.Entry(content).Property(c => c.RowVersion).OriginalValue = request.RowVersion!;
                    await contents.ApplyAsync(content, canonical, schema, ct);
                    content.ModifiedAt = time.GetUtcNow().UtcDateTime;
                    content.ModifiedByUserId = user.UserId;
                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                return TypedResults.Ok((await contents.ViewsAsync([nodeId], ContentFormat.Json, ct)).Single());
            })
            .WithValidation<SaveContent>()
            .WithTags("Content")
            .WithName("SaveNodeContent")
            .WithSummary("Owner or content editor, draft only: saves content (rowVersion required); returns the canonicalized JSON. Validation errors carry JSON paths.");
    }

    /// <summary>
    /// Views of nodes of one version; a signed version's contents are cached by row version (only tampering and the
    /// derived-content refresher change them).
    /// </summary>
    private static async Task<List<NodeContentView>> ReadAsync(
        NodeContents contents, HybridCache cache, int versionId, VersionStatus status, List<(int NodeId, byte[] RowVersion)> rows, List<int> nodeIds,
        ContentFormat format, CancellationToken ct)
    {
        if (status != VersionStatus.Signed)
        {
            return await contents.ViewsAsync(nodeIds, format, ct);
        }

        var rowVersions = rows.ToDictionary(r => r.NodeId, r => r.RowVersion);
        var views = new List<NodeContentView>(nodeIds.Count);
        foreach (var nodeId in nodeIds.Where(rowVersions.ContainsKey))
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"content:{versionId}:{nodeId}:{Convert.ToHexString(rowVersions[nodeId])}:{format}");
            var cached = await cache.GetOrCreateAsync(key, async token => (await contents.ViewsAsync([nodeId], format, token)).SingleOrDefault(), cancellationToken: ct);
            if (cached is not null)
            {
                views.Add(cached);
            }
        }

        return views;
    }

    /// <summary>The content row versions of the nodes, in the order of <paramref name="nodeIds"/> (nodes without content left out).</summary>
    private static async Task<List<(int NodeId, byte[] RowVersion)>> RowVersionsAsync(DocHubDbContext db, List<int> nodeIds, CancellationToken ct)
    {
        var found = await db.NodeContents.AsNoTracking().Where(c => nodeIds.Contains(c.NodeId)).Select(c => new { c.NodeId, c.RowVersion }).ToDictionaryAsync(c => c.NodeId, c => c.RowVersion, ct);
        return nodeIds.Where(found.ContainsKey).Select(id => (id, found[id])).ToList();
    }

    private static async Task<(int VersionId, int DocumentId, VersionStatus Status, Guid LogicalNodeId)> NodeAsync(DocHubDbContext db, int nodeId, CancellationToken ct)
    {
        var node = await (from n in db.DocumentNodes.AsNoTracking()
                          where n.Id == nodeId
                          join v in db.DocumentVersions.AsNoTracking() on n.DocumentVersionId equals v.Id
                          select new { n.DocumentVersionId, v.DocumentId, v.Status, n.LogicalNodeId }).SingleOrDefaultAsync(ct)
            ?? throw DomainException.NotFound("Node", nodeId);
        return (node.DocumentVersionId, node.DocumentId, node.Status, node.LogicalNodeId);
    }

    private static ContentFormat Format(string? format) =>
        NodeContents.ParseFormat(format) ?? throw DomainException.Validation("format must be json, html or both.");

    /// <summary>A comma-separated list of 1–200 distinct node ids.</summary>
    private static List<int> NodeIds(string? value)
    {
        var parts = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw DomainException.Validation("nodeIds must be a comma-separated list of node ids.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count is 0 or > MaxBatch ? throw DomainException.Validation($"nodeIds must list 1 to {MaxBatch} nodes.") : ids;
    }

    public sealed class SaveContent
    {
        /// <summary>The document: <c>{ "type": "doc", "content": [ … ] }</c>.</summary>
        [Required]
        public JsonElement? ContentJson { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }
}
