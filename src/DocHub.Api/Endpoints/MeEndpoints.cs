using DocHub.Api.Auth;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.Endpoints;

internal sealed class MeEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me", async (ICurrentUser currentUser, DocHubDbContext db, CancellationToken ct) =>
            {
                var me = await db.Users.AsNoTracking()
                    .Where(u => u.Id == currentUser.UserId)
                    .Select(u => new MeResponse(u.Id, u.Login, u.DisplayName, u.Email, u.IsAdmin))
                    .SingleOrDefaultAsync(ct);
                return me ?? throw DomainException.NotFound("User", currentUser.UserId);
            })
            .WithTags("Users")
            .WithName("GetMe")
            .WithSummary("The current user.");
    }

    public sealed record MeResponse(int Id, string Login, string DisplayName, string? Email, bool IsAdmin);
}
