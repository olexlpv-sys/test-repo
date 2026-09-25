using System.Globalization;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Domain.Ordering;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content.Diff;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DocHub.Api.Endpoints;

/// <summary>
/// Version comparison (T12, FR-C1/C2): two versions of a document at structure and content level — nodes matched by
/// LogicalNodeId, one merged tree (target structure with removed nodes at their base position), content diffs per node.
/// </summary>
internal sealed class CompareEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents/{id:int}/compare", async Task<Ok<Comparison>> (
                int id, string? @base, string? target, bool? includeUnchanged, DocHubDbContext db, IDocumentAuthorization authorization, IContentDiffService diff,
                HybridCache cache, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var (baseVersion, targetVersion) = await ResolveAsync(db, id, @base, target, ct);
                async Task<Comparison> Compute(CancellationToken token) => await CompareAsync(db, diff, baseVersion, targetVersion, includeUnchanged ?? false, token);
                if (baseVersion.Status != VersionStatus.Signed || targetVersion.Status != VersionStatus.Signed)
                {
                    return TypedResults.Ok(await Compute(ct));
                }

                // Signed versions change only by tampering, which advances their stamps (NFR-L9).
                var key = string.Create(CultureInfo.InvariantCulture,
                    $"compare:{baseVersion.Id}:{await VersionETag.StampAsync(db, baseVersion.Id, ct)}:{targetVersion.Id}:{await VersionETag.StampAsync(db, targetVersion.Id, ct)}:{includeUnchanged ?? false}");
                return TypedResults.Ok(await cache.GetOrCreateAsync(key, async token => await Compute(token), cancellationToken: ct));
            })
            .WithTags("Compare")
            .WithName("CompareVersions")
            .WithSummary("Compares two versions (base=versionId|latestSigned, target=versionId|draft): summary and one merged tree; unchanged branches are left out unless includeUnchanged.");

        endpoints.MapGet("/api/documents/{id:int}/compare/nodes/{logicalNodeId:guid}", async Task<Ok<ContentDiff>> (
                int id, Guid logicalNodeId, string? @base, string? target, DocHubDbContext db, IDocumentAuthorization authorization, IContentDiffService diff, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var (baseVersion, targetVersion) = await ResolveAsync(db, id, @base, target, ct);
                var contents = await (from n in db.DocumentNodes.AsNoTracking()
                                      where (n.DocumentVersionId == baseVersion.Id || n.DocumentVersionId == targetVersion.Id) && n.LogicalNodeId == logicalNodeId
                                      join c in db.NodeContents.AsNoTracking() on n.Id equals c.NodeId into cs
                                      from c in cs.DefaultIfEmpty()
                                      select new { n.DocumentVersionId, Json = c == null ? null : c.ContentJson }).ToListAsync(ct);
                if (contents.Count == 0)
                {
                    throw DomainException.NotFound("Node in these versions", logicalNodeId);
                }

                string Of(int versionId) => contents.FirstOrDefault(c => c.DocumentVersionId == versionId)?.Json ?? ContentSchema.EmptyDocument;
                return TypedResults.Ok(diff.Diff(Of(baseVersion.Id), Of(targetVersion.Id)));
            })
            .WithTags("Compare")
            .WithName("CompareNode")
            .WithSummary("Content diff of one node between two versions (same format as the history diff).");
    }

    /// <summary>base/target of the document (400 for another document's or an unknown version, 404 for a missing draft/signed version).</summary>
    private static async Task<(DocumentVersion Base, DocumentVersion Target)> ResolveAsync(DocHubDbContext db, int documentId, string? @base, string? target, CancellationToken ct)
    {
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == documentId).ToListAsync(ct);
        DocumentVersion Resolve(string? value, string name) => value switch
        {
            null => throw DomainException.Validation($"{name} is required."),
            "draft" => versions.SingleOrDefault(v => v.Status == VersionStatus.Draft) ?? throw DomainException.NotFound("Draft of document", documentId),
            "latestSigned" => versions.Where(v => v.Status == VersionStatus.Signed).MaxBy(v => v.VersionNumber) ?? throw DomainException.NotFound("Signed version of document", documentId),
            _ when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var versionId) =>
                versions.SingleOrDefault(v => v.Id == versionId) ?? throw DomainException.Validation($"{name} is not a version of this document."),
            _ => throw DomainException.Validation($"{name} must be a version id, latestSigned or draft."),
        };
        return (Resolve(@base, "base"), Resolve(target, "target"));
    }

    private sealed record Loaded(SnapshotRow Node, byte[] Hash);

    private sealed record SnapshotRow(int Id, int? ParentNodeId, Guid LogicalNodeId, int NodeTypeId, string Title, int SortOrder);

    private static async Task<Dictionary<Guid, Loaded>> LoadAsync(DocHubDbContext db, int versionId, CancellationToken ct)
    {
        var rows = await (from n in db.DocumentNodes.AsNoTracking()
                          where n.DocumentVersionId == versionId
                          join c in db.NodeContents.AsNoTracking() on n.Id equals c.NodeId into cs
                          from c in cs.DefaultIfEmpty()
                          select new
                          {
                              Node = new SnapshotRow(n.Id, n.ParentNodeId, n.LogicalNodeId, n.NodeTypeId, n.Title, n.SortOrder),
                              Hash = c == null ? null : c.ContentHash,
                              Stale = c != null && c.DerivedStale,
                          }).ToListAsync(ct);

        // A script-edited row's stored hash may be stale: hash its JSON instead.
        var staleIds = rows.Where(r => r.Stale).Select(r => r.Node.Id).ToList();
        var fresh = staleIds.Count == 0 ? [] : await db.NodeContents.AsNoTracking().Where(c => staleIds.Contains(c.NodeId))
            .ToDictionaryAsync(c => c.NodeId, c => CanonicalJson.Hash(c.ContentJson), ct);
        return rows.GroupBy(r => r.Node.LogicalNodeId).ToDictionary(g => g.Key, g =>
        {
            var r = g.First();
            return new Loaded(r.Node, fresh.TryGetValue(r.Node.Id, out var h) ? h : r.Hash ?? CanonicalJson.Hash(ContentSchema.EmptyDocument));
        });
    }

    private static async Task<Comparison> CompareAsync(DocHubDbContext db, IContentDiffService diff, DocumentVersion baseVersion, DocumentVersion targetVersion, bool includeUnchanged, CancellationToken ct)
    {
        var numbers = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == baseVersion.DocumentId && v.VersionNumber != null)
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber!.Value, ct);
        var types = await db.NodeTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var before = await LoadAsync(db, baseVersion.Id, ct);
        var after = await LoadAsync(db, targetVersion.Id, ct);
        var beforeLayout = Layout(before.Values.Select(l => l.Node).ToList());
        var afterLayout = Layout(after.Values.Select(l => l.Node).ToList());

        // Reordered: among matched siblings under the same parent, the ones outside the LCS of both orders.
        var reordered = new HashSet<Guid>();
        foreach (var parent in afterLayout.Children.Keys)
        {
            var newOrder = afterLayout.Children[parent].Where(before.ContainsKey).ToList();
            var oldOrder = (beforeLayout.Children.GetValueOrDefault(parent) ?? []).Where(after.ContainsKey)
                .Where(l => beforeLayout.Parent.GetValueOrDefault(l) == afterLayout.Parent.GetValueOrDefault(l)).ToList();
            newOrder = newOrder.Where(oldOrder.Contains).ToList();
            reordered.UnionWith(newOrder.Except(SiblingOrder.Stayed(oldOrder, newOrder)));
        }

        // Content stats only for changed contents.
        var changedContent = before.Keys.Where(after.ContainsKey).Where(l => !before[l].Hash.AsSpan().SequenceEqual(after[l].Hash)).ToHashSet();
        var ids = changedContent.SelectMany(l => new[] { before[l].Node.Id, after[l].Node.Id }).ToList();
        var json = ids.Count == 0 ? [] : await db.NodeContents.AsNoTracking().Where(c => ids.Contains(c.NodeId)).ToDictionaryAsync(c => c.NodeId, c => c.ContentJson, ct);
        var stats = changedContent.ToDictionary(l => l, l => diff.Diff(json.GetValueOrDefault(before[l].Node.Id) ?? ContentSchema.EmptyDocument, json.GetValueOrDefault(after[l].Node.Id) ?? ContentSchema.EmptyDocument).Stats);

        NodeSide Side(Dictionary<Guid, Loaded> tree, (Dictionary<Guid, string> Numbers, Dictionary<Guid, Guid?> Parent, Dictionary<Guid, List<Guid>> Children) layout, Guid l) =>
            new(tree[l].Node.Title, layout.Numbers.GetValueOrDefault(l, ""), types.GetValueOrDefault(tree[l].Node.NodeTypeId, "?"), layout.Parent.GetValueOrDefault(l));

        CompareNode Node(Guid l, List<CompareNode> children)
        {
            var (inBase, inTarget) = (before.ContainsKey(l), after.ContainsKey(l));
            var changes = new List<string>();
            if (inBase && inTarget)
            {
                var (b, t) = (before[l].Node, after[l].Node);
                if (b.Title != t.Title) { changes.Add("Renamed"); }
                if (b.NodeTypeId != t.NodeTypeId) { changes.Add("TypeChanged"); }
                if (beforeLayout.Parent.GetValueOrDefault(l) != afterLayout.Parent.GetValueOrDefault(l)) { changes.Add("Moved"); }
                if (reordered.Contains(l)) { changes.Add("Reordered"); }
                if (changedContent.Contains(l)) { changes.Add("ContentChanged"); }
            }

            var status = !inBase ? "Added" : !inTarget ? "Removed" : changes.Count > 0 ? "Modified" : "Unchanged";
            return new CompareNode(l, status, changes, inBase ? Side(before, beforeLayout, l) : null, inTarget ? Side(after, afterLayout, l) : null,
                stats.TryGetValue(l, out var s) ? s : null, children);
        }

        // Merged tree: target structure; removed nodes at their base position (under their base parent, else the nearest ancestor that still exists).
        var merged = afterLayout.Children.ToDictionary(c => c.Key, c => c.Value.ToList());
        foreach (var removed in before.Keys.Where(l => !after.ContainsKey(l)).OrderBy(l => beforeLayout.Numbers.GetValueOrDefault(l, ""), StringComparer.Ordinal))
        {
            Guid? parent = beforeLayout.Parent.GetValueOrDefault(removed);
            while (parent is { } p && !after.ContainsKey(p) && before.ContainsKey(p) && !merged.Values.Any(list => list.Contains(p)))
            {
                parent = beforeLayout.Parent.GetValueOrDefault(p);
            }

            var key = parent ?? Guid.Empty;
            var list = merged.TryGetValue(key, out var existing) ? existing : merged[key] = [];
            var basePosition = (beforeLayout.Children.GetValueOrDefault(beforeLayout.Parent.GetValueOrDefault(removed) ?? Guid.Empty) ?? []).IndexOf(removed);
            list.Insert(Math.Clamp(basePosition, 0, list.Count), removed);
        }

        List<CompareNode> Build(Guid parent) =>
            (merged.GetValueOrDefault(parent) ?? []).Select(l => Node(l, Build(l))).Where(n => includeUnchanged || n.Status != "Unchanged" || n.Children.Count > 0).ToList();

        var all = before.Keys.Union(after.Keys).Select(l => Node(l, [])).ToList();
        var summary = new CompareSummary(
            all.Count(n => n.Status == "Added"), all.Count(n => n.Status == "Removed"), all.Count(n => n.Changes.Contains("Moved")), all.Count(n => n.Changes.Contains("Renamed")),
            all.Count(n => n.Changes.Contains("TypeChanged")), all.Count(n => n.Changes.Contains("ContentChanged")), all.Count(n => n.Status == "Unchanged"));
        return new Comparison(
            new VersionRef(baseVersion.Id, DocumentViews.Label(baseVersion, numbers)), new VersionRef(targetVersion.Id, DocumentViews.Label(targetVersion, numbers)),
            summary, Build(Guid.Empty));
    }

    /// <summary>Numbers, parents and ordered children (by logical id; the root's key is <see cref="Guid.Empty"/>).</summary>
    private static (Dictionary<Guid, string> Numbers, Dictionary<Guid, Guid?> Parent, Dictionary<Guid, List<Guid>> Children) Layout(List<SnapshotRow> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var flat = nodes.Select(n => new FlatNode(n.Id, n.ParentNodeId, n.LogicalNodeId, n.NodeTypeId, n.Title, n.SortOrder, false, [])).ToList();
        var numbers = VersionTree.Numbers(flat).ToDictionary(n => byId[n.Key].LogicalNodeId, n => n.Value);
        var parent = nodes.ToDictionary(n => n.LogicalNodeId, n => n.ParentNodeId is { } p && byId.TryGetValue(p, out var pn) ? pn.LogicalNodeId : (Guid?)null);
        var children = nodes.GroupBy(n => parent[n.LogicalNodeId] ?? Guid.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.SortOrder).ThenBy(n => n.Id).Select(n => n.LogicalNodeId).ToList());
        return (numbers, parent, children);
    }

    public sealed record VersionRef(int VersionId, string Label);

    public sealed record CompareSummary(int Added, int Removed, int Moved, int Renamed, int TypeChanged, int ContentChanged, int Unchanged);

    public sealed record NodeSide(string Title, string Number, string NodeType, Guid? ParentLogicalNodeId);

    public sealed record CompareNode(
        Guid LogicalNodeId, string Status, IReadOnlyList<string> Changes, NodeSide? Base, NodeSide? Target, DiffStats? ContentStats, IReadOnlyList<CompareNode> Children);

    public sealed record Comparison(VersionRef Base, VersionRef Target, CompareSummary Summary, IReadOnlyList<CompareNode> Tree);
}
