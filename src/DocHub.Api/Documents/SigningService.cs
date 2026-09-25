using DocHub.Api.Auth;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Domain.Signing;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Documents;

public sealed record UserRef(int Id, string DisplayName);

public sealed record SignatureInfo(UserRef User, DateTime SignedAt, bool IsValid, string? Comment);

public sealed record SignatureStatus(IReadOnlyList<UserRef> RequiredApprovers, IReadOnlyList<SignatureInfo> Signatures, IReadOnlyList<UserRef> PendingApprovers, bool IsComplete);

/// <summary>
/// Signing (FR-V6, T07 rule 2): signatures are bound to the canonical content hash of the draft, all current approvers must
/// hold a valid signature, and finalization assigns the next version number. Lifecycle changes of one document are
/// serialized with an application lock, so concurrent last signatures assign exactly one number.
/// </summary>
public sealed class SigningService(DocHubDbContext db, ICurrentUser user, TimeProvider time)
{
    public static string LockResource(int documentId) => $"document:{documentId}";

    /// <summary>
    /// The version's current content hash, recomputed from the tree and ContentJson (never from stored hashes), and cached
    /// for the document list under the version's content stamp.
    /// </summary>
    public async Task<byte[]> ComputeHashAsync(int versionId, CancellationToken cancellationToken)
    {
        // The stamp first: a change after it makes the cache entry stale, never wrong.
        var stamp = await db.VersionStamps.AsNoTracking().Where(s => s.DocumentVersionId == versionId).Select(s => s.ContentChangeLogId).SingleOrDefaultAsync(cancellationToken) ?? 0;
        var nodes = await (
                from n in db.DocumentNodes.AsNoTracking()
                where n.DocumentVersionId == versionId
                join c in db.NodeContents.AsNoTracking() on n.Id equals c.NodeId into contents
                from c in contents.DefaultIfEmpty()
                select new TreeHashNode(n.Id, n.ParentNodeId, n.LogicalNodeId, n.NodeTypeId, n.Title, n.SortOrder, c == null ? null : c.ContentJson))
            .ToListAsync(cancellationToken);
        var hash = VersionTreeHash.Compute(nodes);

        var cached = await db.VersionContentHashes.SingleOrDefaultAsync(h => h.DocumentVersionId == versionId, cancellationToken);
        if (cached is null)
        {
            cached = new VersionContentHash { DocumentVersionId = versionId, ContentChangeLogId = stamp, ContentHash = hash };
            db.VersionContentHashes.Add(cached);
        }
        else if (cached.ContentChangeLogId != stamp || !cached.ContentHash.AsSpan().SequenceEqual(hash))
        {
            cached.ContentChangeLogId = stamp;
            cached.ContentHash = hash;
            cached.ComputedAt = time.GetUtcNow().UtcDateTime;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (db.Database.CurrentTransaction is null)
        {
            // A concurrent reader wrote the cache entry first; the cache is best-effort.
            db.Entry(cached).State = EntityState.Detached;
        }

        return hash;
    }

    public async Task<SignatureStatus> StatusAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        var approvers = await ApproversAsync(version.DocumentId, cancellationToken);
        var signatures = await (
                from s in db.VersionSignatures.AsNoTracking()
                where s.DocumentVersionId == version.Id && s.WithdrawnAt == null
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                orderby s.SignedAt
                select new { s.UserId, u.DisplayName, s.SignedAt, s.ContentHash, s.Comment })
            .ToListAsync(cancellationToken);

        // A Signed version is judged against its signed hash; a Draft against its current content.
        var reference = version.Status == VersionStatus.Signed && version.SignedContentHash is { } signed
            ? signed
            : version.Status == VersionStatus.Draft ? await ComputeHashAsync(version.Id, cancellationToken) : null;
        var approverIds = approvers.Select(a => a.Id).ToHashSet();
        var infos = signatures
            .Select(s => new SignatureInfo(new UserRef(s.UserId, s.DisplayName), s.SignedAt,
                reference is not null && s.ContentHash.AsSpan().SequenceEqual(reference) && (version.Status == VersionStatus.Signed || approverIds.Contains(s.UserId)), s.Comment))
            .ToList();
        var valid = infos.Where(i => i.IsValid).Select(i => i.User.Id).ToHashSet();
        var pending = version.Status == VersionStatus.Draft ? approvers.Where(a => !valid.Contains(a.Id)).ToList() : [];
        var complete = version.Status == VersionStatus.Signed || (approvers.Count > 0 && pending.Count == 0 && version.Status == VersionStatus.Draft);
        return new SignatureStatus(approvers, infos, pending, complete);
    }

