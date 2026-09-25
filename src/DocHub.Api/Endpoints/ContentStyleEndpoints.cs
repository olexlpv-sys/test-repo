using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocHub.Api.Auth;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Domain.Errors;
using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocHub.Api.Endpoints;

/// <summary>
/// The Word-compatible style catalog (FR-T6, docs/content-format.md §2): the editor's style dropdown, the generated
/// stylesheet, and admin maintenance. Built-in styles and styles used by content can't be deleted — deactivate them.
/// </summary>
internal sealed class ContentStyleEndpoints : IEndpointModule
{
    public const string StyleIdPattern = "^[A-Za-z][A-Za-z0-9]{0,49}$";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/content-styles").WithTags("Content styles");

        group.MapGet("", async Task<Ok<List<ContentStyleResponse>>> (DocHubDbContext db, CancellationToken ct, ContentStyleKind? kind = null, bool includeInactive = false) =>
            {
                var styles = await Project(db, db.ContentStyles
                        .Where(s => (includeInactive || s.IsActive) && (kind == null || s.Kind == kind))
                        .OrderBy(s => s.Kind).ThenBy(s => s.Name))
                    .ToListAsync(ct);
                return TypedResults.Ok(styles.Select(s => s.ToResponse()).ToList());
            })
            .WithName("ListContentStyles")
            .WithSummary("Styles for the editor's style dropdown, optionally of one kind.");

