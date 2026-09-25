using System.ComponentModel.DataAnnotations;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

/// <summary>Documents (T07): list (via <c>usp_ListDocuments</c>), create, details, rename, move, delete, restore, new draft.</summary>
internal sealed class DocumentEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/folders/{folderId:int}/documents", async Task<Ok<PagedResult<DocumentListItem>>> (
                int folderId, [AsParameters] DocumentListRequest request, DocHubDbContext db, IDbProcedures procedures, SigningService signing,
                ICurrentUser user, CancellationToken ct) =>
            {
                if (!await db.Folders.AnyAsync(f => f.Id == folderId, ct))
                {
                    throw DomainException.NotFound("Folder", folderId);
                }

                var page = await procedures.ListDocumentsAsync(new DocumentListQuery(
                    folderId, user.UserId, request.IncludeDeleted, request.IncludeSubfolders, request.Search, request.Status, request.SortBy ?? "title",
                    request.SortDir ?? "asc", request.Page, request.PageSize), ct);

                var items = new List<DocumentListItem>(page.Items.Count);
                foreach (var row in page.Items)
                {
                    SignatureProgress? progress = null;
                    if (row.DraftVersionId is { } draftId)
                    {
                        // The procedure counts valid signatures from the cached draft hash; a stale cache is refreshed here.
                        var signed = row.SignaturesSigned ?? (await signing.StatusAsync(await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == draftId, ct), ct))
                            .Signatures.Count(s => s.IsValid);
                        progress = new SignatureProgress(signed, row.SignaturesRequired ?? 0);
                    }

                    var roles = new List<string>(3);
                    if (row.IsOwner)
                    {
                        roles.Add("Owner");
                    }

                    if (row.IsEditor)
                    {
                        roles.Add("Editor");
                    }

                    if (row.IsApprover)
                    {
                        roles.Add("Approver");
                    }

                    items.Add(new DocumentListItem(row.Id, row.RowVersion, row.FolderId, row.Title, row.Status, row.LatestSignedVersion, row.DraftVersionId is not null,
                        progress, new UserRef(row.OwnerUserId, row.OwnerDisplayName), roles, row.CreatedAt, row.ModifiedAt));
                }

                return TypedResults.Ok(new PagedResult<DocumentListItem>(items, request.Page, request.PageSize, page.TotalCount));
            })
            .WithValidation<DocumentListRequest>()
            .WithTags("Documents")
            .WithName("ListDocuments")
            .WithSummary("Documents of a folder (usp_ListDocuments): filter by title (case/accent-insensitive substring), status, sort, page; deleted ones only for their owner and admins.");

        var group = endpoints.MapGroup("/api/documents").WithTags("Documents");

        group.MapPost("", async Task<Results<Created<CreatedDocument>, ValidationProblem>> (CreateDocument request, DocHubDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                if (TitleErrors(request.Title) is { } errors)
                {
                    return errors;
                }

                if (!await db.Folders.AnyAsync(f => f.Id == request.FolderId, ct))
                {
                    throw DomainException.NotFound("Folder", request.FolderId);
                }

                var document = new Document { FolderId = request.FolderId, Title = request.Title!.Trim(), OwnerUserId = user.UserId };
                var draft = new DocumentVersion { Status = VersionStatus.Draft, CreatedByUserId = user.UserId, IsCurrent = true };
                await db.InTransactionAsync(async () =>
                {
                    db.Documents.Add(document);
                    await db.SaveChangesAsync(ct);
                    draft.DocumentId = document.Id;
                    db.DocumentVersions.Add(draft);
                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                return TypedResults.Created($"/api/documents/{document.Id}", new CreatedDocument(document.Id, draft.Id));
            })
            .WithValidation<CreateDocument>()
            .WithName("CreateDocument")
            .WithSummary("Creates a document (owner = caller) with an empty draft, in one transaction.");

        group.MapGet("/{id:int}", async Task<Ok<DocumentDetails>> (int id, IDocumentAuthorization authorization, DocumentViews views, ICurrentUser user, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                return TypedResults.Ok(await views.DetailsAsync(id, user.UserId, user.IsAdmin, ct));
            })
            .WithName("GetDocument")
            .WithSummary("Document details with its versions and my roles (deleted: owner/admin only, else 404).");

        group.MapPut("/{id:int}", async Task<Results<Ok<DocumentDetails>, ValidationProblem>> (
                int id, RenameDocument request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, DocumentViews views, ICurrentUser user,
                CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var document = await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Manage, ct);
                if (TitleErrors(request.Title) is { } errors)
                {
                    return errors;
                }

                db.Entry(document).Property(d => d.RowVersion).OriginalValue = request.RowVersion!;
                document.Title = request.Title!.Trim();
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await views.DetailsAsync(id, user.UserId, user.IsAdmin, ct));
            })
            .WithValidation<RenameDocument>()
            .WithName("RenameDocument")
            .WithSummary("Owner: renames the document (document-level title; signatures are not affected).");

        group.MapPost("/{id:int}/move", async Task<Ok<DocumentDetails>> (
                int id, MoveDocument request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, DocumentViews views, ICurrentUser user,
                CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                // Admins may move deleted documents too (to empty a folder, FR-F4); owners only active ones.
                var document = user.IsAdmin
                    ? await db.Documents.SingleAsync(d => d.Id == id, ct)
                    : await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Move, ct);
                if (!await db.Folders.AnyAsync(f => f.Id == request.FolderId, ct))
                {
                    throw DomainException.NotFound("Folder", request.FolderId);
                }

                db.Entry(document).Property(d => d.RowVersion).OriginalValue = request.RowVersion!;
                document.FolderId = request.FolderId;
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await views.DetailsAsync(id, user.UserId, user.IsAdmin, ct));
            })
            .WithValidation<MoveDocument>()
            .WithName("MoveDocument")
            .WithSummary("Owner or admin: moves the document to another folder (admins also deleted documents).");

        group.MapDelete("/{id:int}", async Task<NoContent> (
                int id, string? rowVersion, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time,
                CancellationToken ct) =>
            {
                var version = RowVersions.Parse(rowVersion);
                await authorization.EnsureCanViewAsync(id, ct);
                var document = await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Manage, ct);
                db.Entry(document).Property(d => d.RowVersion).OriginalValue = version;
                document.DeletedAt = time.GetUtcNow().UtcDateTime;
                document.DeletedByUserId = user.UserId;
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteDocument")
            .WithSummary("Owner: soft-deletes the document (?rowVersion=base64); versions, draft and signatures stay as they are.");

        group.MapPost("/{id:int}/restore", async Task<Ok<DocumentDetails>> (
                int id, RestoreDocument? request, DocHubDbContext db, IDocumentAuthorization authorization, DocumentViews views, ICurrentUser user, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Restore, ct);
                var document = await db.Documents.SingleAsync(d => d.Id == id, ct);
                if (!document.IsDeleted)
                {
                    throw DomainException.Conflict(ErrorCodes.NotDeleted, "The document is not deleted.");
                }

                var folderId = request?.FolderId ?? document.FolderId;
                if (!await db.Folders.AnyAsync(f => f.Id == folderId, ct))
                {
                    throw request?.FolderId is null
                        ? DomainException.Conflict(ErrorCodes.FolderMissing, "The original folder no longer exists; restore into another folder (folderId).")
                        : DomainException.NotFound("Folder", folderId);
                }

                document.FolderId = folderId;
                document.DeletedAt = null;
                document.DeletedByUserId = null;
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await views.DetailsAsync(id, user.UserId, user.IsAdmin, ct));
            })
            .WithName("RestoreDocument")
            .WithSummary("Owner or admin: restores a deleted document, optionally into another folder.");

        group.MapPost("/{id:int}/drafts", async Task<Created<VersionHeader>> (
                int id, DocHubDbContext db, IDbProcedures procedures, IDocumentAuthorization authorization, IVersionGuard guard, DocumentViews views, ICurrentUser user,
                CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Manage, ct);
                var newVersionId = await db.InTransactionAsync(async () =>
                {
                    await db.LockAsync(SigningService.LockResource(id), ct);
                    if (await db.DocumentVersions.AnyAsync(v => v.DocumentId == id && v.Status == VersionStatus.Draft, ct))
                    {
                        throw DomainException.Conflict(ErrorCodes.DraftAlreadyExists, "The document already has a draft.");
                    }

                    var latestSigned = await db.DocumentVersions.Where(v => v.DocumentId == id && v.Status == VersionStatus.Signed)
                        .OrderByDescending(v => v.VersionNumber).Select(v => (int?)v.Id).FirstOrDefaultAsync(ct)
                        ?? throw DomainException.Conflict(ErrorCodes.NoSignedVersion, "A new draft is copied from the latest signed version, and there is none yet.");
                    return await procedures.CopyVersionToDraftAsync(latestSigned, user.UserId, ct);
                }, ct);

                var header = (await views.VersionsAsync(id, newVersionId, ct)).Single();
                return TypedResults.Created($"/api/versions/{newVersionId}", header);
            })
            .WithName("CreateDraft")
            .WithSummary("Owner: new draft = deep copy of the latest signed version (usp_CopyVersionToDraft).");
    }

    private static ValidationProblem? TitleErrors(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        var error = trimmed.Length switch
        {
            0 => "The title is required.",
            > 300 => "The title can have at most 300 characters.",
            _ => null,
        };
        return error is null
            ? null
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [error] }, title: "Validation failed", type: ErrorCodes.ValidationFailed);
    }

    public sealed record DocumentListRequest(
        [property: StringLength(300)] string? Search = null,
        [property: RegularExpression("^(Draft|Signed|Deleted)$")] string? Status = null,
        [property: RegularExpression("^(title|status|modifiedAt|createdAt|owner|latestSignedVersion)$")] string? SortBy = null,
        [property: RegularExpression("^(asc|desc)$")] string? SortDir = null,
        bool IncludeDeleted = false,
        bool IncludeSubfolders = false,
        [property: Range(1, 1_000_000)] int Page = 1,
        [property: Range(1, PageRequest.MaxPageSize)] int PageSize = PageRequest.DefaultPageSize);

    public sealed record SignatureProgress(int Signed, int Required);

    public sealed record DocumentListItem(
        int Id, byte[] RowVersion, int FolderId, string Title, string Status, int? LatestSignedVersion, bool HasDraft, SignatureProgress? SignatureProgress,
        UserRef Owner, IReadOnlyList<string> MyRoles, DateTime CreatedAt, DateTime ModifiedAt);

    public sealed record CreatedDocument(int Id, int DraftVersionId);

    public sealed class CreateDocument
    {
        [Range(1, int.MaxValue)]
        public int FolderId { get; init; }

        [Required]
        public string? Title { get; init; }
    }

    public sealed class RenameDocument
    {
        [Required]
        public string? Title { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed class MoveDocument
    {
        [Range(1, int.MaxValue)]
        public int FolderId { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed class RestoreDocument
    {
        public int? FolderId { get; init; }
    }
}
