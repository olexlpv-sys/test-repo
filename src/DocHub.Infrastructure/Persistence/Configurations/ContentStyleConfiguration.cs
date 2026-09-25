using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class ContentStyleConfiguration : IEntityTypeConfiguration<ContentStyle>
{
    public void Configure(EntityTypeBuilder<ContentStyle> builder)
    {
        builder.ToAuditedTable("ContentStyle");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StyleId).HasMaxLength(50).IsUnicode(false);
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.Kind).HasConversion<byte>();
        builder.Property(x => x.BasedOnStyleId).HasMaxLength(50).IsUnicode(false);
        builder.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
        builder.Property(x => x.RowVersion).Version();
    }
}
