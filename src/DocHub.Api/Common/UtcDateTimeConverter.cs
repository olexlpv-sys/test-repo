using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocHub.Api.Common;

/// <summary>
/// Writes every <see cref="DateTime"/> as UTC with a <c>Z</c>. All timestamps are stored in UTC (<c>SYSUTCDATETIME()</c>,
/// <c>TimeProvider.GetUtcNow()</c>), but SQL <c>datetime2</c> comes back as <see cref="DateTimeKind.Unspecified"/> — through
/// EF, raw SQL and ADO alike — and would be written without a designator, which browsers read as local time. Also applies to
/// <c>DateTime?</c>. Reading is unchanged.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        });
    }
}