    /// <summary>Records my signature on the draft (replacing an earlier one) and finalizes when every approver has signed.</summary>
    public Task SignAsync(int versionId, string? comment, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var documentId = await DocumentOfAsync(versionId, cancellationToken);
            await db.LockAsync(LockResource(documentId), cancellationToken);
            var version = await EditableDraftAsync(versionId, cancellationToken);
            if (!await db.DocumentNodes.AnyAsync(n => n.DocumentVersionId == versionId, cancellationToken))
            {
                throw DomainException.Validation("An empty document can't be signed; add content first.");
            }

            if ((await ApproversAsync(version.DocumentId, cancellationToken)).Count == 0)
            {
                throw DomainException.Conflict(ErrorCodes.NoApprovers, "The document has no approvers, so it can't be signed.");
            }

            var hash = await ComputeHashAsync(versionId, cancellationToken);
            var now = time.GetUtcNow().UtcDateTime;
            var mine = await db.VersionSignatures.Where(s => s.DocumentVersionId == versionId && s.UserId == user.UserId && s.WithdrawnAt == null).ToListAsync(cancellationToken);
            foreach (var old in mine)
            {
                old.WithdrawnAt = now;
            }

            await db.SaveChangesAsync(cancellationToken); // the unique active-signature index needs the old one withdrawn first
            db.VersionSignatures.Add(new VersionSignature { DocumentVersionId = versionId, UserId = user.UserId, SignedAt = now, ContentHash = hash, Comment = comment });
            await db.SaveChangesAsync(cancellationToken);
            await FinalizeIfCompleteAsync(version, hash, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Withdraws my signature from the draft.</summary>
    public Task WithdrawAsync(int versionId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var documentId = await DocumentOfAsync(versionId, cancellationToken);
            await db.LockAsync(LockResource(documentId), cancellationToken);
            await EditableDraftAsync(versionId, cancellationToken);
            var mine = await db.VersionSignatures.Where(s => s.DocumentVersionId == versionId && s.UserId == user.UserId && s.WithdrawnAt == null).ToListAsync(cancellationToken);
            if (mine.Count == 0)
            {
                throw DomainException.NotFound("Signature of the current user on version", versionId);
            }

            foreach (var signature in mine)
            {
                signature.WithdrawnAt = time.GetUtcNow().UtcDateTime;
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Re-checks finalization of the document's draft (e.g. after an approver grant was revoked, T10). Call inside a
    /// transaction holding <see cref="LockResource"/>.
    /// </summary>
    public async Task FinalizeDraftIfCompleteAsync(int documentId, CancellationToken cancellationToken)
    {
        var draft = await db.DocumentVersions.SingleOrDefaultAsync(v => v.DocumentId == documentId && v.Status == VersionStatus.Draft, cancellationToken);
        if (draft is not null && await db.DocumentNodes.AnyAsync(n => n.DocumentVersionId == draft.Id, cancellationToken))
        {
            await FinalizeIfCompleteAsync(draft, await ComputeHashAsync(draft.Id, cancellationToken), cancellationToken);
        }
    }

    private async Task FinalizeIfCompleteAsync(DocumentVersion draft, byte[] hash, CancellationToken cancellationToken)
    {
        var approvers = (await ApproversAsync(draft.DocumentId, cancellationToken)).Select(a => a.Id).ToList();
        if (approvers.Count == 0)
        {
            return;
        }

        var signed = await db.VersionSignatures.AsNoTracking()
            .Where(s => s.DocumentVersionId == draft.Id && s.WithdrawnAt == null)
            .Select(s => new { s.UserId, s.ContentHash })
            .ToListAsync(cancellationToken);
        var valid = signed.Where(s => s.ContentHash.AsSpan().SequenceEqual(hash)).Select(s => s.UserId).ToHashSet();
        if (!approvers.All(valid.Contains))
        {
            return;
        }

        var last = await db.DocumentVersions.Where(v => v.DocumentId == draft.DocumentId).MaxAsync(v => v.VersionNumber, cancellationToken) ?? 0;
        draft.Status = VersionStatus.Signed;
        draft.VersionNumber = last + 1;
        draft.SignedAt = time.GetUtcNow().UtcDateTime;
        draft.SignedContentHash = hash;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> DocumentOfAsync(int versionId, CancellationToken cancellationToken) =>
        await db.DocumentVersions.Where(v => v.Id == versionId).Select(v => (int?)v.DocumentId).SingleOrDefaultAsync(cancellationToken)
        ?? throw DomainException.NotFound("Version", versionId);

    /// <summary>The guard of rule 1, re-checked inside the lock.</summary>
    private async Task<DocumentVersion> EditableDraftAsync(int versionId, CancellationToken cancellationToken)
    {
        var version = await db.DocumentVersions.SingleAsync(v => v.Id == versionId, cancellationToken);
        if (await db.Documents.Where(d => d.Id == version.DocumentId).Select(d => d.DeletedAt).SingleAsync(cancellationToken) is not null)
        {
            throw DomainException.Conflict(ErrorCodes.DocumentDeleted, "The document is deleted; restore it first.");
        }

        if (version.Status != VersionStatus.Draft)
        {
            throw DomainException.Conflict(ErrorCodes.VersionNotEditable, "Only draft versions can be signed.");
        }

        return version;
    }

    private async Task<List<UserRef>> ApproversAsync(int documentId, CancellationToken cancellationToken)
    {
        var approvers = await (
                from p in db.DocumentPermissions.AsNoTracking()
                where p.DocumentId == documentId && p.Role == DocumentRole.Approver
                join u in db.Users.AsNoTracking() on p.UserId equals u.Id
                select new UserRef(u.Id, u.DisplayName))
            .Distinct()
            .ToListAsync(cancellationToken);
        return approvers.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Id).ToList();
    }
}
