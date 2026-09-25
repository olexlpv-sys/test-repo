using System.Globalization;
using DocHub.Api.Documents;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content.Diff;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.History;

/// <summary>
/// The state changes are counted from (T11 <c>since</c>): a version (<c>latestSigned</c>, <c>v:{id}</c>) or a point in the
/// log (<c>e:{entryId}</c>, <c>d:{isoDate}</c>). <see cref="Point"/> is the last ChangeLog id of the baseline; rows after it in
/// <see cref="Versions"/> (the target and its ancestors after the baseline) are the changes. For a point in the log,
/// <see cref="StateVersionId"/> is the version whose state as of the point is reconstructed from the log.
/// </summary>
public sealed record Baseline(string Key, long Point, int? VersionId, int? StateVersionId, IReadOnlyList<int> Versions);

/// <summary>A node of a tree snapshot.</summary>
public sealed record SnapshotNode(int Id, int? ParentId, Guid LogicalNodeId, string Title, int NodeTypeId, int SortOrder);

public sealed record StructuralChange(string Kind, string? OldNumber, string? NewNumber, string? OldTitle, string? NewTitle, int? OldNodeTypeId, int? NewNodeTypeId);

public sealed record NodeChangeSummary(
    Guid LogicalNodeId, int ChangeCount, DateTime? LastChangedAt, UserRef? LastChangedBy, bool HasScriptChange, bool HasChangeAfterSigning, IReadOnlyList<StructuralChange> Structural);

public sealed record RemovedNode(Guid LogicalNodeId, string Title, string Number, int NodeTypeId, Guid? FormerParentLogicalNodeId, int FormerPosition, long? LastEntryId);

public sealed record ChangeSummary(string Since, IReadOnlyList<NodeChangeSummary> Nodes, IReadOnlyList<RemovedNode> Removed);

/// <summary>Change summaries for section badges and attributed diffs for track changes (T11, FR-H4).</summary>
public sealed class ChangeTracking(DocHubDbContext db, ChangeHistory history, IContentDiffService diff)
{
    /// <summary>Resolves <paramref name="since"/> for a target version (400 for an unknown form, 404 for a foreign version/entry).</summary>
    public async Task<Baseline> BaselineAsync(DocumentVersion target, string? since, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == target.DocumentId).ToListAsync(ct);
        var byId = versions.ToDictionary(v => v.Id);
        var chain = new List<DocumentVersion>();
        for (DocumentVersion? v = target; v is not null && chain.Count < versions.Count; v = v.BasedOnVersionId is { } b ? byId.GetValueOrDefault(b) : null)
        {
            chain.Add(v);
        }

        since ??= "latestSigned";
        DocumentVersion? baselineVersion = null;
        if (since == "latestSigned")
        {
            baselineVersion = versions.Where(v => v.Status == VersionStatus.Signed && v.Id != target.Id && (target.VersionNumber is not { } n || v.VersionNumber < n))
                .OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            return await VersionBaselineAsync(since, baselineVersion, chain, ct);
        }

        if (since.StartsWith("v:", StringComparison.Ordinal) && int.TryParse(since.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var versionId))
        {
            baselineVersion = byId.GetValueOrDefault(versionId) ?? throw DomainException.NotFound("Version of this document", versionId);
            return await VersionBaselineAsync(since, baselineVersion, chain, ct);
        }

