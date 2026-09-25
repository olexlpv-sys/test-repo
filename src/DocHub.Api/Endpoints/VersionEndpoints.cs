using System.ComponentModel.DataAnnotations;
using System.Globalization;
using DocHub.Api.Common;
using DocHub.Api.Documents;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocHub.Api.Endpoints;

/// <summary>Versions (T07): header (ETag = version stamp), signatures (all approvers must sign), discard draft.</summary>
internal sealed class VersionEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/versions").WithTags("Versions");

        group.MapGet("/{versionId:int}", async (int versionId, HttpContext http, DocHubDbContext db, IDocumentAuthorization authorization, DocumentViews views,
                CancellationToken ct) =>
            {
                var documentId = await DocumentOfAsync(db, versionId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);

                // NFR-L9: the stamp advances with every change of the version (content, status, signatures).
                var stamp = await db.VersionStamps.AsNoTracking().Where(s => s.DocumentVersionId == versionId).Select(s => (long?)s.LastChangeLogId).SingleOrDefaultAsync(ct) ?? 0;
                var etag = new EntityTagHeaderValue($"\"{stamp.ToString(CultureInfo.InvariantCulture)}\"");
                http.Response.Headers.ETag = etag.ToString();
                http.Response.Headers.CacheControl = "private, no-cache";
                http.Response.Headers.Vary = "X-User-Id";
                if (http.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Compare(etag, useStrongComparison: false)))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Ok((await views.VersionsAsync(documentId, versionId, ct)).Single());
            })
            .Produces<VersionHeader>()
            .Produces(StatusCodes.Status304NotModified)
            .WithName("GetVersion")
            .WithSummary("Version header; ETag = version stamp (If-None-Match → 304).");

        group.MapGet("/{versionId:int}/signatures", async Task<Ok<SignatureStatus>> (
                int versionId, DocHubDbContext db, IDocumentAuthorization authorization, SigningService signing, CancellationToken ct) =>
            {
                var documentId = await DocumentOfAsync(db, versionId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);
                var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, ct);
                return TypedResults.Ok(await signing.StatusAsync(version, ct));
            })
            .WithName("GetSignatures")
            .WithSummary("Required approvers, signatures (isValid = bound to the current content), pending approvers.");

        group.MapPost("/{versionId:int}/signatures", async Task<Ok<SigningResult>> (
                int versionId, SignRequest? request, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, SigningService signing,
                DocumentViews views, CancellationToken ct) =>
            {
                var documentId = await DocumentOfAsync(db, versionId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);
                await guard.EnsureEditableAsync(versionId, ct);
                // Without approvers nobody may sign; say why rather than "forbidden" (FR-V6).
                if (!await db.DocumentPermissions.AnyAsync(p => p.DocumentId == documentId && p.Role == DocumentRole.Approver, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.NoApprovers, "The document has no approvers, so it can't be signed.");
                }

                await authorization.DemandAsync(documentId, PermissionAction.Sign, ct);
                if (request?.Comment is { Length: > 1000 })
                {
                    throw DomainException.Validation("The comment can have at most 1000 characters.");
                }

                await signing.SignAsync(versionId, request?.Comment, ct);
                return TypedResults.Ok(await ResultAsync(db, signing, views, documentId, versionId, ct));
            })
            .WithName("SignVersion")
            .WithSummary("Approver: signs the draft with its current content hash; the last required signature finalizes it (vN).");

        group.MapDelete("/{versionId:int}/signatures/mine", async Task<Ok<SigningResult>> (
                int versionId, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, SigningService signing, DocumentViews views,
                CancellationToken ct) =>
            {
                var documentId = await DocumentOfAsync(db, versionId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);
                await guard.EnsureEditableAsync(versionId, ct);
                await authorization.DemandAsync(documentId, PermissionAction.Sign, ct);
                await signing.WithdrawAsync(versionId, ct);
                return TypedResults.Ok(await ResultAsync(db, signing, views, documentId, versionId, ct));
            })
            .WithName("WithdrawSignature")
            .WithSummary("Approver: withdraws my signature from the draft.");

        group.MapDelete("/{versionId:int}", async Task<NoContent> (
                int versionId, string? rowVersion, DocHubDbContext db, IDocumentAuthorization authorization, IVersionGuard guard, CancellationToken ct) =>
            {
                var expected = RowVersions.Parse(rowVersion);
                var documentId = await DocumentOfAsync(db, versionId, ct);
                await authorization.EnsureCanViewAsync(documentId, ct);
                await guard.EnsureEditableAsync(versionId, ct);
                await authorization.DemandAsync(documentId, PermissionAction.Manage, ct);
                await db.InTransactionAsync(async () =>
                {
                    await db.LockAsync(SigningService.LockResource(documentId), ct);
                    // Re-checked under the lock: a concurrent sign or document delete may have landed first.
                    var (draft, _) = await guard.EnsureEditableAsync(versionId, ct);

                    var latestSigned = await db.DocumentVersions.Where(v => v.DocumentId == documentId && v.Status == VersionStatus.Signed)
                        .OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct)
                        ?? throw DomainException.Conflict(ErrorCodes.OnlyVersion, "The draft is the only version; delete the document instead.");

                    // IsCurrent moves back to the latest signed version (one current version per document, NFR-L8).
                    db.Entry(draft).Property(v => v.RowVersion).OriginalValue = expected;
                    draft.Status = VersionStatus.Deleted;
                    draft.IsCurrent = false;
                    await db.SaveChangesAsync(ct);
                    latestSigned.IsCurrent = true;
                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                return TypedResults.NoContent();
            })
            .WithName("DiscardDraft")
            .WithSummary("Owner: discards the draft (?rowVersion=base64); 409 only-version when there is no signed version.");
    }

    private static async Task<int> DocumentOfAsync(DocHubDbContext db, int versionId, CancellationToken ct) =>
        await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => (int?)v.DocumentId).SingleOrDefaultAsync(ct)
        ?? throw DomainException.NotFound("Version", versionId);

    private static async Task<SigningResult> ResultAsync(DocHubDbContext db, SigningService signing, DocumentViews views, int documentId, int versionId, CancellationToken ct)
    {
        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, ct);
        return new SigningResult(await signing.StatusAsync(version, ct), (await views.VersionsAsync(documentId, versionId, ct)).Single());
    }

    public sealed class SignRequest
    {
        [StringLength(1000)]
        public string? Comment { get; init; }
    }

    public sealed record SigningResult(SignatureStatus Signatures, VersionHeader Version);
}
