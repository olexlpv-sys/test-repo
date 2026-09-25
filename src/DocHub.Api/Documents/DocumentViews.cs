using DocHub.Domain.Entities;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Documents;

public sealed record VersionHeader(
    int Id,
    int DocumentId,
    byte[] RowVersion,
    VersionStatus Status,
    int? VersionNumber,
    string Label,
    DateTime CreatedAt,
    UserRef CreatedBy,
    DateTime? SignedAt,
    IReadOnlyList<UserRef> SignedBy,
    int? BasedOnVersionId,
    bool IsCurrent,
    bool ModifiedAfterSigning);

public sealed record MyRoles(bool IsOwner, bool IsAdmin, bool IsEditor, bool IsApprover, IReadOnlyList<Guid> EditorNodeScopes);

public sealed record DocumentDetails(
    int Id,
    byte[] RowVersion,
    int FolderId,
    string Title,
    string Status,
    UserRef Owner,
    DateTime CreatedAt,
    DateTime? DeletedAt,
    IReadOnlyList<VersionHeader> Versions,
    MyRoles MyRoles);

/// <summary>Read models of documents and versions (labels, signers, the FR-H5 flag).</summary>
public sealed class DocumentViews(DocHubDbContext db)
{
    public async Task<IReadOnlyList<VersionHeader>> VersionsAsync(int documentId, int? versionId, CancellationToken cancellationToken)
    {
        var versions = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == documentId && (versionId == null || v.Id == versionId))
            .OrderBy(v => v.CreatedAt).ThenBy(v => v.Id)
            .ToListAsync(cancellationToken);
        var ids = versions.Select(v => v.Id).ToList();
        var numbers = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == documentId && v.VersionNumber != null)
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber!.Value, cancellationToken);
        var creators = await db.Users.AsNoTracking()
            .Where(u => versions.Select(v => v.CreatedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        var flagged = await db.Database
            .SqlQuery<int>($"SELECT [DocumentVersionId] AS [Value] FROM [audit].[vModifiedAfterSigning] WHERE [DocumentId] = {documentId}")
            .ToListAsync(cancellationToken);
        var signatures = await (
                from s in db.VersionSignatures.AsNoTracking()
                where ids.Contains(s.DocumentVersionId) && s.WithdrawnAt == null
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                select new { s.DocumentVersionId, s.ContentHash, s.SignedAt, User = new UserRef(u.Id, u.DisplayName) })
            .ToListAsync(cancellationToken);

        return versions.Select(v => new VersionHeader(
                v.Id, v.DocumentId, v.RowVersion, v.Status, v.VersionNumber, Label(v, numbers), v.CreatedAt,
                creators.GetValueOrDefault(v.CreatedByUserId) ?? new UserRef(v.CreatedByUserId, "?"), v.SignedAt,
                v.Status == VersionStatus.Signed && v.SignedContentHash is { } signed
                    ? signatures.Where(s => s.DocumentVersionId == v.Id && s.ContentHash.AsSpan().SequenceEqual(signed)).OrderBy(s => s.SignedAt).Select(s => s.User).ToList()
                    : [],
                v.BasedOnVersionId, v.IsCurrent, v.Status == VersionStatus.Signed && flagged.Contains(v.Id)))
            .ToList();
    }

    public async Task<DocumentDetails> DetailsAsync(int documentId, int userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, cancellationToken);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == document.OwnerUserId).Select(u => new UserRef(u.Id, u.DisplayName)).SingleAsync(cancellationToken);
        var versions = await VersionsAsync(documentId, null, cancellationToken);
        var grants = await db.DocumentPermissions.AsNoTracking().Where(p => p.DocumentId == documentId && p.UserId == userId).ToListAsync(cancellationToken);
        var roles = new MyRoles(
            document.OwnerUserId == userId,
            isAdmin,
            grants.Any(g => g.Role == DocumentRole.Editor),
            grants.Any(g => g.Role == DocumentRole.Approver),
            grants.Where(g => g.Role == DocumentRole.Editor && g.LogicalNodeId != null).Select(g => g.LogicalNodeId!.Value).ToList());
        return new DocumentDetails(document.Id, document.RowVersion, document.FolderId, document.Title, Status(document, versions), owner,
            document.CreatedAt, document.DeletedAt, versions, roles);
    }

    /// <summary>Derived document status: Deleted if deleted, else Draft if a draft exists, else Signed.</summary>
    public static string Status(Document document, IReadOnlyCollection<VersionHeader> versions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(versions);
        return document.IsDeleted ? "Deleted" : versions.Any(v => v.Status == VersionStatus.Draft) ? "Draft" : "Signed";
    }

    /// <summary><c>v{n}</c> for signed, <c>Draft</c> / <c>Draft (based on v{n})</c> for drafts, <c>Discarded draft</c> for deleted.</summary>
    public static string Label(DocumentVersion version, IReadOnlyDictionary<int, int> versionNumbers)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(versionNumbers);
        return version.Status switch
        {
            VersionStatus.Signed => $"v{version.VersionNumber}",
            VersionStatus.Deleted => "Discarded draft",
            _ => version.BasedOnVersionId is { } basedOn && versionNumbers.TryGetValue(basedOn, out var n) ? $"Draft (based on v{n})" : "Draft",
        };
    }
}