        long point;
        if (since.StartsWith("e:", StringComparison.Ordinal) && long.TryParse(since.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var entryId))
        {
            var row = await history.RowAsync(entryId, ct);
            if (row?.DocumentId != target.DocumentId)
            {
                throw DomainException.NotFound("History entry of this document", entryId);
            }

            point = entryId;
        }
        else if (since.StartsWith("d:", StringComparison.Ordinal)
                 && DateTime.TryParse(since.AsSpan(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            point = (await db.Database.SqlQueryRaw<long>(
                    "SELECT COALESCE(MAX([Id]), 0) AS [Value] FROM [audit].[ChangeLog] WHERE [DocumentId] = @d AND [ChangedAt] <= @at",
                    ChangeHistory.Parameter("@d", target.DocumentId), ChangeHistory.Parameter("@at", date)).ToListAsync(ct))[0];
        }
        else
        {
            throw DomainException.Validation("since must be latestSigned, v:{versionId}, e:{entryId} or d:{isoDate}.");
        }

        // The chain version that existed at the point (the latest one created at or before it) is the baseline state.
        var created = await CreatedRowsAsync(target.DocumentId, ct);
        var state = chain.Where(v => created.TryGetValue(v.Id, out var id) && id <= point).Select(v => (int?)v.Id).FirstOrDefault();
        var included = state is null ? chain : chain.TakeWhile(v => v.Id != state).Append(byId[state.Value]).ToList();
        return new Baseline(since, point, state, state, included.Select(v => v.Id).ToList());
    }

    private async Task<Baseline> VersionBaselineAsync(string since, DocumentVersion? baseline, List<DocumentVersion> chain, CancellationToken ct)
    {
        if (baseline is null)
        {
            return new Baseline(since, 0, null, null, chain.Select(v => v.Id).ToList());
        }

        // Changes after the baseline was finalized (its signing), else after its last change.
        var point = (await db.Database.SqlQueryRaw<long>(
                """
                SELECT COALESCE(MAX(CASE WHEN [TableName] = N'app.DocumentVersion' AND [EntityId] = @v AND JSON_VALUE([NewValues], N'$.Status') = N'2' THEN [Id] END),
                                MAX([Id]), 0) AS [Value]
                FROM [audit].[ChangeLog] WHERE [DocumentId] = @d AND ([DocumentVersionId] = @v OR ([TableName] = N'app.DocumentVersion' AND [EntityId] = @v))
                """,
                ChangeHistory.Parameter("@v", baseline.Id), ChangeHistory.Parameter("@d", baseline.DocumentId)).ToListAsync(ct))[0];
        var index = chain.FindIndex(v => v.Id == baseline.Id);
        var after = index < 0 ? chain.Take(1) : chain.Take(index);
        return new Baseline(since, point, baseline.Id, null, after.Select(v => v.Id).ToList());
    }

    private async Task<Dictionary<int, long>> CreatedRowsAsync(int documentId, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<CreatedRow>(
                "SELECT [EntityId], MIN([Id]) AS [Id] FROM [audit].[ChangeLog] WHERE [DocumentId] = @d AND [TableName] = N'app.DocumentVersion' AND [Operation] = 'I' GROUP BY [EntityId]",
                ChangeHistory.Parameter("@d", documentId))
            .ToListAsync(ct))
        .ToDictionary(r => r.EntityId, r => r.Id);

    private sealed class CreatedRow
    {
        public int EntityId { get; set; }

        public long Id { get; set; }
    }

    /// <summary>The tree of the baseline: a version's current nodes, or a version's nodes as of a log point.</summary>
    public async Task<List<SnapshotNode>> BaselineTreeAsync(Baseline baseline, CancellationToken ct)
    {
        if (baseline.StateVersionId is { } state)
        {
            var rows = await db.Database.SqlQueryRaw<ChangeLogRow>(
                    """
                    SELECT [Id], [ChangedAt], [TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],
                           [ChangedColumns], [UserId], [Source], [DbLogin], [CorrelationId], [OperationContext], [Ticket], [Reason]
                    FROM [audit].[ChangeLog]
                    WHERE [TableName] = N'app.DocumentNode' AND [Id] <= @p
                      AND ([DocumentVersionId] = @v OR TRY_CONVERT(INT, JSON_VALUE([OldValues], N'$.DocumentVersionId')) = @v)
                    """,
                    ChangeHistory.Parameter("@p", baseline.Point), ChangeHistory.Parameter("@v", state))
                .ToListAsync(ct);
            return rows.GroupBy(r => r.EntityId)
                .Select(g => g.MaxBy(r => r.Id)!)
                .Where(r => r.Operation != "D" && r.DocumentVersionId == state)
                .Select(r => new SnapshotNode(r.EntityId, int.TryParse(r.New("ParentNodeId"), out var p) ? p : null, r.LogicalNodeId!.Value, r.New("Title") ?? "",
                    int.TryParse(r.New("NodeTypeId"), out var t) ? t : 0, int.TryParse(r.New("SortOrder"), out var s) ? s : 0))
                .ToList();
        }

        return baseline.VersionId is { } version ? await TreeAsync(version, ct) : [];
    }

    public Task<List<SnapshotNode>> TreeAsync(int versionId, CancellationToken ct) =>
        db.DocumentNodes.AsNoTracking().Where(n => n.DocumentVersionId == versionId)
            .Select(n => new SnapshotNode(n.Id, n.ParentNodeId, n.LogicalNodeId, n.Title, n.NodeTypeId, n.SortOrder))
            .ToListAsync(ct);

    public async Task<ChangeSummary> SummaryAsync(DocumentVersion target, Baseline baseline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(baseline);
        var before = await BaselineTreeAsync(baseline, ct);
        var after = await TreeAsync(target.Id, ct);
        var (oldNumbers, oldPlaces) = Layout(before);
        var (newNumbers, newPlaces) = Layout(after);
        var oldByLogical = before.GroupBy(n => n.LogicalNodeId).ToDictionary(g => g.Key, g => g.First());
        var newLogical = after.Select(n => n.LogicalNodeId).ToHashSet();
        var common = oldByLogical.Keys.Where(newLogical.Contains).ToHashSet();

        // Changes after the baseline in the target (and its ancestors after the baseline), grouped into entries.
        var rows = await ChangeRowsAsync(target.DocumentId, baseline, null, null, ct);
        var entries = await history.EntriesAsync(rows, target.DocumentId, ct);
        var byNode = entries.Where(e => e.LogicalNodeId is not null && e.Kind != "CopiedToNewDraft").ToLookup(e => e.LogicalNodeId!.Value);

        var nodes = after.OrderBy(n => newNumbers.GetValueOrDefault(n.Id, "~"), NumberComparer.Instance).Select(n =>
        {
            var structural = new List<StructuralChange>();
            if (!oldByLogical.TryGetValue(n.LogicalNodeId, out var old))
            {
                structural.Add(new StructuralChange("Added", null, newNumbers.GetValueOrDefault(n.Id), null, n.Title, null, n.NodeTypeId));
            }
            else
            {
                var (oldNumber, newNumber) = (oldNumbers.GetValueOrDefault(old.Id), newNumbers.GetValueOrDefault(n.Id));
                if (Moved(n.LogicalNodeId, oldPlaces[old.Id], newPlaces[n.Id], common, before, after))
                {
                    structural.Add(new StructuralChange("Moved", oldNumber, newNumber, null, null, null, null));
                }

                if (old.Title != n.Title)
                {
                    structural.Add(new StructuralChange("Renamed", oldNumber, newNumber, old.Title, n.Title, null, null));
                }

                if (old.NodeTypeId != n.NodeTypeId)
                {
                    structural.Add(new StructuralChange("TypeChanged", oldNumber, newNumber, null, null, old.NodeTypeId, n.NodeTypeId));
                }
            }

            var changes = byNode[n.LogicalNodeId].ToList();
            var last = changes.MaxBy(e => e.Id);
            return new NodeChangeSummary(n.LogicalNodeId, changes.Count, last?.ChangedAt, last?.User, changes.Any(e => e.Source == "Script"), changes.Any(e => e.AfterSigning), structural);
        }).ToList();

        var removedLogical = before.Where(n => !newLogical.Contains(n.LogicalNodeId)).ToList();
        var lastEntries = await LastEntriesAsync(target.DocumentId, removedLogical.Select(n => n.LogicalNodeId).ToList(), ct);
        var removed = removedLogical
            .OrderBy(n => oldNumbers.GetValueOrDefault(n.Id, "~"), NumberComparer.Instance)
            .Select(n => new RemovedNode(n.LogicalNodeId, n.Title, oldNumbers.GetValueOrDefault(n.Id, ""), n.NodeTypeId,
                n.ParentId is { } p && before.FirstOrDefault(x => x.Id == p) is { } parent ? parent.LogicalNodeId : null, oldPlaces[n.Id].Index,
                lastEntries.TryGetValue(n.LogicalNodeId, out var entry) ? entry : null))
            .ToList();
        return new ChangeSummary(baseline.Key, nodes, removed);
    }

    /// <summary>The attributed diff of a node from the baseline to now (or to an entry).</summary>
    public async Task<ContentDiff> ChangesAsync(DocumentVersion target, Guid logicalNodeId, Baseline baseline, long? until, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(baseline);
        var baselineJson = await BaselineContentAsync(baseline, logicalNodeId, ct);
        var rows = (await ChangeRowsAsync(target.DocumentId, baseline, logicalNodeId, until, ct))
            .Where(r => r.TableName == "app.NodeContent" && r.Operation != "D" && (r.Operation == "I" || r.Columns.Contains("ContentJson")))
            .OrderBy(r => r.Id)
            .ToList();
        var userIds = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var steps = rows.Select(r => new ContentStep(r.New("ContentJson") ?? ContentSchema.EmptyDocument,
                new DiffAuthor(r.Id, r.UserId, r.UserId is { } u ? names.GetValueOrDefault(u) : null, r.Source, r.Ticket, r.ChangedAt)))
            .ToList();
        return AttributedDiff.Diff(diff, baselineJson, steps);
    }

    private async Task<string> BaselineContentAsync(Baseline baseline, Guid logicalNodeId, CancellationToken ct)
    {
        if (baseline.StateVersionId is { } state)
        {
            var row = await history.LatestAsync("app.NodeContent", logicalNodeId, state, baseline.Point, ct);
            return row is null || row.Operation == "D" ? ContentSchema.EmptyDocument : row.New("ContentJson") ?? ContentSchema.EmptyDocument;
        }

        if (baseline.VersionId is not { } version)
        {
            return ContentSchema.EmptyDocument;
        }

        return await (from n in db.DocumentNodes.AsNoTracking()
                      where n.DocumentVersionId == version && n.LogicalNodeId == logicalNodeId
                      join c in db.NodeContents.AsNoTracking() on n.Id equals c.NodeId
                      select c.ContentJson).FirstOrDefaultAsync(ct) ?? ContentSchema.EmptyDocument;
    }

    private async Task<List<ChangeLogRow>> ChangeRowsAsync(int documentId, Baseline baseline, Guid? logicalNodeId, long? until, CancellationToken ct)
    {
        if (baseline.Versions.Count == 0)
        {
            return [];
        }

        var versions = System.Text.Json.JsonSerializer.Serialize(baseline.Versions);
        return await db.Database.SqlQueryRaw<ChangeLogRow>(
                """
                SELECT [Id], [ChangedAt], [TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],
                       [ChangedColumns], [UserId], [Source], [DbLogin], [CorrelationId], [OperationContext], [Ticket], [Reason]
                FROM [audit].[ChangeLog]
                WHERE [DocumentId] = @d AND [Id] > @p AND (@until IS NULL OR [Id] <= @until) AND [LogicalNodeId] IS NOT NULL
                  AND (@l IS NULL OR [LogicalNodeId] = @l)
                  AND [TableName] IN (N'app.DocumentNode', N'app.NodeContent')
                  AND ([DocumentVersionId] IN (SELECT CAST([value] AS INT) FROM OPENJSON(@versions))
                       OR TRY_CONVERT(INT, JSON_VALUE([OldValues], N'$.DocumentVersionId')) IN (SELECT CAST([value] AS INT) FROM OPENJSON(@versions)))
                """,
                ChangeHistory.Parameter("@d", documentId), ChangeHistory.Parameter("@p", baseline.Point), ChangeHistory.Parameter("@until", until),
                ChangeHistory.Parameter("@l", logicalNodeId), ChangeHistory.Parameter("@versions", versions))
            .ToListAsync(ct);
    }

    /// <summary>The newest row with the node's content per removed node (for <c>entries/{id}/content</c> of a placeholder).</summary>
    private async Task<Dictionary<Guid, long>> LastEntriesAsync(int documentId, List<Guid> logicalNodeIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, long>();
        foreach (var logical in logicalNodeIds)
        {
            var id = (await db.Database.SqlQueryRaw<long>(
                    "SELECT COALESCE(MAX([Id]), 0) AS [Value] FROM [audit].[ChangeLog] WHERE [LogicalNodeId] = @l AND [DocumentId] = @d AND [TableName] = N'app.NodeContent'",
                    ChangeHistory.Parameter("@l", logical), ChangeHistory.Parameter("@d", documentId)).ToListAsync(ct))[0];
            if (id > 0)
            {
                result[logical] = id;
            }
        }

        return result;
    }

    /// <summary>Numbers ("1.2") and places (parent, 0-based index among siblings) of a tree.</summary>
    private static (Dictionary<int, string> Numbers, Dictionary<int, (Guid? Parent, int Index)> Places) Layout(List<SnapshotNode> nodes)
    {
        var flat = nodes.Select(n => new FlatNode(n.Id, n.ParentId, n.LogicalNodeId, n.NodeTypeId, n.Title, n.SortOrder, false, [])).ToList();
        var numbers = VersionTree.Numbers(flat);
        var byId = nodes.ToDictionary(n => n.Id);
        var places = new Dictionary<int, (Guid?, int)>();
        foreach (var siblings in nodes.GroupBy(n => n.ParentId is { } p && byId.ContainsKey(p) ? p : (int?)null))
        {
            var index = 0;
            foreach (var n in siblings.OrderBy(s => s.SortOrder).ThenBy(s => s.Id))
            {
                places[n.Id] = (siblings.Key is { } p ? byId[p].LogicalNodeId : null, index++);
            }
        }

        foreach (var n in nodes.Where(n => !places.ContainsKey(n.Id)))
        {
            places[n.Id] = (null, 0);
        }

        return (numbers, places);
    }

    /// <summary>
    /// Moved = another parent, or out of order among the siblings present in both trees: the siblings in the longest common
    /// subsequence of both orders stayed, the others moved (swapping two siblings moves one, inserting a sibling moves none).
    /// </summary>
    private static bool Moved(Guid logicalNodeId, (Guid? Parent, int Index) old, (Guid? Parent, int Index) neu, HashSet<Guid> common, List<SnapshotNode> before, List<SnapshotNode> after)
    {
        if (old.Parent != neu.Parent)
        {
            return true;
        }

        static List<Guid> Order(List<SnapshotNode> tree, Guid? parent, HashSet<Guid> common)
        {
            var byId = tree.ToDictionary(n => n.Id);
            return tree.Where(n => (n.ParentId is { } p && byId.TryGetValue(p, out var pn) ? pn.LogicalNodeId : (Guid?)null) == parent && common.Contains(n.LogicalNodeId))
                .OrderBy(n => n.SortOrder).ThenBy(n => n.Id).Select(n => n.LogicalNodeId).ToList();
        }

        var (a, b) = (Order(before, old.Parent, common), Order(after, neu.Parent, common));
        a = a.Where(b.Contains).ToList();
        b = b.Where(a.Contains).ToList();
        var stayed = Domain.Ordering.SiblingOrder.Stayed(a, b);
        return !stayed.Contains(logicalNodeId);
    }

    /// <summary>Orders "1.2.10" after "1.2.9".</summary>
    private sealed class NumberComparer : IComparer<string>
    {
        public static readonly NumberComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (a, b) = ((x ?? "").Split('.'), (y ?? "").Split('.'));
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = int.TryParse(a[i], out var ai) && int.TryParse(b[i], out var bi) ? ai.CompareTo(bi) : string.CompareOrdinal(a[i], b[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
