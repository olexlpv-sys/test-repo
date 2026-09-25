using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;

namespace DocHub.Api.Common;

/// <summary>
/// HybridCache serialization with System.Text.Json at the API's depth limit (256) instead of the default 64: cached trees
/// of signed versions nest up to 100 levels (two JSON levels each). Strings and byte arrays keep the built-in serializers.
/// </summary>
internal sealed class DeepJsonCacheSerializerFactory : IHybridCacheSerializerFactory
{
    public const int MaxDepth = 256;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = MaxDepth };

    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        serializer = typeof(T) == typeof(string) || typeof(T) == typeof(byte[]) ? null : new Serializer<T>();
        return serializer is not null;
    }

    private sealed class Serializer<T> : IHybridCacheSerializer<T>
    {
        public T Deserialize(ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source, new JsonReaderOptions { MaxDepth = MaxDepth });
            return JsonSerializer.Deserialize<T>(ref reader, Options)!;
        }

        public void Serialize(T value, IBufferWriter<byte> target)
        {
            using var writer = new Utf8JsonWriter(target, new JsonWriterOptions { MaxDepth = MaxDepth });
            JsonSerializer.Serialize(writer, value, Options);
        }
    }
}
