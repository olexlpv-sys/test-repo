using DocHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToAuditedTable("User");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Login).HasMaxLength(100);
        builder.Property(x => x.DisplayName).HasMaxLength(200);
        builder.Property(x => x.Email).HasMaxLength(256);
        builder.Property(x => x.IsAdmin).HasDefaultValue(false);
        builder.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
    }
}
