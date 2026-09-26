using System.ComponentModel.DataAnnotations;
using DocHub.Api.Auth;
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

        group.MapGet("", async Task<Ok<PagedResult<UserResponse>>> ([AsParameters] UserSearch query, DocHubDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                // Inactive users only for admins (the Admin tab's user list shows the whole seed, T17).
                var includeInactive = query.IncludeInactive && user.IsAdmin;
                var users = db.Users.AsNoTracking().Where(u => includeInactive || u.IsActive);
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    // Prefix search (LIKE 'x%', wildcards escaped by EF) on the indexed login, display name and e-mail.
                    var prefix = query.Search.Trim();
                    users = users.Where(u => u.Login.StartsWith(prefix) || u.DisplayName.StartsWith(prefix) || (u.Email != null && u.Email.StartsWith(prefix)));
                }

                var total = await users.CountAsync(ct);
                var items = await users
                    .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
                    .Skip(query.Skip)
                    .Take(query.PageSize)
                    .Select(u => new UserResponse(u.Id, u.Login, u.DisplayName, u.Email, u.IsAdmin, u.IsActive))
                    .ToListAsync(ct);
                return TypedResults.Ok(new PagedResult<UserResponse>(items, query.Page, query.PageSize, total));
            })
            .WithValidation<UserSearch>()
            .WithName("ListUsers")
            .WithSummary("Active users (admins: includeInactive for all), paged; search = prefix of login, display name or e-mail.");

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
        [property: Range(1, UserSearch.MaxPageSize)] int PageSize = UserSearch.DefaultPageSize,
        bool IncludeInactive = false)
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;

        /// <summary>Rows to skip; clamped so a huge page number gives an empty page instead of an overflow.</summary>
        public int Skip => (int)Math.Min(((long)Page - 1) * PageSize, int.MaxValue);
    }

    public sealed record UserResponse(int Id, string Login, string DisplayName, string? Email, bool IsAdmin, bool IsActive);
}
