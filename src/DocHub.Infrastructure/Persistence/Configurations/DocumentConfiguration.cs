using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToAuditedTable("Document");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(300);
        builder.Property(x => x.CreatedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.DeletedAt).UtcTimestamp();
        builder.Property(x => x.RowVersion).Version();
        builder.Ignore(x => x.IsDeleted);
    }
}
