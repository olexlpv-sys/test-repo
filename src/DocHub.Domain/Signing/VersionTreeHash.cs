using System.Security.Cryptography;
using System.Text;
using DocHub.Domain.Content;

namespace DocHub.Domain.Signing;

/// <summary>One node of a version for the tree hash.</summary>
public sealed record TreeHashNode(int Id, int? ParentId, Guid LogicalNodeId, int NodeTypeId, string Title, int SortOrder, string? ContentJson);

/// <summary>
/// The content hash a signature is bound to (FR-V6, T07 rule 2): nodes depth-first by SortOrder (then id), each as
/// <c>[LogicalNodeId, ParentLogicalNodeId, NodeTypeId, Title, SHA-256(canonical ContentJson)]</c> (a JSON array), one per line, SHA-256 over
/// UTF-8. The content part is recomputed from ContentJson, never taken from the stored ContentHash, so script edits are
/// detected; NodeTypeId (not the editable code) keeps dictionary edits from outdating signatures.
/// </summary>
public static class VersionTreeHash
{
    public static byte[] Compute(IReadOnlyCollection<TreeHashNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var byId = nodes.ToDictionary(n => n.Id);
        var children = nodes.ToLookup(n => n.ParentId is { } p && byId.ContainsKey(p) ? p : (int?)null);
        var text = new StringBuilder();
        var visited = new HashSet<int>();

        // Iterative depth-first walk (a deep tree must not exhaust the stack). Nodes a walk from the roots can't reach
        // (only a parent cycle made by a script) are walked afterwards in id order, so every node counts.
        var starts = children[null].OrderBy(n => n.SortOrder).ThenBy(n => n.Id)
            .Concat(nodes.OrderBy(n => n.Id))
            .ToList();
        foreach (var start in starts)
        {
            if (visited.Contains(start.Id))
            {
                continue;
            }

            var stack = new Stack<TreeHashNode>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!visited.Add(node.Id))
                {
                    continue;
                }

                var parent = node.ParentId is { } p && byId.TryGetValue(p, out var parentNode) ? parentNode.LogicalNodeId.ToString("D") : "";
                var content = node.ContentJson is null ? "" : Convert.ToHexString(CanonicalJson.Hash(node.ContentJson));
                // A JSON array per line: titles may contain any character, so fields are encoded, not just joined.
                text.Append(System.Text.Json.JsonSerializer.Serialize(new object[] { node.LogicalNodeId.ToString("D"), parent, node.NodeTypeId, node.Title, content }))
                    .Append('\n');
                foreach (var child in children[node.Id].OrderByDescending(n => n.SortOrder).ThenByDescending(n => n.Id))
                {
                    stack.Push(child);
                }
            }
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }
}
