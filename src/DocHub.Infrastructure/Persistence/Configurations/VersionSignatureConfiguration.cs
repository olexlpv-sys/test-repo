using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class VersionSignatureConfiguration : IEntityTypeConfiguration<VersionSignature>
{
    public void Configure(EntityTypeBuilder<VersionSignature> builder)
    {
        builder.ToAuditedTable("VersionSignature");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SignedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.ContentHash).Sha256();
        builder.Property(x => x.WithdrawnAt).UtcTimestamp();
        builder.Property(x => x.Comment).HasMaxLength(1000);
    }
}
