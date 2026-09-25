using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocHub.Domain.Content;

/// <summary>
/// Canonical form of content JSON for hashing (docs/content-format.md §2): object keys sorted ordinally, no insignificant
/// whitespace, strings re-escaped uniformly. Equal documents hash equal whatever their formatting or key order.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        var buffer = new ArrayBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Deepest nesting the canonical form handles (validated content is far shallower, ContentSchema.MaxDepth).</summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// SHA-256 of the canonical UTF-8 serialization. Content that isn't parseable within <see cref="MaxDepth"/> or holds
    /// escaped lone surrogates (only a script can store either) is hashed as raw text instead — deterministic, and any change still changes the hash.
    /// </summary>
    public static byte[] Hash(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(json)));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) // too deep, or strings .NET can't read (lone surrogates)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes("raw:" + json));
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed class ArrayBufferWriter : System.Buffers.IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            var needed = _written + Math.Max(sizeHint, 1);
            if (needed > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
            }
        }
    }
}
