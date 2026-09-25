using System.ComponentModel.DataAnnotations;
using DocHub.Api.Common;
using DocHub.Api.Errors;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

/// <summary>Seeded users, read-only (FR-H2): type-ahead source for the "Acting as" dropdown and permission pickers.</summary>
internal sealed class UserEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/users").WithTags("Users");

        group.MapGet("", async Task<Ok<PagedResult<UserResponse>>> ([AsParameters] UserSearch query, DocHubDbContext db, CancellationToken ct) =>
            {
                var users = db.Users.AsNoTracking().Where(u => u.IsActive);
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    // Prefix search (LIKE 'x%', wildcards escaped by EF) on the indexed login, display name and e-mail.
                    var prefix = query.Search.Trim();
                    users = users.Where(u => u.Login.StartsWith(prefix) || u.DisplayName.StartsWith(prefix) || (u.Email != null && u.Email.StartsWith(prefix)));
                }

                var total = await users.CountAsync(ct);
                var items = await users
                    .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .Select(u => new UserResponse(u.Id, u.Login, u.DisplayName, u.Email, u.IsAdmin, u.IsActive))
                    .ToListAsync(ct);
                return TypedResults.Ok(new PagedResult<UserResponse>(items, query.Page, query.PageSize, total));
            })
            .WithValidation<UserSearch>()
            .WithName("ListUsers")
            .WithSummary("Active users, paged; search = prefix of login, display name or e-mail.");

        group.MapGet("/{id:int}", async Task<Ok<UserResponse>> (int id, DocHubDbContext db, CancellationToken ct) =>
            {
                var user = await db.Users.AsNoTracking()
                    .Where(u => u.Id == id)
                    .Select(u => new UserResponse(u.Id, u.Login, u.DisplayName, u.Email, u.IsAdmin, u.IsActive))
                    .SingleOrDefaultAsync(ct);
                return TypedResults.Ok(user ?? throw DomainException.NotFound("User", id));
            })
            .WithName("GetUser")
            .WithSummary("A user by id (also inactive users, e.g. authors in history).");
    }

    public sealed record UserSearch(
        [property: StringLength(100)] string? Search = null,
        [property: Range(1, int.MaxValue)] int Page = 1,
        [property: Range(1, UserSearch.MaxPageSize)] int PageSize = UserSearch.DefaultPageSize)
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;
    }

    public sealed record UserResponse(int Id, string Login, string DisplayName, string? Email, bool IsAdmin, bool IsActive);
}
