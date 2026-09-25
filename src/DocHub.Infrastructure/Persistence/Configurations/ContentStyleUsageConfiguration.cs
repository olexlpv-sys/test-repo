using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class ContentStyleUsageConfiguration : IEntityTypeConfiguration<ContentStyleUsage>
{
    public void Configure(EntityTypeBuilder<ContentStyleUsage> builder)
    {
        builder.ToTable("ContentStyleUsage");
        builder.HasKey(x => new { x.StyleId, x.NodeId });
        builder.Property(x => x.StyleId).HasMaxLength(50).IsUnicode(false);
    }
}
