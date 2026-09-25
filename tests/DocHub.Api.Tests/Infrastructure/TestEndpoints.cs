using System.ComponentModel.DataAnnotations;
using DocHub.Api.Common;
using DocHub.Api.Endpoints;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using DocHub.Infrastructure.Procedures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>Test-only endpoints that exercise the cross-cutting plumbing (T04) through real requests.</summary>
internal sealed class TestEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/__test");

        group.MapPost("/folders/{id:int}/touch", async (int id, DocHubDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var folder = await db.Folders.SingleAsync(f => f.Id == id, ct);
            folder.SortOrder++;
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new { traceId = http.TraceIdentifier });
        });

        group.MapPut("/folders/{id:int}", async (int id, RenameFolder request, DocHubDbContext db, CancellationToken ct) =>
        {
            var folder = await db.Folders.SingleAsync(f => f.Id == id, ct);
            db.Entry(folder).Property(f => f.RowVersion).OriginalValue = request.RowVersion;
            folder.Name = request.Name;
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new { folder.RowVersion });
        });

        group.MapPost("/folders", async (CreateFolder request, DocHubDbContext db, CancellationToken ct) =>
        {
            var folder = new Folder { ParentFolderId = request.ParentFolderId, Name = request.Name!, SortOrder = 1, CreatedByUserId = 1 };
            db.Folders.Add(folder);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new { folder.Id, folder.RowVersion, folder.CreatedAt });
        }).WithValidation<CreateFolder>();

        // Skip the endpoints' pre-checks to hit the database constraints directly (lost races, T06 review).
        group.MapPost("/folders/raw/{parentId:int}", async (int parentId, DocHubDbContext db, CancellationToken ct) =>
        {
            db.Folders.Add(new Folder { ParentFolderId = parentId, Name = $"raw {Guid.NewGuid():N}", SortOrder = 1, CreatedByUserId = 1 });
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok();
        });

        group.MapDelete("/folders/raw/{id:int}", async (int id, DocHubDbContext db, CancellationToken ct) =>
        {
            db.Folders.Remove(await db.Folders.SingleAsync(f => f.Id == id, ct));
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });

        group.MapPost("/errors/{kind}", IResult (string kind) => throw kind switch
        {
            "validation" => DomainException.Validation("Bad input."),
            "not-found" => DomainException.NotFound("Thing", 42),
            "forbidden" => DomainException.Forbidden("No."),
            "conflict" => DomainException.Conflict("version-not-editable", "Only drafts can be edited."),
            _ => new InvalidOperationException("Boom: secret internal detail"),
        });

        group.MapGet("/ping", (IDbProcedures procedures, CancellationToken ct) => procedures.PingAsync(ct));

        group.MapGet("/enum", () => TypedResults.Ok(new { status = VersionStatus.Signed, role = DocumentRole.Approver }));

        group.MapGet("/paging", ([AsParameters] PageRequest paging) => TypedResults.Ok(new PagedResult<int>([1, 2], paging.Page, paging.PageSize, 2)))
            .WithValidation<PageRequest>();
    }

    public sealed record RenameFolder(string Name, byte[] RowVersion);

    public sealed class CreateFolder
    {
        public int? ParentFolderId { get; init; }

        [Required]
        [StringLength(200, MinimumLength = 1)]
        public string? Name { get; init; }
    }
}
