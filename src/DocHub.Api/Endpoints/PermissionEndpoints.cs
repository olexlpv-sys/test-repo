using System.ComponentModel.DataAnnotations;
using DocHub.Api.Auth;
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
/// Document roles (T10, FR-P1…P4): the owner grants and revokes Editor (document or node) and Approver roles; grants are
/// per document, so they carry over to new drafts. Grant CRUD is EF; checks are the permission procedures.
/// </summary>
internal sealed class PermissionEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/documents/{id:int}").WithTags("Permissions");

        group.MapGet("/permissions", async Task<Ok<DocumentPermissions>> (int id, DocHubDbContext db, IDocumentAuthorization authorization, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                return TypedResults.Ok(await PermissionsAsync(db, id, ct));
            })
            .WithName("GetDocumentPermissions")
            .WithSummary("The owner and all grants; node grants carry the node title from the draft (else the latest signed version) and whether the node is in the draft.");

        group.MapGet("/my-permissions", async Task<Ok<MyRoles>> (int id, DocHubDbContext db, IDocumentAuthorization authorization, ICurrentUser user, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var scopes = await db.DocumentPermissions.AsNoTracking()
                    .Where(p => p.DocumentId == id && p.UserId == user.UserId && p.Role == DocumentRole.Editor && p.LogicalNodeId != null)
                    .Select(p => p.LogicalNodeId!.Value).ToListAsync(ct);
                return TypedResults.Ok(DocumentViews.Roles(await authorization.EffectiveAsync(id, null, ct), scopes));
            })
            .WithName("GetMyPermissions")
            .WithSummary("My effective rights (for the UI): editableLogicalNodeIds are node grants expanded to descendants in the draft or current version.");

        group.MapPost("/permissions", async Task<Results<Created<Grant>, ValidationProblem>> (
                int id, CreateGrant request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, ICurrentUser user, TimeProvider time,
                CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                var document = await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Manage, ct);

                var errors = new Dictionary<string, string[]>();
                var role = request.Role!.Value;
                if (!Enum.IsDefined(role))
                {
                    errors["role"] = ["Must be Editor or Approver."];
                }
                else if (request.LogicalNodeId is not null && role != DocumentRole.Editor)
                {
                    errors["logicalNodeId"] = ["Only editors can be granted a single node."];
                }
                else if (request.LogicalNodeId is { } node && !await db.DocumentNodes.AnyAsync(n => n.LogicalNodeId == node && CurrentVersions(db, id).Contains(n.DocumentVersionId), ct))
                {
                    errors["logicalNodeId"] = ["The node is not in the draft or the latest signed version of this document."];
                }

                if (!await db.Users.AnyAsync(u => u.Id == request.UserId && u.IsActive, ct))
                {
                    errors["userId"] = ["The user doesn't exist or is inactive."];
                }

                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors, title: "Validation failed", type: ErrorCodes.ValidationFailed);
                }

                if (request.UserId == document.OwnerUserId)
                {
                    throw new DomainException(ErrorKind.Validation, ErrorCodes.OwnerCannotHaveRole, "The owner already has every owner right and can't be an editor or approver.");
                }

                // Under the document lock: a grant never lands on a document deleted meanwhile, and an approver added
                // during signing is counted by the next finalization check.
                var grantId = await db.InTransactionAsync(async () =>
                {
                    await db.LockAsync(SigningService.LockResource(id), ct);
                    await guard.EnsureDocumentActiveAsync(id, ct);
                    await authorization.RecheckAsync(id, PermissionAction.Manage, ct);
                    var grant = new DocumentPermission
                    {
                        DocumentId = id, UserId = request.UserId, Role = role, LogicalNodeId = request.LogicalNodeId,
                        GrantedAt = time.GetUtcNow().UtcDateTime, GrantedByUserId = user.UserId,
                    };
                    db.DocumentPermissions.Add(grant);
                    await db.SaveChangesAsync(ct);
                    return grant.Id;
                }, ct);
                var created = (await PermissionsAsync(db, id, ct)).Grants.Single(g => g.Id == grantId);
                return TypedResults.Created($"/api/documents/{id}/permissions/{grantId}", created);
            })
            .WithValidation<CreateGrant>()
            .WithName("GrantDocumentRole")
            .WithSummary("Owner: grants Editor (optionally of one node and its descendants) or Approver to a user.");

        group.MapDelete("/permissions/{grantId:int}", async Task<NoContent> (
                int id, int grantId, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, SigningService signing, CancellationToken ct) =>
            {
                await authorization.EnsureCanViewAsync(id, ct);
                await guard.EnsureDocumentActiveAsync(id, ct);
                await authorization.DemandAsync(id, PermissionAction.Manage, ct);
                await db.InTransactionAsync(async () =>
                {
                    await db.LockAsync(SigningService.LockResource(id), ct);
                    await guard.EnsureDocumentActiveAsync(id, ct);
                    await authorization.RecheckAsync(id, PermissionAction.Manage, ct);
                    var grant = await db.DocumentPermissions.SingleOrDefaultAsync(p => p.Id == grantId && p.DocumentId == id, ct)
                        ?? throw DomainException.NotFound("Grant", grantId);
                    db.DocumentPermissions.Remove(grant);
                    await db.SaveChangesAsync(ct);
                    // A former approver's signature no longer counts; without them the draft may now carry every
                    // required signature (T07 rule 2).
                    if (grant.Role == DocumentRole.Approver)
                    {
                        await signing.WithdrawRevokedApproverAsync(id, grant.UserId, ct);
                        await signing.FinalizeDraftIfCompleteAsync(id, ct);
                    }

                    return true;
                }, ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeDocumentRole")
            .WithSummary("Owner: revokes a grant; revoking an approver withdraws their signature on the draft and re-checks whether it is now fully signed.");
    }

    /// <summary>The draft and the latest signed version (where a node grant's node is looked up).</summary>
    private static IQueryable<int> CurrentVersions(DocHubDbContext db, int documentId) =>
        db.DocumentVersions.Where(v => v.DocumentId == documentId
                && (v.Status == VersionStatus.Draft
                    || (v.Status == VersionStatus.Signed && v.VersionNumber == db.DocumentVersions.Where(s => s.DocumentId == documentId && s.Status == VersionStatus.Signed).Max(s => s.VersionNumber))))
            .Select(v => v.Id);

    private static async Task<DocumentPermissions> PermissionsAsync(DocHubDbContext db, int documentId, CancellationToken ct)
    {
        var owner = await (from d in db.Documents.AsNoTracking()
                           where d.Id == documentId
                           join u in db.Users.AsNoTracking() on d.OwnerUserId equals u.Id
                           select new UserRef(u.Id, u.DisplayName)).SingleAsync(ct);
        var grants = await (from p in db.DocumentPermissions.AsNoTracking()
                            where p.DocumentId == documentId
                            join u in db.Users.AsNoTracking() on p.UserId equals u.Id
                            join g in db.Users.AsNoTracking() on p.GrantedByUserId equals g.Id
                            orderby p.Role, u.DisplayName, p.Id
                            select new { p.Id, User = new UserRef(u.Id, u.DisplayName), p.Role, p.LogicalNodeId, p.GrantedAt, GrantedBy = new UserRef(g.Id, g.DisplayName) })
            .ToListAsync(ct);

        // Node titles from the draft if there is one, else (and for nodes deleted in the draft) from the latest signed version.
        var versions = await CurrentVersions(db, documentId)
            .Join(db.DocumentVersions, id => id, v => v.Id, (_, v) => new { v.Id, IsDraft = v.Status == VersionStatus.Draft })
            .ToListAsync(ct);
        var logicalIds = grants.Where(g => g.LogicalNodeId != null).Select(g => g.LogicalNodeId!.Value).Distinct().ToList();
        var versionIds = versions.Select(v => v.Id).ToList();
        var nodes = logicalIds.Count == 0
            ? []
            : await db.DocumentNodes.AsNoTracking()
                .Where(n => versionIds.Contains(n.DocumentVersionId) && logicalIds.Contains(n.LogicalNodeId))
                .Select(n => new { n.LogicalNodeId, n.DocumentVersionId, n.Title })
                .ToListAsync(ct);
        var current = versions.OrderByDescending(v => v.IsDraft).FirstOrDefault()?.Id;
        var inCurrent = nodes.Where(n => n.DocumentVersionId == current).Select(n => n.LogicalNodeId).ToHashSet();
        var titles = nodes.OrderByDescending(n => n.DocumentVersionId == current).GroupBy(n => n.LogicalNodeId).ToDictionary(g => g.Key, g => g.First().Title);

        return new DocumentPermissions(owner, grants.Select(g => new Grant(
                g.Id, g.User, g.Role, g.LogicalNodeId, g.LogicalNodeId is { } l ? titles.GetValueOrDefault(l) : null,
                g.LogicalNodeId is { } n ? inCurrent.Contains(n) : null, g.GrantedAt, g.GrantedBy))
            .ToList());
    }

    public sealed record DocumentPermissions(UserRef Owner, IReadOnlyList<Grant> Grants);

    /// <summary>A grant; for node grants, <c>nodeInCurrentVersion</c> tells whether the node is in the draft (else the latest signed version) — where it isn't, the grant has no effect.</summary>
    public sealed record Grant(int Id, UserRef User, DocumentRole Role, Guid? LogicalNodeId, string? NodeTitle, bool? NodeInCurrentVersion, DateTime GrantedAt, UserRef GrantedBy);

    public sealed class CreateGrant
    {
        [Range(0, int.MaxValue)]
        public int UserId { get; init; }

        [Required]
        public DocumentRole? Role { get; init; }

        public Guid? LogicalNodeId { get; init; }
    }
}
