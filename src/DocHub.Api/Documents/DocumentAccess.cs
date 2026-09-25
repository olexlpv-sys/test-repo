using DocHub.Api.Auth;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Documents;

/// <summary>
/// The authorization seam of document features (T07 rule 5): every check goes through <c>app.usp_CheckPermission</c>, so
/// T10 changes the role logic in one place.
/// </summary>
public interface IDocumentAuthorization
{
    /// <summary>FR-P5: any user for a non-deleted document, owner/admin for a deleted one; otherwise <c>404</c>.</summary>
    Task EnsureCanViewAsync(int documentId, CancellationToken cancellationToken);

    Task<bool> CanAsync(int documentId, PermissionAction action, CancellationToken cancellationToken, int? versionId = null, Guid? logicalNodeId = null);

    /// <summary><c>403 forbidden</c> unless the current user may perform <paramref name="action"/>.</summary>
    Task DemandAsync(int documentId, PermissionAction action, CancellationToken cancellationToken, int? versionId = null, Guid? logicalNodeId = null);
}

internal sealed class DocumentAuthorization(IDbProcedures procedures, ICurrentUser user, DocHubDbContext db) : IDocumentAuthorization
{
    public async Task EnsureCanViewAsync(int documentId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(documentId, PermissionAction.View, cancellationToken))
        {
            throw DomainException.NotFound("Document", documentId);
        }
    }

    public Task<bool> CanAsync(int documentId, PermissionAction action, CancellationToken cancellationToken, int? versionId = null, Guid? logicalNodeId = null) =>
        procedures.CheckPermissionAsync(documentId, user.UserId, action, versionId, logicalNodeId, cancellationToken);

    public async Task DemandAsync(int documentId, PermissionAction action, CancellationToken cancellationToken, int? versionId = null, Guid? logicalNodeId = null)
    {
        if (!await CanAsync(documentId, action, cancellationToken, versionId, logicalNodeId))
        {
            // The document may have been deleted since the caller's earlier checks (a concurrent delete): answer as the
            // earlier checks would now — not visible → 404, deleted → 409 — rather than a misleading 403.
            await EnsureCanViewAsync(documentId, cancellationToken);
            if (action is not (PermissionAction.Move or PermissionAction.Restore)
                && await db.Documents.AsNoTracking().AnyAsync(d => d.Id == documentId && d.DeletedAt != null, cancellationToken))
            {
                throw DomainException.Conflict(ErrorCodes.DocumentDeleted, "The document is deleted; restore it first.");
            }

            throw DomainException.Forbidden($"You are not allowed to {Describe(action)} this document.");
        }
    }

    private static string Describe(PermissionAction action) => action switch
    {
        PermissionAction.Manage => "manage",
        PermissionAction.EditStructure => "change the structure of",
        PermissionAction.EditContent => "edit the content of",
        PermissionAction.Sign => "sign",
        PermissionAction.Move => "move",
        PermissionAction.Restore => "restore",
        _ => action.ToString().ToLowerInvariant() + " in",
    };
}

/// <summary>Editability guards (T07 rule 1), shared by every mutating endpoint of documents, trees, contents and comments.</summary>
public interface IVersionGuard
{
    /// <summary><c>409 document-deleted</c> if the document is deleted, <c>409 version-not-editable</c> unless the version is a Draft.</summary>
    Task<(DocumentVersion Version, Document Document)> EnsureEditableAsync(int versionId, CancellationToken cancellationToken);

    /// <summary><c>409 document-deleted</c> if the document is deleted.</summary>
    Task<Document> EnsureDocumentActiveAsync(int documentId, CancellationToken cancellationToken);
}

internal sealed class VersionGuard(DocHubDbContext db) : IVersionGuard
{
    public async Task<(DocumentVersion Version, Document Document)> EnsureEditableAsync(int versionId, CancellationToken cancellationToken)
    {
        var version = await db.DocumentVersions.SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken) ?? throw DomainException.NotFound("Version", versionId);
        var document = await EnsureDocumentActiveAsync(version.DocumentId, cancellationToken);
        if (version.Status != VersionStatus.Draft)
        {
            throw DomainException.Conflict(ErrorCodes.VersionNotEditable, "Only draft versions can be changed.");
        }

        return (version, document);
    }

    public async Task<Document> EnsureDocumentActiveAsync(int documentId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken) ?? throw DomainException.NotFound("Document", documentId);
        if (document.IsDeleted)
        {
            throw DomainException.Conflict(ErrorCodes.DocumentDeleted, "The document is deleted; restore it first.");
        }

        return document;
    }
}
