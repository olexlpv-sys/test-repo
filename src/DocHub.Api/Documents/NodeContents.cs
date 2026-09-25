using System.Text.Json;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Documents;

/// <summary>The content of a node as returned by the API (T09).</summary>
public sealed record NodeContentView(
    int NodeId, Guid LogicalNodeId, int SchemaVersion, JsonElement? ContentJson, string? ContentHtml, DateTime ModifiedAt, UserRef ModifiedBy, byte[] RowVersion);

/// <summary>Which representations a content read returns.</summary>
public enum ContentFormat
{
    Json,
    Html,
    Both,
}

/// <summary>
/// Node content (T09): schema validation against the style catalog and font list, canonicalization, derived columns
/// (HTML, plain text, hash) and the node's <c>ContentStyleUsage</c> rows.
/// </summary>
public sealed class NodeContents(DocHubDbContext db, StyleProperties styles)
{
    /// <summary>The validator/canonicalizer for the current catalog (active and inactive styles).</summary>
    public async Task<ContentDocument> SchemaAsync(CancellationToken cancellationToken) =>
        new(await db.ContentStyles.AsNoTracking().Select(s => s.StyleId).ToListAsync(cancellationToken), styles.FontFamilies);

    /// <summary><c>json</c> (default), <c>html</c> or <c>both</c>.</summary>
    public static ContentFormat? ParseFormat(string? value) => value?.ToUpperInvariant() switch
    {
        null or "JSON" => ContentFormat.Json,
        "HTML" => ContentFormat.Html,
        "BOTH" => ContentFormat.Both,
        _ => null,
    };

    /// <summary>
    /// Sets the stored content and all derived columns (so the audit trigger does not flag them stale: every derived
    /// column is written by the same statement) and rebuilds the node's style usage. The caller saves.
    /// </summary>
    public async Task ApplyAsync(NodeContent content, string canonicalJson, ContentDocument schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(schema);
        var rendered = ContentHtmlRenderer.Render(canonicalJson);
        content.ContentJson = canonicalJson;
        content.SchemaVersion = ContentSchema.Version;
        content.ContentHtml = rendered.Html;
        content.PlainText = rendered.PlainText;
        content.ContentHash = rendered.ContentHash;
        content.DerivedStale = false;
        var entry = db.Entry(content);
        entry.Property(c => c.ContentHtml).IsModified = true;
        entry.Property(c => c.PlainText).IsModified = true;
        entry.Property(c => c.ContentHash).IsModified = true;
        entry.Property(c => c.DerivedStale).IsModified = true;

        using var document = JsonDocument.Parse(canonicalJson);
        await SetStyleUsageAsync(content.NodeId, schema.UsedStyles(document.RootElement), cancellationToken);
    }

    /// <summary>Replaces the node's <c>ContentStyleUsage</c> rows with <paramref name="used"/> (tracked; the caller saves).</summary>
    public async Task SetStyleUsageAsync(int nodeId, IReadOnlySet<string> used, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(used);
        var existing = await db.ContentStyleUsages.Where(u => u.NodeId == nodeId).ToListAsync(cancellationToken);
        db.ContentStyleUsages.RemoveRange(existing.Where(u => !used.Contains(u.StyleId)));
        var kept = existing.Select(u => u.StyleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.ContentStyleUsages.AddRange(used.Where(s => !kept.Contains(s)).Select(s => new ContentStyleUsage { StyleId = s, NodeId = nodeId }));
    }

    /// <summary>The views of the given nodes' contents, in the order of <paramref name="nodeIds"/> (missing ones left out).</summary>
    public async Task<List<NodeContentView>> ViewsAsync(IReadOnlyCollection<int> nodeIds, ContentFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var withJson = format != ContentFormat.Html;
        var withHtml = format != ContentFormat.Json;
        var rows = await (
                from c in db.NodeContents.AsNoTracking()
                where nodeIds.Contains(c.NodeId)
                join u in db.Users.AsNoTracking() on c.ModifiedByUserId equals u.Id
                select new
                {
                    c.NodeId, c.LogicalNodeId, c.SchemaVersion, c.ContentJson, c.DerivedStale, c.ModifiedAt, c.RowVersion,
                    ContentHtml = withHtml ? c.ContentHtml : null,
                    ModifiedBy = new UserRef(u.Id, u.DisplayName),
                })
            .ToDictionaryAsync(r => r.NodeId, cancellationToken);

        return nodeIds.Where(rows.ContainsKey).Select(id =>
            {
                var r = rows[id];
                // Until the refresher catches up, a script-edited row renders from its ContentJson (T09 rule 8).
                var html = !withHtml ? null : r.DerivedStale ? ContentHtmlRenderer.Render(r.ContentJson).Html : r.ContentHtml;
                return new NodeContentView(r.NodeId, r.LogicalNodeId, r.SchemaVersion, withJson ? ParseStored(r.ContentJson) : null, html, r.ModifiedAt, r.ModifiedBy, r.RowVersion);
            })
            .ToList();
    }

    /// <summary>
    /// Stored JSON as an element. What a script may have stored is made returnable: lone surrogates become U+FFFD, and
    /// content nested beyond the API's depth limit reads as an empty document.
    /// </summary>
    private static JsonElement ParseStored(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(ContentSchema.RepairLoneSurrogates(json), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse(ContentSchema.EmptyDocument);
            return empty.RootElement.Clone();
        }
    }
}
