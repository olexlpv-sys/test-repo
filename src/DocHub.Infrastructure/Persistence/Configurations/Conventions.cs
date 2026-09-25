using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocHub.Infrastructure.Persistence.Configurations;

internal static class Conventions
{
    public const string UtcNow = "SYSUTCDATETIME()";

    /// <summary>Maps a table that has an audit trigger (T03). EF Core must know about triggers so it does not use OUTPUT without INTO.</summary>
    public static EntityTypeBuilder<T> ToAuditedTable<T>(this EntityTypeBuilder<T> builder, string table)
        where T : class =>
        builder.ToTable(table, "app", t => t.HasTrigger($"TR_{table}_Audit"));

    public static PropertyBuilder<DateTime> UtcTimestamp(this PropertyBuilder<DateTime> property, bool defaultNow = false)
    {
        property.HasColumnType("datetime2(3)");
        return defaultNow ? property.HasDefaultValueSql(UtcNow) : property;
    }

    public static PropertyBuilder<DateTime?> UtcTimestamp(this PropertyBuilder<DateTime?> property) =>
        property.HasColumnType("datetime2(3)");

    public static PropertyBuilder<byte[]> Sha256(this PropertyBuilder<byte[]> property) =>
        property.HasColumnType("varbinary(32)").HasMaxLength(32);

    public static PropertyBuilder<byte[]> Version(this PropertyBuilder<byte[]> property) =>
        property.IsRowVersion();
}
