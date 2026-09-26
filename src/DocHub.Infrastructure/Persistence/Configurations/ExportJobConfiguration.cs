using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> builder)
    {
        builder.ToAuditedTable("ExportJob");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OptionsJson).HasMaxLength(1000);
        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.RequestedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.StartedAt).UtcTimestamp();
        builder.Property(x => x.FinishedAt).UtcTimestamp();
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.Property(x => x.BlobPath).HasMaxLength(400);
        builder.Property(x => x.FileName).HasMaxLength(300);
        builder.Property(x => x.CacheKey).HasColumnType("char(64)").HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.RowVersion).Version();
    }
}
