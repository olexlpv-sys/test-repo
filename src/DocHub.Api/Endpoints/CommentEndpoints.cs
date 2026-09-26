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

/// <summary>
/// Comments (T13, FR-CM1/CM2): on the document or on a node of a version, one level of replies, edit/delete of one's own,
/// resolve/reopen by the owner or approvers. Comments belong to their version (not copied to new drafts).
/// </summary>
internal sealed class CommentEndpoints : IEndpointModule
{
    public const int MaxBody = 4000;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/versions/{versionId:int}/comments", async Task<Ok<List<CommentView>>> (
                int versionId, Guid? logicalNodeId, string? scope, bool? includeResolved, bool? includePreviousVersions, DocHubDbContext db,
                IDocumentAuthorization authorization, CancellationToken ct) =>
            {
                var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct) ?? throw DomainException.NotFound("Version", versionId);
                await authorization.EnsureCanViewAsync(version.DocumentId, ct);
                if (scope is not (null or "all" or "document" or "node"))
                {
                    throw DomainException.Validation("scope must be all, document or node.");
                }

                return TypedResults.Ok(await ThreadsAsync(db, version, logicalNodeId, scope ?? "all", includeResolved ?? true, includePreviousVersions ?? false, ct));
            })
            .WithTags("Comments")
            .WithName("ListComments")
            .WithSummary("Comment threads of a version (document and/or node comments); includePreviousVersions adds older versions' comments on the same nodes.");

        endpoints.MapGet("/api/documents/{id:int}/comments/counts", async Task<Ok<CommentCounts>> (
                int id, int? versionId, DocHubDbContext db, IDocumentAuthorization authorization, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var version = versionId is { } v
                    ? await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == v && x.DocumentId == id, ct) ?? throw DomainException.NotFound("Version of this document", v)
                    : await db.DocumentVersions.AsNoTracking().Where(x => x.DocumentId == id && (x.Status == VersionStatus.Draft || x.IsCurrent)).OrderBy(x => x.Status).FirstOrDefaultAsync(ct)
                        ?? throw DomainException.NotFound("Current version of document", id);
                var counts = await db.Comments.AsNoTracking().Where(c => c.DocumentVersionId == version.Id && c.DeletedAt == null)
                    .GroupBy(c => c.LogicalNodeId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                return TypedResults.Ok(new CommentCounts(version.Id, counts.Where(c => c.Key == null).Sum(c => c.Count),
                    counts.Where(c => c.Key != null).ToDictionary(c => c.Key!.Value, c => c.Count)));
            })
            .WithTags("Comments")
            .WithName("GetCommentCounts")
            .WithSummary("Numbers of (not deleted) comments, replies included, on the document and per node of a version (default: the draft, else the current version).");

        endpoints.MapPost("/api/versions/{versionId:int}/comments", async Task<Results<Created<CommentView>, ValidationProblem>> (
                int versionId, CreateComment request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time,
                CancellationToken ct) =>
            {
                var version = await CommentableAsync(db, authorization, guard, versionId, ct);
                await authorization.DemandAsync(version.DocumentId, PermissionAction.Comment, ct);

                var errors = new Dictionary<string, string[]>();
                var body = request.Body?.Trim() ?? "";
                if (body.Length is 0 or > MaxBody)
                {
                    errors["body"] = [$"The comment needs 1 to {MaxBody} characters."];
                }

                if (request.LogicalNodeId is { } node && !await db.DocumentNodes.AnyAsync(n => n.DocumentVersionId == versionId && n.LogicalNodeId == node, ct))
                {
                    errors["logicalNodeId"] = ["The node is not part of this version."];
                }

                if (request.ParentCommentId is { } parentId)
                {
                    var parent = await db.Comments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parentId, ct);
                    if (parent is null || parent.DocumentVersionId != versionId || parent.LogicalNodeId != request.LogicalNodeId || parent.ParentCommentId is not null || parent.DeletedAt is not null)
                    {
                        errors["parentCommentId"] = ["Replies go to a top-level comment of the same version and node (one level only)."];
                    }
                }

                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors, title: "Validation failed", type: ErrorCodes.ValidationFailed);
                }

                var comment = new Comment
                {
                    DocumentId = version.DocumentId, DocumentVersionId = versionId, LogicalNodeId = request.LogicalNodeId, ParentCommentId = request.ParentCommentId,
                    AuthorUserId = user.UserId, Body = body, CreatedAt = time.GetUtcNow().UtcDateTime,
                };
                db.Comments.Add(comment);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/comments/{comment.Id}", await ViewAsync(db, comment.Id, ct));
            })
            .WithValidation<CreateComment>()
            .WithTags("Comments")
            .WithName("CreateComment")
            .WithSummary("Owner, editor or approver: comments on the document or a node of a Draft or Signed version, or replies to a top-level comment.");

        endpoints.MapPut("/api/comments/{id:int}", async Task<Results<Ok<CommentView>, ValidationProblem>> (
                int id, UpdateComment request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time,
                CancellationToken ct) =>
            {
                var comment = await MutableAsync(db, authorization, guard, id, ct);
                if (comment.AuthorUserId != user.UserId)
                {
                    throw DomainException.Forbidden("Only the author can edit a comment.");
                }

                var body = request.Body?.Trim() ?? "";
                if (body.Length is 0 or > MaxBody)
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [$"The comment needs 1 to {MaxBody} characters."] },
                        title: "Validation failed", type: ErrorCodes.ValidationFailed);
                }

                db.Entry(comment).Property(c => c.RowVersion).OriginalValue = request.RowVersion!;
                comment.Body = body;
                comment.EditedAt = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await ViewAsync(db, id, ct));
            })
            .WithValidation<UpdateComment>()
            .WithTags("Comments")
            .WithName("UpdateComment")
            .WithSummary("Author: edits a comment (rowVersion required).");

        endpoints.MapDelete("/api/comments/{id:int}", async Task<NoContent> (
                int id, string? rowVersion, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time,
                CancellationToken ct) =>
            {
                var expected = RowVersions.Parse(rowVersion);
                var comment = await MutableAsync(db, authorization, guard, id, ct);
                if (comment.AuthorUserId != user.UserId && !await authorization.CanAsync(comment.DocumentId, PermissionAction.Manage, ct))
                {
                    throw DomainException.Forbidden("Only the author or the document owner can delete a comment.");
                }

                db.Entry(comment).Property(c => c.RowVersion).OriginalValue = expected;
                comment.DeletedAt = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .WithTags("Comments")
            .WithName("DeleteComment")
            .WithSummary("Author or owner: soft-deletes a comment (?rowVersion=base64); a deleted comment with replies stays as \"deleted\".");

        foreach (var (path, resolve) in new[] { ("resolve", true), ("reopen", false) })
        {
            endpoints.MapPost($"/api/comments/{{id:int}}/{path}", async Task<Ok<CommentView>> (
                    int id, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
                {
                    var comment = await MutableAsync(db, authorization, guard, id, ct);
                    await authorization.DemandAsync(comment.DocumentId, PermissionAction.Resolve, ct);
                    if (comment.ParentCommentId is not null)
                    {
                        throw DomainException.Validation("Only top-level comments are resolved or reopened.");
                    }

                    comment.ResolvedAt = resolve ? time.GetUtcNow().UtcDateTime : null;
                    comment.ResolvedByUserId = resolve ? user.UserId : null;
                    await db.SaveChangesAsync(ct);
                    return TypedResults.Ok(await ViewAsync(db, id, ct));
                })
                .WithTags("Comments")
                .WithName(resolve ? "ResolveComment" : "ReopenComment")
                .WithSummary(resolve ? "Owner or approver: resolves a top-level comment." : "Owner or approver: reopens a resolved top-level comment.");
        }
    }

    /// <summary>View (404) → document active (409 document-deleted) → version not discarded (409 version-not-editable).</summary>
    private static async Task<DocumentVersion> CommentableAsync(DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, int versionId, CancellationToken ct)
    {
        var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct) ?? throw DomainException.NotFound("Version", versionId);
        await authorization.EnsureCanViewAsync(version.DocumentId, ct);
        await guard.EnsureDocumentActiveAsync(version.DocumentId, ct);
        if (version.Status == VersionStatus.Deleted)
        {
            throw DomainException.Conflict(ErrorCodes.VersionNotEditable, "The version was discarded; it can't be commented on.");
        }

        return version;
    }

    /// <summary>A comment that may be changed: visible, not deleted, on an active document and a version that isn't discarded.</summary>
    private static async Task<Comment> MutableAsync(DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, int id, CancellationToken ct)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw DomainException.NotFound("Comment", id);
        await CommentableAsync(db, authorization, guard, comment.DocumentVersionId, ct);
        return comment.DeletedAt is null ? comment : throw DomainException.NotFound("Comment", id);
    }

    private static async Task<CommentView> ViewAsync(DocHubDbContext db, int id, CancellationToken ct)
    {
        var comment = await db.Comments.AsNoTracking().SingleAsync(c => c.Id == id, ct);
        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == comment.DocumentVersionId, ct);
        var threads = await ThreadsAsync(db, version, comment.LogicalNodeId, comment.LogicalNodeId is null ? "document" : "node", true, false, ct, onlyId: comment.ParentCommentId ?? comment.Id);
        var thread = threads.Single();
        return thread.Id == id ? thread : thread.Replies.Single(r => r.Id == id);
    }

    private static async Task<List<CommentView>> ThreadsAsync(
        DocHubDbContext db, DocumentVersion version, Guid? logicalNodeId, string scope, bool includeResolved, bool includePreviousVersions, CancellationToken ct, int? onlyId = null)
    {
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == version.DocumentId).ToListAsync(ct);
        var numbers = versions.Where(v => v.VersionNumber != null).ToDictionary(v => v.Id, v => v.VersionNumber!.Value);
        var labels = versions.ToDictionary(v => v.Id, v => DocumentViews.Label(v, numbers));
        var titles = await db.DocumentNodes.AsNoTracking().Where(n => n.DocumentVersionId == version.Id).ToDictionaryAsync(n => n.LogicalNodeId, n => n.Title, ct);

        // Previous versions: older, not discarded versions of the document — their comments on nodes this version still has.
        var previous = includePreviousVersions
            ? versions.Where(v => v.Id != version.Id && v.CreatedAt <= version.CreatedAt && v.Status != VersionStatus.Deleted).Select(v => v.Id).ToList()
            : [];
        // Nodes of this version by join (a version can have thousands — too many for an id list parameter).
        var query = db.Comments.AsNoTracking().Where(c =>
            c.DocumentVersionId == version.Id
            || (previous.Contains(c.DocumentVersionId) && c.LogicalNodeId != null
                && db.DocumentNodes.Any(n => n.DocumentVersionId == version.Id && n.LogicalNodeId == c.LogicalNodeId)));
        if (onlyId is { } only)
        {
            query = query.Where(c => c.Id == only || c.ParentCommentId == only);
        }

        query = scope switch
        {
            "document" => query.Where(c => c.LogicalNodeId == null),
            "node" => query.Where(c => c.LogicalNodeId != null),
            _ => query,
        };
        if (logicalNodeId is { } l)
        {
            query = query.Where(c => c.LogicalNodeId == l);
        }

        var comments = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var userIds = comments.Select(c => c.AuthorUserId).Concat(comments.Where(c => c.ResolvedByUserId != null).Select(c => c.ResolvedByUserId!.Value)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), ct);

        CommentView View(Comment c, IReadOnlyList<CommentView> replies) => new(
            c.Id, c.DocumentVersionId, labels.GetValueOrDefault(c.DocumentVersionId), c.LogicalNodeId, c.LogicalNodeId is { } n ? titles.GetValueOrDefault(n) : null,
            users.GetValueOrDefault(c.AuthorUserId) ?? new UserRef(c.AuthorUserId, "?"), c.DeletedAt is null ? c.Body : null, c.DeletedAt is not null, c.CreatedAt, c.EditedAt,
            c.ResolvedAt, c.ResolvedByUserId is { } r ? users.GetValueOrDefault(r) : null, c.RowVersion, replies);

        var replies = comments.Where(c => c.ParentCommentId != null && c.DeletedAt == null).ToLookup(c => c.ParentCommentId!.Value);
        return comments
            .Where(c => c.ParentCommentId is null && (includeResolved || c.ResolvedAt is null))
            .Select(c => View(c, replies[c.Id].Select(r => View(r, [])).ToList()))
            // A deleted comment stays only as the head of its remaining replies.
            .Where(t => !t.IsDeleted || t.Replies.Count > 0)
            .ToList();
    }

    public sealed record CommentView(
        int Id, int VersionId, string? VersionLabel, Guid? LogicalNodeId, string? NodeTitle, UserRef Author, string? Body, bool IsDeleted, DateTime CreatedAt,
        DateTime? EditedAt, DateTime? ResolvedAt, UserRef? ResolvedBy, byte[] RowVersion, IReadOnlyList<CommentView> Replies);

    public sealed record CommentCounts(int VersionId, int Document, IReadOnlyDictionary<Guid, int> Nodes);

    public sealed class CreateComment
    {
        public Guid? LogicalNodeId { get; init; }

        [Range(1, int.MaxValue)]
        public int? ParentCommentId { get; init; }

        [Required]
        public string? Body { get; init; }
    }

    public sealed class UpdateComment
    {
        [Required]
        public string? Body { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }
}
