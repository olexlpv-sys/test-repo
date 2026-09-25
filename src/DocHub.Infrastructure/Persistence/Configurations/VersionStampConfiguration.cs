using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class VersionStampConfiguration : IEntityTypeConfiguration<VersionStamp>
{
    public void Configure(EntityTypeBuilder<VersionStamp> builder)
    {
        // Written only by the audit triggers (DENY for everyone else); the application reads it.
        builder.ToTable("VersionStamp");
        builder.HasKey(x => x.DocumentVersionId);
        builder.Property(x => x.DocumentVersionId).ValueGeneratedNever();
        builder.Property(x => x.TamperedAt).HasColumnType("datetime2(7)");
    }
}