        group.MapGet("/stylesheet.css", async (HttpContext http, DocHubDbContext db, CancellationToken ct) =>
            {
                var styles = await db.ContentStyles.AsNoTracking().ToListAsync(ct);
                var css = StyleProperties.ToCss(styles);
                var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..32]}\"");
                http.Response.Headers.ETag = etag.ToString();
                http.Response.Headers.CacheControl = "no-cache";
                if (http.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Compare(etag, useStrongComparison: false)))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Text(css, "text/css", Encoding.UTF8);
            })
            .AllowAnonymous()
            .Produces<string>(StatusCodes.Status200OK, "text/css")
            .Produces(StatusCodes.Status304NotModified)
            .WithName("GetContentStylesheet")
            .WithSummary("CSS generated from the catalog (.ds-style-Heading1 { … }), revalidated with ETag.");

        group.MapGet("/{id:int}", async Task<Ok<ContentStyleResponse>> (int id, DocHubDbContext db, CancellationToken ct) =>
                TypedResults.Ok((await FindAsync(db, id, ct)).ToResponse()))
            .WithName("GetContentStyle")
            .WithSummary("A style; usageCount = contents using it.");

        group.MapPost("", async Task<Results<Created<ContentStyleResponse>, ValidationProblem>> (
                CreateContentStyle request, DocHubDbContext db, StyleProperties rules, CancellationToken ct) =>
            {
                var kind = request.Kind!.Value;
                var errors = rules.Validate(request.Properties, kind);
                await ValidateBasedOnAsync(db, request.StyleId!, kind, request.BasedOnStyleId, errors, ct);
                if (errors.Count > 0)
                {
                    return Problem(errors);
                }

                var style = new ContentStyle
                {
                    StyleId = request.StyleId!,
                    Name = request.Name!.Trim(),
                    Kind = kind,
                    BasedOnStyleId = request.BasedOnStyleId,
                    PropertiesJson = request.Properties.GetRawText(),
                };
                db.ContentStyles.Add(style);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/content-styles/{style.Id}", (await FindAsync(db, style.Id, ct)).ToResponse());
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<CreateContentStyle>()
            .WithName("CreateContentStyle")
            .WithSummary("Admin: creates a style (styleId unique; properties validated against the style property schema).");

        group.MapPut("/{id:int}", async Task<Results<Ok<ContentStyleResponse>, ValidationProblem>> (
                int id, UpdateContentStyle request, DocHubDbContext db, StyleProperties rules, CancellationToken ct) =>
            {
                var style = await db.ContentStyles.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw DomainException.NotFound("Content style", id);
                var errors = rules.Validate(request.Properties, style.Kind);
                await ValidateBasedOnAsync(db, style.StyleId, style.Kind, request.BasedOnStyleId, errors, ct);
                if (errors.Count > 0)
                {
                    return Problem(errors);
                }

                db.Entry(style).Property(s => s.RowVersion).OriginalValue = request.RowVersion!;
                style.Name = request.Name!.Trim();
                style.BasedOnStyleId = request.BasedOnStyleId;
                style.PropertiesJson = request.Properties.GetRawText();
                style.IsActive = request.IsActive;
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok((await FindAsync(db, id, ct)).ToResponse());
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithValidation<UpdateContentStyle>()
            .WithName("UpdateContentStyle")
            .WithSummary("Admin: updates a style (styleId and kind are fixed; rowVersion required); isActive = false deactivates it.");

        group.MapDelete("/{id:int}", async Task<NoContent> (int id, string? rowVersion, DocHubDbContext db, CancellationToken ct) =>
            {
                var version = ParseRowVersion(rowVersion);
                var style = await db.ContentStyles.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw DomainException.NotFound("Content style", id);
                if (style.IsBuiltIn)
                {
                    throw DomainException.Conflict(ErrorCodes.BuiltInStyle, "Built-in styles can't be deleted; deactivate the style instead.");
                }

                if (await db.ContentStyleUsages.AnyAsync(u => u.StyleId == style.StyleId, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.InUse, "The style is used by content; deactivate it instead.");
                }

                if (await db.ContentStyles.AnyAsync(s => s.BasedOnStyleId == style.StyleId, ct))
                {
                    throw DomainException.Conflict(ErrorCodes.InUse, "Other styles are based on this style.");
                }

                db.Entry(style).Property(s => s.RowVersion).OriginalValue = version;
                db.ContentStyles.Remove(style);
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("DeleteContentStyle")
            .WithSummary("Admin: deletes an unused custom style (?rowVersion=base64); built-in → 409 built-in-style, used → 409 in-use.");
    }

    private static IQueryable<StyleRow> Project(DocHubDbContext db, IQueryable<ContentStyle> styles) =>
        styles.AsNoTracking().Select(s => new StyleRow(
            s.Id, s.StyleId, s.Name, s.Kind, s.BasedOnStyleId, s.PropertiesJson, s.IsBuiltIn, s.IsActive, s.RowVersion,
            db.ContentStyleUsages.Count(u => u.StyleId == s.StyleId)));

    private static async Task<StyleRow> FindAsync(DocHubDbContext db, int id, CancellationToken ct) =>
        await Project(db, db.ContentStyles.Where(s => s.Id == id)).SingleOrDefaultAsync(ct) ?? throw DomainException.NotFound("Content style", id);

    /// <summary>BasedOn must be an existing style of the same kind (as in Word) and must not lead back to the style.</summary>
    private static async Task ValidateBasedOnAsync(
        DocHubDbContext db, string styleId, ContentStyleKind kind, string? basedOnStyleId, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (basedOnStyleId is null)
        {
            return;
        }

        var catalog = await db.ContentStyles.AsNoTracking().Select(s => new { s.StyleId, s.Kind, s.BasedOnStyleId }).ToDictionaryAsync(s => s.StyleId, StringComparer.OrdinalIgnoreCase, ct);
        if (!catalog.TryGetValue(basedOnStyleId, out var parent))
        {
            errors["basedOnStyleId"] = ["The style doesn't exist."];
            return;
        }

        if (parent.Kind != kind)
        {
            errors["basedOnStyleId"] = [$"A {kind} style can only be based on another {kind} style."];
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = basedOnStyleId; current is not null && seen.Add(current); current = catalog.GetValueOrDefault(current)?.BasedOnStyleId)
        {
            if (string.Equals(current, styleId, StringComparison.OrdinalIgnoreCase))
            {
                errors["basedOnStyleId"] = ["The style can't be based on itself or on a style based on it."];
                return;
            }
        }
    }

    private static byte[] ParseRowVersion(string? rowVersion)
    {
        try
        {
            return string.IsNullOrEmpty(rowVersion) ? throw new FormatException() : Convert.FromBase64String(rowVersion);
        }
        catch (FormatException)
        {
            throw DomainException.Validation("The rowVersion query parameter (base64) is required.");
        }
    }

    private static ValidationProblem Problem(Dictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, title: "Validation failed", type: ErrorCodes.ValidationFailed);

    private sealed record StyleRow(
        int Id, string StyleId, string Name, ContentStyleKind Kind, string? BasedOnStyleId, string PropertiesJson, bool IsBuiltIn, bool IsActive,
        byte[] RowVersion, int UsageCount)
    {
        public ContentStyleResponse ToResponse()
        {
            using var properties = JsonDocument.Parse(PropertiesJson);
            return new ContentStyleResponse(Id, StyleId, Name, Kind, BasedOnStyleId, properties.RootElement.Clone(), IsBuiltIn, IsActive, RowVersion, UsageCount);
        }
    }

    public class ContentStyleInput : IValidatableObject
    {
        [Required]
        [StringLength(100, MinimumLength = 1)]
        [RegularExpression(@".*\S.*", ErrorMessage = "Name must not be blank.")]
        public string? Name { get; init; }

        [StringLength(50)]
        public string? BasedOnStyleId { get; init; }

        /// <summary>Style properties (docs/content-format.md §2), e.g. <c>{ "fontSize": 40, "bold": true }</c>.</summary>
        public JsonElement Properties { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Properties.ValueKind == JsonValueKind.Undefined)
            {
                yield return new ValidationResult("The properties object is required.", [nameof(Properties)]);
            }
        }
    }

    public sealed class CreateContentStyle : ContentStyleInput
    {
        [Required]
        [RegularExpression(StyleIdPattern, ErrorMessage = "StyleId must match " + StyleIdPattern + ".")]
        public string? StyleId { get; init; }

        [Required]
        [EnumDataType(typeof(ContentStyleKind))]
        public ContentStyleKind? Kind { get; init; }
    }

    public sealed class UpdateContentStyle : ContentStyleInput
    {
        public bool IsActive { get; init; } = true;

        [Required]
        public byte[]? RowVersion { get; init; }
    }

    public sealed record ContentStyleResponse(
        int Id, string StyleId, string Name, ContentStyleKind Kind, string? BasedOnStyleId, JsonElement Properties, bool IsBuiltIn, bool IsActive,
        byte[] RowVersion, int UsageCount);
}
