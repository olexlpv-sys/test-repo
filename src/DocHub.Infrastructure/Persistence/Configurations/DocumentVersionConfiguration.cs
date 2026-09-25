using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.ToAuditedTable("DocumentVersion");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.CreatedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.SignedAt).UtcTimestamp();
        builder.Property(x => x.SignedContentHash).HasColumnType("varbinary(32)").HasMaxLength(32);
        builder.Property(x => x.RowVersion).Version();
    }
}
