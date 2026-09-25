using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class NodeContentConfiguration : IEntityTypeConfiguration<NodeContent>
{
    public void Configure(EntityTypeBuilder<NodeContent> builder)
    {
        builder.ToAuditedTable("NodeContent");
        builder.HasKey(x => x.NodeId);
        builder.Property(x => x.NodeId).ValueGeneratedNever();
        builder.Property(x => x.ContentHash).Sha256();
        builder.Property(x => x.ModifiedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.RowVersion).Version();
    }
}
