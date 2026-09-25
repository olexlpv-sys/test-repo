using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToAuditedTable("Comment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Body).HasMaxLength(4000);
        builder.Property(x => x.CreatedAt).UtcTimestamp(defaultNow: true);
        builder.Property(x => x.EditedAt).UtcTimestamp();
        builder.Property(x => x.DeletedAt).UtcTimestamp();
        builder.Property(x => x.ResolvedAt).UtcTimestamp();
        builder.Property(x => x.RowVersion).Version();
    }
}
