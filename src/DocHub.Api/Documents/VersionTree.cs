using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Documents;

public sealed record TreeNode(
    int Id, Guid LogicalNodeId, int NodeTypeId, string Title, string Number, bool HasContent, int SortOrder, byte[] RowVersion, IReadOnlyList<TreeNode> Children);

/// <summary>A node of a version as loaded for the tree (no content columns except the has-content flag).</summary>
public sealed record FlatNode(int Id, int? ParentNodeId, Guid LogicalNodeId, int NodeTypeId, string Title, int SortOrder, bool HasContent, byte[] RowVersion);

/// <summary>The tree of a version (ADR-02): one query for its nodes, assembled in memory with 1-based numbering ("1.2.1").</summary>
public static class VersionTree
{
    public static Task<List<FlatNode>> LoadAsync(DocHubDbContext db, int versionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DocumentNodes.AsNoTracking()
            .Where(n => n.DocumentVersionId == versionId)
            .Select(n => new FlatNode(n.Id, n.ParentNodeId, n.LogicalNodeId, n.NodeTypeId, n.Title, n.SortOrder,
                db.NodeContents.Any(c => c.NodeId == n.Id && c.PlainText != ""), n.RowVersion))
            .ToListAsync(cancellationToken);
    }

    public static List<TreeNode> Build(IReadOnlyCollection<FlatNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var children = nodes.ToLookup(n => n.ParentNodeId is { } p && ids.Contains(p) ? p : (int?)null);
        var visited = new HashSet<int>();

        List<TreeNode> Level(int? parentId, string prefix) => children[parentId]
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Id)
            .Where(n => visited.Add(n.Id))
            .Select((n, i) =>
            {
                var number = prefix.Length == 0 ? $"{i + 1}" : $"{prefix}.{i + 1}";
                return new TreeNode(n.Id, n.LogicalNodeId, n.NodeTypeId, n.Title, number, n.HasContent, n.SortOrder, n.RowVersion, Level(n.Id, number));
            })
            .ToList();

        return Level(null, "");
    }

    /// <summary>Numbers of all nodes ("1.2.1"), keyed by node id.</summary>
    public static Dictionary<int, string> Numbers(IReadOnlyCollection<FlatNode> nodes)
    {
        var numbers = new Dictionary<int, string>();
        void Walk(IEnumerable<TreeNode> level)
        {
            foreach (var node in level)
            {
                numbers[node.Id] = node.Number;
                Walk(node.Children);
            }
        }

        Walk(Build(nodes));
        return numbers;
    }
}
