using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class DocumentPermissionConfiguration : IEntityTypeConfiguration<DocumentPermission>
{
    public void Configure(EntityTypeBuilder<DocumentPermission> builder)
    {
        builder.ToAuditedTable("DocumentPermission");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).HasConversion<byte>();
        builder.Property(x => x.GrantedAt).UtcTimestamp(defaultNow: true);
    }
}
