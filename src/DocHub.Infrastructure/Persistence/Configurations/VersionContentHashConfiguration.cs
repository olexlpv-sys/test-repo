using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class VersionContentHashConfiguration : IEntityTypeConfiguration<VersionContentHash>
{
    public void Configure(EntityTypeBuilder<VersionContentHash> builder)
    {
        // Derived cache written by the API (not audited).
        builder.ToTable("VersionContentHash");
        builder.HasKey(x => x.DocumentVersionId);
        builder.Property(x => x.DocumentVersionId).ValueGeneratedNever();
        builder.Property(x => x.ContentHash).Sha256();
        builder.Property(x => x.ComputedAt).UtcTimestamp(defaultNow: true);
    }
}
