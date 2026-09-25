using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class ReconciliationFindingConfiguration : IEntityTypeConfiguration<ReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<ReconciliationFinding> builder)
    {
        // Append-only ledger table written by audit procedures (T21 §4); read-only for the application.
        builder.ToTable("ReconciliationFinding", "audit");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasColumnType("varchar(40)").HasMaxLength(40).IsUnicode(false);
        builder.Property(x => x.TableName).HasMaxLength(256);
        builder.Property(x => x.Principal).HasMaxLength(256);
        builder.Property(x => x.Detail).HasMaxLength(1000);
        builder.Property(x => x.TransactionCommitTime).HasColumnType("datetime2(7)");
        builder.Property(x => x.DetectedAt).HasColumnType("datetime2(7)");
    }
}
