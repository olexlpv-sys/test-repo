using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping of the schema owned by the SQL Database Project (ADR-01). No migrations: the configurations mirror
/// database/DocHub.Database and a schema-drift test keeps them in sync.
/// </summary>
public sealed class DocHubDbContext(DbContextOptions<DocHubDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<NodeType> NodeTypes => Set<NodeType>();

    public DbSet<ContentStyle> ContentStyles => Set<ContentStyle>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    public DbSet<DocumentNode> DocumentNodes => Set<DocumentNode>();

    public DbSet<NodeContent> NodeContents => Set<NodeContent>();

    public DbSet<ContentStyleUsage> ContentStyleUsages => Set<ContentStyleUsage>();

    public DbSet<VersionSignature> VersionSignatures => Set<VersionSignature>();

    public DbSet<VersionStamp> VersionStamps => Set<VersionStamp>();

    public DbSet<DocumentPermission> DocumentPermissions => Set<DocumentPermission>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();

    public DbSet<VersionContentHash> VersionContentHashes => Set<VersionContentHash>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("app");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocHubDbContext).Assembly);
    }
}
