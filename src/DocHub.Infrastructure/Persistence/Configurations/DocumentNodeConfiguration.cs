using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class DocumentNodeConfiguration : IEntityTypeConfiguration<DocumentNode>
{
    public void Configure(EntityTypeBuilder<DocumentNode> builder)
    {
        builder.ToAuditedTable("DocumentNode");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500);
        builder.Property(x => x.CreatedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.ModifiedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.RowVersion).Version();
    }
}
