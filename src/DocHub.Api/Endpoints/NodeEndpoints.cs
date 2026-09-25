using System.ComponentModel.DataAnnotations;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Errors;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Domain.Ordering;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DocHub.Api.Endpoints;

/// <summary>
/// The node tree of a version (T08, FR-T1…T3): readable for every version, edited only in drafts and only by the owner
/// (editors change text, never structure).
/// </summary>
internal sealed class NodeEndpoints : IEndpointModule
{
    /// <summary>The content of a new node (canonical empty document).</summary>
    public const string EmptyContent = ContentSchema.EmptyDocument;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/versions/{versionId:int}/tree", async (
                int versionId, HttpContext http, DocHubDbContext db, IDocumentAuthorization authorization, HybridCache cache, CancellationToken ct) =>
            {
                var version = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => new { v.DocumentId, v.Status }).SingleOrDefaultAsync(ct)
                    ?? throw DomainException.NotFound("Version", versionId);
                await authorization.EnsureCanViewAsync(version.DocumentId, ct);

                var stamp = await VersionETag.StampAsync(db, versionId, ct);
                if (VersionETag.IsNotModified(http, stamp))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                // Signed trees don't change except by tampering, which advances the stamp: cache by version + stamp.
                var tree = version.Status == VersionStatus.Signed
                    ? await cache.GetOrCreateAsync($"tree:{versionId}:{stamp}", async token => VersionTree.Build(await VersionTree.LoadAsync(db, versionId, token)), cancellationToken: ct)
                    : VersionTree.Build(await VersionTree.LoadAsync(db, versionId, ct));
                return Results.Ok(tree);
            })
            .Produces<List<TreeNode>>()
            .Produces(StatusCodes.Status304NotModified)
            .WithTags("Tree")
            .WithName("GetTree")
            .WithSummary("The nested tree of a version (any status) with numbering; ETag = version stamp.");

        endpoints.MapGet("/api/nodes/{nodeId:int}", async Task<Ok<NodeResponse>> (int nodeId, DocHubDbContext db, IDocumentAuthorization authorization, CancellationToken ct) =>
            {
                var (_, documentId) = await NodeVersionAsync(db, nodeId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);
                return TypedResults.Ok(await DetailsAsync(db, nodeId, ct));
            })
            .WithTags("Tree")
            .WithName("GetNode")
            .WithSummary("A node with its ancestors (path).");

        endpoints.MapPost("/api/versions/{versionId:int}/nodes", async Task<Results<Created<NodeResponse>, ValidationProblem>> (
                int versionId, CreateNode request, DocHubDbContext db, NodeRules rules, ICurrentUser user, CancellationToken ct) =>
            {
                await rules.EnsureCanEditStructureAsync(versionId, ct);
                if (await rules.ValidateAsync(request.Title, request.NodeTypeId, ct) is { } errors)
                {
                    return errors;
                }

                var node = await rules.MutateAsync(versionId, async () =>
                {
                    if (request.ParentNodeId is { } parentId && !await db.DocumentNodes.AnyAsync(n => n.Id == parentId && n.DocumentVersionId == versionId, ct))
                    {
                        throw DomainException.Validation("The parent node is not part of this version.");
                    }

                    var created = new DocumentNode
                    {
                        DocumentVersionId = versionId,
                        LogicalNodeId = Guid.NewGuid(),
                        ParentNodeId = request.ParentNodeId,
                        NodeTypeId = request.NodeTypeId,
                        Title = request.Title!.Trim(),
                        SortOrder = await PlaceAsync(db, versionId, request.ParentNodeId, null, request.Position, ct),
                        CreatedByUserId = user.UserId,
                        ModifiedByUserId = user.UserId,
                    };
                    db.DocumentNodes.Add(created);
                    await db.SaveChangesAsync(ct);
                    db.NodeContents.Add(new NodeContent
                    {
                        NodeId = created.Id,
                        DocumentVersionId = versionId,
                        LogicalNodeId = created.LogicalNodeId,
                        ContentJson = EmptyContent,
                        ContentHash = CanonicalJson.Hash(EmptyContent),
                        ModifiedByUserId = user.UserId,
                    });
                    await db.SaveChangesAsync(ct);
                    return created;
                }, ct);
                return TypedResults.Created($"/api/nodes/{node.Id}", await DetailsAsync(db, node.Id, ct));
            })
            .WithValidation<CreateNode>()
            .WithTags("Tree")
            .WithName("CreateNode")
            .WithSummary("Owner, draft only: adds a node (with empty content) under a parent at a position (default last).");

        endpoints.MapPatch("/api/nodes/{nodeId:int}", async Task<Results<Ok<NodeResponse>, ValidationProblem>> (
                int nodeId, UpdateNode request, DocHubDbContext db, NodeRules rules, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
            {
                var (versionId, _) = await NodeVersionAsync(db, nodeId, ct);
                await rules.EnsureCanEditStructureAsync(versionId, ct);
                var current = await db.DocumentNodes.AsNoTracking().SingleAsync(n => n.Id == nodeId, ct);
                if (await rules.ValidateAsync(request.Title ?? current.Title, request.NodeTypeId == current.NodeTypeId ? null : request.NodeTypeId, ct) is { } errors)
                {
                    return errors;
                }

                await rules.MutateAsync(versionId, async () =>
                {
                    var node = await db.DocumentNodes.SingleOrDefaultAsync(n => n.Id == nodeId, ct) ?? throw DomainException.NotFound("Node", nodeId);
                    db.Entry(node).Property(n => n.RowVersion).OriginalValue = request.RowVersion!;
                    node.Title = (request.Title ?? node.Title).Trim();
                    node.NodeTypeId = request.NodeTypeId ?? node.NodeTypeId;
                    node.ModifiedAt = time.GetUtcNow().UtcDateTime;
                    node.ModifiedByUserId = user.UserId;
                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                return TypedResults.Ok(await DetailsAsync(db, nodeId, ct));
            })
            .WithValidation<UpdateNode>()
            .WithTags("Tree")
            .WithName("UpdateNode")
            .WithSummary("Owner, draft only: renames a node and/or changes its type (rowVersion required).");

        endpoints.MapPost("/api/nodes/{nodeId:int}/move", async Task<Ok<NodeResponse>> (
                int nodeId, MoveNode request, DocHubDbContext db, NodeRules rules, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
            {
                var (versionId, _) = await NodeVersionAsync(db, nodeId, ct);
                await rules.EnsureCanEditStructureAsync(versionId, ct);
                await rules.MutateAsync(versionId, async () =>
                {
                    var parents = await db.DocumentNodes.AsNoTracking().Where(n => n.DocumentVersionId == versionId).ToDictionaryAsync(n => n.Id, n => n.ParentNodeId, ct);
                    if (request.NewParentNodeId is { } parentId)
                    {
                        if (!parents.ContainsKey(parentId))
                        {
                            throw DomainException.Validation("The new parent node is not part of this version.");
                        }

                        var steps = 0;
                        for (int? current = parentId; current is { } c && parents.ContainsKey(c) && steps++ <= parents.Count; current = parents[c])
                        {
                            if (c == nodeId)
                            {
                                throw DomainException.Conflict(ErrorCodes.InvalidMove, "A node can't be moved into itself or into one of its descendants.");
                            }
                        }
                    }

                    var node = await db.DocumentNodes.SingleOrDefaultAsync(n => n.Id == nodeId, ct) ?? throw DomainException.NotFound("Node", nodeId);
                    db.Entry(node).Property(n => n.RowVersion).OriginalValue = request.RowVersion!;
                    node.SortOrder = await PlaceAsync(db, versionId, request.NewParentNodeId, nodeId, request.Position, ct);
                    node.ParentNodeId = request.NewParentNodeId;
                    node.ModifiedAt = time.GetUtcNow().UtcDateTime;
                    node.ModifiedByUserId = user.UserId;
                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                return TypedResults.Ok(await DetailsAsync(db, nodeId, ct));
            })
            .WithValidation<MoveNode>()
            .WithTags("Tree")
            .WithName("MoveNode")
            .WithSummary("Owner, draft only: re-parents and/or reorders a node within its version; position = 0-based index (default last).");

        endpoints.MapDelete("/api/nodes/{nodeId:int}", async Task<Ok<DeletedNodes>> (
                int nodeId, string? rowVersion, DocHubDbContext db, NodeRules rules, IDbProcedures procedures, CancellationToken ct) =>
            {
                var expected = RowVersions.Parse(rowVersion);
                var (versionId, _) = await NodeVersionAsync(db, nodeId, ct);
                await rules.EnsureCanEditStructureAsync(versionId, ct);
                var deleted = await rules.MutateAsync(versionId, async () =>
                {
                    var current = await db.DocumentNodes.AsNoTracking().Where(n => n.Id == nodeId).Select(n => n.RowVersion).SingleOrDefaultAsync(ct)
                        ?? throw DomainException.NotFound("Node", nodeId);
                    if (!current.AsSpan().SequenceEqual(expected))
                    {
                        throw new DbUpdateConcurrencyException("The node was changed by someone else.");
                    }

                    return await procedures.DeleteSubtreeAsync(nodeId, ct);
                }, ct);
                return TypedResults.Ok(new DeletedNodes(deleted));
            })
            .WithTags("Tree")
            .WithName("DeleteNode")
            .WithSummary("Owner, draft only: deletes a node with its whole subtree and contents (usp_DeleteSubtree; ?rowVersion=base64).");
    }

    private static async Task<(int VersionId, int DocumentId)> NodeVersionAsync(DocHubDbContext db, int nodeId, CancellationToken ct)
    {
        var node = await (from n in db.DocumentNodes.AsNoTracking()
                          where n.Id == nodeId
                          join v in db.DocumentVersions.AsNoTracking() on n.DocumentVersionId equals v.Id
                          select new { n.DocumentVersionId, v.DocumentId }).SingleOrDefaultAsync(ct)
            ?? throw DomainException.NotFound("Node", nodeId);
        return (node.DocumentVersionId, node.DocumentId);
    }

    /// <summary>Sort order at <paramref name="position"/> among the siblings under <paramref name="parentId"/>, renumbering them when no gap is left.</summary>
    private static async Task<int> PlaceAsync(DocHubDbContext db, int versionId, int? parentId, int? excludeId, int? position, CancellationToken ct)
    {
        var siblings = await db.DocumentNodes
            .Where(n => n.DocumentVersionId == versionId && n.ParentNodeId == parentId && n.Id != excludeId)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Id)
            .ToListAsync(ct);
        var (sortOrder, renumbered) = SiblingOrder.Place(siblings.Select(n => n.SortOrder).ToList(), position);
        if (renumbered is not null)
        {
            for (var i = 0; i < siblings.Count; i++)
            {
                siblings[i].SortOrder = renumbered[i];
            }
        }

        return sortOrder;
    }

    private static async Task<NodeResponse> DetailsAsync(DocHubDbContext db, int nodeId, CancellationToken ct)
    {
        var node = await db.DocumentNodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, ct) ?? throw DomainException.NotFound("Node", nodeId);
        var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == node.DocumentVersionId).Select(v => v.DocumentId).SingleAsync(ct);
        var flat = await VersionTree.LoadAsync(db, node.DocumentVersionId, ct);
        var numbers = VersionTree.Numbers(flat);
        var byId = flat.ToDictionary(n => n.Id);
        var path = new List<NodeRef>();
        var seen = new HashSet<int>();
        for (var current = node.ParentNodeId; current is { } c && byId.TryGetValue(c, out var parent) && seen.Add(c); current = parent.ParentNodeId)
        {
            path.Insert(0, new NodeRef(parent.Id, parent.Title, numbers.GetValueOrDefault(parent.Id, "")));
        }

        return new NodeResponse(node.Id, node.DocumentVersionId, documentId, node.LogicalNodeId, node.ParentNodeId, node.NodeTypeId, node.Title,
            numbers.GetValueOrDefault(node.Id, ""), byId.TryGetValue(node.Id, out var self) && self.HasContent, node.SortOrder, node.RowVersion,
            node.CreatedAt, node.CreatedByUserId, node.ModifiedAt, node.ModifiedByUserId, path);
    }

    public sealed record NodeRef(int Id, string Title, string Number);

    public sealed record NodeResponse(
        int Id, int VersionId, int DocumentId, Guid LogicalNodeId, int? ParentNodeId, int NodeTypeId, string Title, string Number, bool HasContent, int SortOrder,
        byte[] RowVersion, DateTime CreatedAt, int CreatedByUserId, DateTime ModifiedAt, int ModifiedByUserId, IReadOnlyList<NodeRef> Path);

    public sealed record DeletedNodes(int DeletedCount);

    public sealed class CreateNode
    {
        public int? ParentNodeId { get; init; }

        [Range(1, int.MaxValue)]
        public int NodeTypeId { get; init; }

        [Required]
        public string? Title { get; init; }

        [Range(0, int.MaxValue)]
        public int? Position { get; init; }
    }

    public sealed class UpdateNode
    {
        public string? Title { get; init; }

        [Range(1, int.MaxValue)]
        public int? NodeTypeId { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed class MoveNode
    {
        public int? NewParentNodeId { get; init; }

        [Range(0, int.MaxValue)]
        public int? Position { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }
}

/// <summary>Shared checks of structural node operations (T08 rules).</summary>
public sealed class NodeRules(DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard)
{
    /// <summary>View (404) → editable draft (409) → owner (403). Returns the document id.</summary>
    public async Task<int> EnsureCanEditStructureAsync(int versionId, CancellationToken cancellationToken)
    {
        var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => (int?)v.DocumentId).SingleOrDefaultAsync(cancellationToken)
            ?? throw DomainException.NotFound("Version", versionId);
        await authorization.EnsureCanViewAsync(documentId, cancellationToken);
        await guard.EnsureEditableAsync(versionId, cancellationToken);
        await authorization.DemandAsync(documentId, PermissionAction.EditStructure, cancellationToken);
        return documentId;
    }

    /// <summary>
    /// Runs a structural change in a transaction holding the document's lifecycle lock (the one signing, discarding and new
    /// drafts take), after re-checking inside it that the version is still an editable draft — so a change can never land
    /// in a version that was just signed or discarded. The lock also serializes tree changes (cycle checks, positions).
    /// </summary>
    public Task<T> MutateAsync<T>(int versionId, Func<Task<T>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return db.InTransactionAsync(async () =>
        {
            var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => (int?)v.DocumentId).SingleOrDefaultAsync(cancellationToken)
                ?? throw DomainException.NotFound("Version", versionId);
            await db.LockAsync(SigningService.LockResource(documentId), cancellationToken);
            await guard.EnsureEditableAsync(versionId, cancellationToken);
            return await change();
        }, cancellationToken);
    }

    /// <summary>Title 1–500 characters (trimmed); a new or changed node type must exist and be active.</summary>
    public async Task<ValidationProblem?> ValidateAsync(string? title, int? nodeTypeId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length is 0 or > 500)
        {
            errors["title"] = ["The title is required and can have at most 500 characters."];
        }

        if (nodeTypeId is { } typeId && !await db.NodeTypes.AnyAsync(t => t.Id == typeId && t.IsActive, cancellationToken))
        {
            errors["nodeTypeId"] = ["The node type doesn't exist or is inactive."];
        }

        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors, title: "Validation failed", type: ErrorCodes.ValidationFailed);
    }
}
