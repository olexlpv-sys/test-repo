using System.ComponentModel.DataAnnotations;
using DocHub.Api.Auth;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

/// <summary>Node types (FR-N1, FR-N2): readable by everyone, maintained by admins; a used type can only be deactivated.</summary>
internal sealed class NodeTypeEndpoints : IEndpointModule
{
    public const string CodePattern = "^[A-Z][A-Z0-9_]{1,49}$";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/node-types").WithTags("Node types");

        group.MapGet("", async Task<Ok<List<NodeTypeResponse>>> (DocHubDbContext db, CancellationToken ct, bool includeInactive = false) =>
            {
                var types = await Project(db, db.NodeTypes.Where(t => includeInactive || t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)).ToListAsync(ct);
                return TypedResults.Ok(types);
            })
            .WithName("ListNodeTypes")
            .WithSummary("Node types ordered by sort order and name, with their usage counts.");

        group.MapGet("/{id:int}", async Task<Ok<NodeTypeResponse>> (int id, DocHubDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await FindAsync(db, id, ct)))
            .WithName("GetNodeType")
            .WithSummary("A node type; usageCount = nodes (any version) using it.");

        group.MapPost("", async Task<Created<NodeTypeResponse>> (CreateNodeType request, DocHubDbContext db, CancellationToken ct) =>
            {
                var type = new NodeType { Code = request.Code!, Name = request.Name!.Trim(), Description = request.Description, SortOrder = request.SortOrder };
                db.NodeTypes.Add(type);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/node-types/{type.Id}", await FindAsync(db, type.Id, ct));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<CreateNodeType>()
            .WithName("CreateNodeType")
            .WithSummary("Admin: creates a node type (code unique).");

        group.MapPut("/{id:int}", async Task<Ok<NodeTypeResponse>> (int id, UpdateNodeType request, DocHubDbContext db, CancellationToken ct) =>
            {
                var type = await db.NodeTypes.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw DomainException.NotFound("Node type", id);
                db.Entry(type).Property(t => t.RowVersion).OriginalValue = request.RowVersion!;
                type.Code = request.Code!;
                type.Name = request.Name!.Trim();
                type.Description = request.Description;
                type.SortOrder = request.SortOrder;
                type.IsActive = request.IsActive;
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await FindAsync(db, id, ct));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<UpdateNodeType>()
            .WithName("UpdateNodeType")
            .WithSummary("Admin: updates a node type (optimistic concurrency via rowVersion); isActive = false deactivates it.");

        group.MapDelete("/{id:int}", async Task<NoContent> (int id, DocHubDbContext db, CancellationToken ct) =>
            {
                var type = await db.NodeTypes.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw DomainException.NotFound("Node type", id);
                if (await db.DocumentNodes.AnyAsync(n => n.NodeTypeId == id, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.InUse, "The node type is used by nodes; deactivate it instead.");
                }

                db.NodeTypes.Remove(type);
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("DeleteNodeType")
            .WithSummary("Admin: deletes an unused node type (409 in-use otherwise).");
    }

    private static IQueryable<NodeTypeResponse> Project(DocHubDbContext db, IQueryable<NodeType> types) =>
        types.AsNoTracking().Select(t => new NodeTypeResponse(
            t.Id, t.Code, t.Name, t.Description, t.SortOrder, t.IsActive, t.RowVersion,
            db.DocumentNodes.Count(n => n.NodeTypeId == t.Id)));

    private static async Task<NodeTypeResponse> FindAsync(DocHubDbContext db, int id, CancellationToken ct) =>
        await Project(db, db.NodeTypes.Where(t => t.Id == id)).SingleOrDefaultAsync(ct) ?? throw DomainException.NotFound("Node type", id);

    public class CreateNodeType
    {
        [Required]
        [RegularExpression(CodePattern, ErrorMessage = "Code must match " + CodePattern + ".")]
        public string? Code { get; init; }

        [Required]
        [StringLength(100, MinimumLength = 1)]
        [RegularExpression(@".*\S.*", ErrorMessage = "Name must not be blank.")]
        public string? Name { get; init; }

        [StringLength(500)]
        public string? Description { get; init; }

        public int SortOrder { get; init; }
    }

    public sealed class UpdateNodeType : CreateNodeType
    {
        public bool IsActive { get; init; } = true;

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed record NodeTypeResponse(int Id, string Code, string Name, string? Description, int SortOrder, bool IsActive, byte[] RowVersion, int UsageCount);
}
