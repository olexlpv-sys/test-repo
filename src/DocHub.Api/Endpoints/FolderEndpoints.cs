using System.ComponentModel.DataAnnotations;
using System.Data;
using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Domain.Ordering;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

/// <summary>
/// Virtual folders (FR-F1…F5): everyone reads the tree; admins create, rename, move and delete. The folder table is small,
/// so the tree is loaded in one query and assembled in memory.
/// </summary>
internal sealed class FolderEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/folders").WithTags("Folders");

        group.MapGet("/tree", async Task<Ok<List<FolderNode>>> (DocHubDbContext db, CancellationToken ct) =>
            {
                var folders = await db.Folders.AsNoTracking().Select(f => new { f.Id, f.ParentFolderId, f.Name, f.SortOrder }).ToListAsync(ct);
                var counts = await db.Documents.AsNoTracking()
                    .Where(d => d.DeletedAt == null)
                    .GroupBy(d => d.FolderId)
                    .Select(g => new { FolderId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(c => c.FolderId, c => c.Count, ct);
                var children = folders.ToLookup(f => f.ParentFolderId);

                List<FolderNode> Build(int? parentId) => children[parentId]
                    .OrderBy(f => f.SortOrder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id)
                    .Select(f => new FolderNode(f.Id, f.Name, f.SortOrder, counts.GetValueOrDefault(f.Id), Build(f.Id)))
                    .ToList();

                return TypedResults.Ok(Build(null));
            })
            .WithName("GetFolderTree")
            .WithSummary("The whole folder tree, nested and ordered; documentCount = non-deleted documents directly in the folder.");

        group.MapGet("/{id:int}", async Task<Ok<FolderResponse>> (int id, DocHubDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await DetailsAsync(db, id, ct)))
            .WithName("GetFolder")
            .WithSummary("A folder with its breadcrumb path from the root.");

        group.MapPost("", async Task<Results<Created<FolderResponse>, ValidationProblem>> (
                CreateFolder request, DocHubDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                if (NameErrors(request.Name) is { } errors)
                {
                    return errors;
                }

                if (request.ParentFolderId is { } parentId && !await db.Folders.AnyAsync(f => f.Id == parentId, ct))
                {
                    throw DomainException.NotFound("Folder", parentId);
                }

                var siblings = await SiblingOrdersAsync(db, request.ParentFolderId, excludeId: null, ct);
                var folder = new Folder
                {
                    ParentFolderId = request.ParentFolderId,
                    Name = request.Name!.Trim(),
                    SortOrder = siblings.Count == 0 ? SiblingOrder.Gap : (int)Math.Min((long)siblings[^1] + SiblingOrder.Gap, int.MaxValue),
                    CreatedByUserId = user.UserId,
                };
                db.Folders.Add(folder);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/folders/{folder.Id}", await DetailsAsync(db, folder.Id, ct));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<CreateFolder>()
            .WithName("CreateFolder")
            .WithSummary("Admin: creates a folder as the last child of the parent (root when parentFolderId is null).");

        group.MapPut("/{id:int}", async Task<Results<Ok<FolderResponse>, ValidationProblem>> (int id, RenameFolder request, DocHubDbContext db, CancellationToken ct) =>
            {
                if (NameErrors(request.Name) is { } errors)
                {
                    return errors;
                }

                var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw DomainException.NotFound("Folder", id);
                db.Entry(folder).Property(f => f.RowVersion).OriginalValue = request.RowVersion!;
                folder.Name = request.Name!.Trim();
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await DetailsAsync(db, id, ct));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<RenameFolder>()
            .WithName("RenameFolder")
            .WithSummary("Admin: renames a folder (rowVersion required).");

        group.MapPost("/{id:int}/move", async Task<Ok<FolderResponse>> (int id, MoveFolder request, DocHubDbContext db, CancellationToken ct) =>
            {
                // The retrying execution strategy requires user transactions to run inside it (retried as a whole).
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                    // One move at a time: two concurrent moves could otherwise create a cycle together.
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        DECLARE @result INT;
                        EXEC @result = sys.sp_getapplock @Resource = N'folders', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
                        IF @result < 0 THROW 50041, N'Another folder move is in progress; try again.', 1;
                        """, ct);

                    var all = await db.Folders.AsNoTracking().Select(f => new { f.Id, f.ParentFolderId, f.SortOrder, f.Name }).ToDictionaryAsync(f => f.Id, ct);
                    if (!all.ContainsKey(id))
                    {
                        throw DomainException.NotFound("Folder", id);
                    }

                    if (request.NewParentFolderId is { } parentId)
                    {
                        if (!all.ContainsKey(parentId))
                        {
                            throw DomainException.NotFound("Folder", parentId);
                        }

                        // The new parent must not be the folder itself or one of its descendants (bounded: the tree is never
                        // longer than the folder count; TR_Folder_NoCycle keeps the table acyclic).
                        var steps = 0;
                        for (int? current = parentId; current is { } c && all.ContainsKey(c) && steps++ <= all.Count; current = all[c].ParentFolderId)
                        {
                            if (c == id)
                            {
                                throw DomainException.Conflict(ErrorCodes.InvalidMove, "A folder can't be moved into itself or into one of its sub-folders.");
                            }
                        }
                    }

                    var siblings = all.Values
                        .Where(f => f.ParentFolderId == request.NewParentFolderId && f.Id != id)
                        .OrderBy(f => f.SortOrder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id)
                        .ToList();
                    var (sortOrder, renumbered) = SiblingOrder.Place(siblings.Select(f => f.SortOrder).ToList(), request.Position);

                    var folder = await db.Folders.SingleAsync(f => f.Id == id, ct);
                    db.Entry(folder).Property(f => f.RowVersion).OriginalValue = request.RowVersion!;
                    folder.ParentFolderId = request.NewParentFolderId;
                    folder.SortOrder = sortOrder;
                    if (renumbered is not null)
                    {
                        var tracked = await db.Folders.Where(f => f.ParentFolderId == request.NewParentFolderId && f.Id != id).ToDictionaryAsync(f => f.Id, ct);
                        for (var i = 0; i < siblings.Count; i++)
                        {
                            tracked[siblings[i].Id].SortOrder = renumbered[i];
                        }
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                });
                return TypedResults.Ok(await DetailsAsync(db, id, ct));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<MoveFolder>()
            .WithName("MoveFolder")
            .WithSummary("Admin: re-parents and/or reorders a folder; position = 0-based index among the new siblings (default last).");

        group.MapDelete("/{id:int}", async Task<NoContent> (int id, string? rowVersion, DocHubDbContext db, CancellationToken ct) =>
            {
                var version = RowVersions.Parse(rowVersion);
                var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw DomainException.NotFound("Folder", id);
                if (await db.Folders.AnyAsync(f => f.ParentFolderId == id, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.InUse, "The folder has sub-folders.");
                }

                // Deleted documents count too: an admin moves them to another folder first (FR-F4).
                if (await db.Documents.AnyAsync(d => d.FolderId == id, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.InUse, "The folder contains documents (deleted ones included).");
                }

                db.Entry(folder).Property(f => f.RowVersion).OriginalValue = version;
                db.Folders.Remove(folder);
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("DeleteFolder")
            .WithSummary("Admin: deletes an empty folder (?rowVersion=base64); sub-folders or any documents → 409 in-use.");
    }

    private static async Task<FolderResponse> DetailsAsync(DocHubDbContext db, int id, CancellationToken ct)
    {
        var all = await db.Folders.AsNoTracking().Select(f => new { f.Id, f.ParentFolderId, f.Name, f.SortOrder, f.RowVersion }).ToDictionaryAsync(f => f.Id, ct);
        if (!all.TryGetValue(id, out var folder))
        {
            throw DomainException.NotFound("Folder", id);
        }

        var path = new List<FolderRef>();
        var seen = new HashSet<int>();
        for (int? current = id; current is { } c && all.TryGetValue(c, out var item) && seen.Add(c); current = item.ParentFolderId)
        {
            path.Insert(0, new FolderRef(item.Id, item.Name));
        }

        return new FolderResponse(folder.Id, folder.ParentFolderId, folder.Name, folder.SortOrder, path, folder.RowVersion);
    }

    private static async Task<List<int>> SiblingOrdersAsync(DocHubDbContext db, int? parentId, int? excludeId, CancellationToken ct) =>
        await db.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == parentId && f.Id != excludeId)
            .OrderBy(f => f.SortOrder)
            .Select(f => f.SortOrder)
            .ToListAsync(ct);

    /// <summary>FR-F4 name rules: trimmed, 1–200 characters, no path separators.</summary>
    private static ValidationProblem? NameErrors(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        string? error = trimmed.Length switch
        {
            0 => "The name is required.",
            > 200 => "The name can have at most 200 characters.",
            _ when trimmed.IndexOfAny(['/', '\\']) >= 0 => "The name can't contain / or \\.",
            _ => null,
        };
        return error is null
            ? null
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [error] }, title: "Validation failed", type: ErrorCodes.ValidationFailed);
    }

    public sealed record FolderNode(int Id, string Name, int SortOrder, int DocumentCount, List<FolderNode> Children);

    public sealed record FolderRef(int Id, string Name);

    public sealed record FolderResponse(int Id, int? ParentFolderId, string Name, int SortOrder, List<FolderRef> Path, byte[] RowVersion);

    public sealed class CreateFolder
    {
        public int? ParentFolderId { get; init; }

        [Required]
        public string? Name { get; init; }
    }

    public sealed class RenameFolder
    {
        [Required]
        public string? Name { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed class MoveFolder
    {
        public int? NewParentFolderId { get; init; }

        [Range(0, int.MaxValue)]
        public int? Position { get; init; }

        [Required]
        public byte[]? RowVersion { get; init; }
    }
}
