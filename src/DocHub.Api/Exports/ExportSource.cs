using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocHub.Api.Documents;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Export;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Exports;

/// <summary>What an export depends on, loaded cheaply at request time (T20 "Storage").</summary>
public sealed record ExportKey(int DocumentId, int VersionId, VersionStatus Status, string CacheKey, string FileName);

/// <summary>
/// Loads what a PDF export needs: the cache key at request time (<see cref="KeyAsync"/>) and the whole print document in the worker
/// (<see cref="LoadAsync"/>). The cache key hashes the version stamp, the document row version (rename, move, ownership), the folder
/// and its path, the style catalog version, the subtree and the options — equal keys mean identical files.
/// </summary>
public sealed class ExportSource(DocHubDbContext db, StyleProperties styles, TimeProvider clock)
{
    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Serialize(PdfExportOptions options) => JsonSerializer.Serialize(options, OptionsJson);

    public static PdfExportOptions Deserialize(string json) => JsonSerializer.Deserialize<PdfExportOptions>(json, OptionsJson) ?? new PdfExportOptions();

    public async Task<ExportKey> KeyAsync(int versionId, Guid? logicalNodeId, PdfExportOptions options, CancellationToken cancellationToken)
    {
        var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw DomainException.NotFound("Version", versionId);
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == version.DocumentId, cancellationToken);
        string? subtreeTitle = null;
        if (logicalNodeId is { } node)
        {
            subtreeTitle = await db.DocumentNodes.AsNoTracking().Where(n => n.DocumentVersionId == versionId && n.LogicalNodeId == node)
                .Select(n => n.Title).SingleOrDefaultAsync(cancellationToken) ?? throw DomainException.NotFound("Node of this version", node);
        }

        var stamp = await db.VersionStamps.AsNoTracking().Where(s => s.DocumentVersionId == versionId).Select(s => (long?)s.LastChangeLogId)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var catalog = await db.ContentStyles.AsNoTracking().Select(s => s.RowVersion).ToListAsync(cancellationToken);
        var catalogVersion = catalog.Select(Convert.ToHexString).DefaultIfEmpty("").Max(StringComparer.Ordinal);
        var folderPath = await FolderPathAsync(document.FolderId, cancellationToken);
        var label = await LabelAsync(version, cancellationToken);

        var key = string.Join('\n',
            "pdf-v1",
            stamp.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(document.RowVersion),
            document.FolderId.ToString(CultureInfo.InvariantCulture),
            folderPath,
            catalogVersion,
            string.Join(',', styles.FontFamilies.Order(StringComparer.Ordinal)),
            logicalNodeId?.ToString("N") ?? "",
            Serialize(options));
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var name = subtreeTitle is null ? $"{document.Title} - {label}" : $"{document.Title} - {subtreeTitle} - {label}";
        return new ExportKey(document.Id, version.Id, version.Status, cacheKey, FileName(name));
    }

    public async Task<PrintDocument> LoadAsync(int versionId, Guid? logicalNodeId, CancellationToken cancellationToken)
    {
        var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw DomainException.NotFound("Version", versionId);
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == version.DocumentId, cancellationToken);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == document.OwnerUserId).Select(u => u.DisplayName).SingleAsync(cancellationToken);
        var flat = await VersionTree.LoadAsync(db, versionId, cancellationToken);
        var contents = await (
                from c in db.NodeContents.AsNoTracking()
                join n in db.DocumentNodes.AsNoTracking() on c.NodeId equals n.Id
                where n.DocumentVersionId == versionId
                select new { c.NodeId, c.ContentJson })
            .ToDictionaryAsync(c => c.NodeId, c => c.ContentJson, cancellationToken);

        var sections = new List<PrintSection>();
        void Walk(IEnumerable<TreeNode> level, int depth, bool included)
        {
            foreach (var node in level)
            {
                var take = included || node.LogicalNodeId == logicalNodeId;
                if (take)
                {
                    // Rendered from the JSON (not the stored HTML), so script-edited content is never stale.
                    var html = contents.TryGetValue(node.Id, out var json) ? ContentHtmlRenderer.Render(json).Html : "";
                    sections.Add(new PrintSection(node.Number, depth, node.Title, html));
                }

                Walk(node.Children, depth + 1, take);
            }
        }

        Walk(VersionTree.Build(flat), 1, logicalNodeId is null);
        if (logicalNodeId is { } node && sections.Count == 0)
        {
            throw DomainException.NotFound("Node of this version", node);
        }

        var signatures = version.Status == VersionStatus.Signed && version.SignedContentHash is { } signed
            ? (await (
                    from s in db.VersionSignatures.AsNoTracking()
                    where s.DocumentVersionId == versionId && s.WithdrawnAt == null
                    join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                    select new { s.ContentHash, s.SignedAt, u.DisplayName, s.Comment })
                .ToListAsync(cancellationToken))
                .Where(s => s.ContentHash.AsSpan().SequenceEqual(signed))
                .OrderBy(s => s.SignedAt)
                .Select(s => new PrintSignature(s.DisplayName, s.SignedAt, s.Comment))
                .ToList()
            : [];
        var tampered = version.Status == VersionStatus.Signed && await db.Database
            .SqlQuery<int>($"SELECT [DocumentVersionId] AS [Value] FROM [audit].[vModifiedAfterSigning] WHERE [DocumentVersionId] = {versionId}")
            .AnyAsync(cancellationToken);
        var catalog = await db.ContentStyles.AsNoTracking().ToListAsync(cancellationToken);

        return new PrintDocument(
            document.Title,
            await FolderPathAsync(document.FolderId, cancellationToken),
            await LabelAsync(version, cancellationToken),
            owner,
            clock.GetUtcNow().UtcDateTime,
            version.Status != VersionStatus.Signed,
            tampered,
            version.Status == VersionStatus.Signed ? version.SignedAt : null,
            sections,
            signatures,
            StyleProperties.ToCss(catalog));
    }

    /// <summary>A file name without characters that file systems or the Content-Disposition header reject.</summary>
    public static string FileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '<', '>', '|', ':', '*', '?', '\\', '/']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 200)
        {
            clean = clean[..200].TrimEnd();
        }

        return (clean.Length == 0 ? "export" : clean) + ".pdf";
    }

    private async Task<string> LabelAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        var numbers = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == version.DocumentId && v.VersionNumber != null)
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber!.Value, cancellationToken);
        return DocumentViews.Label(version, numbers);
    }

    private async Task<string> FolderPathAsync(int folderId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var seen = new HashSet<int>();
        for (int? current = folderId; current is { } id && seen.Add(id);)
        {
            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == id).Select(f => new { f.Name, f.ParentFolderId }).SingleOrDefaultAsync(cancellationToken);
            if (folder is null)
            {
                break;
            }

            names.Insert(0, folder.Name);
            current = folder.ParentFolderId;
        }

        return string.Join(" / ", names);
    }
}
