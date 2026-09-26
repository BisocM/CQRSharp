using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Idempotency;

/// <summary>
///     The System.Text.Json <see cref="IIdempotencyResultSerializer" />. It only ever serializes through the
///     <see cref="JsonTypeInfo{T}" /> the supplied options resolve, so it is Native-AOT- and trimming-safe: give it
///     options whose <c>TypeInfoResolver</c> is your source-generated <c>JsonSerializerContext</c> and every result type
///     listed there replays; a type the options cannot resolve is simply not replayed.
/// </summary>
internal sealed class JsonIdempotencyResultSerializer(JsonSerializerOptions options) : IIdempotencyResultSerializer
{
    public bool TrySerialize<TResult>(TResult result, out byte[] payload)
    {
        payload = [];
        if (!TryGetTypeInfo<TResult>(out var typeInfo)) return false;

        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(result, typeInfo);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return false;
        }
    }

    public bool TryDeserialize<TResult>(byte[] payload, [MaybeNullWhen(false)] out TResult result)
    {
        result = default;
        if (!TryGetTypeInfo<TResult>(out var typeInfo)) return false;

        try
        {
            var value = JsonSerializer.Deserialize(payload, typeInfo);
            if (value is null && default(TResult) is not null) return false;

            result = value!;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return false;
        }
    }

    // TryGetTypeInfo rather than GetTypeInfo: an unregistered type is an expected "cannot replay", not an error.
    private bool TryGetTypeInfo<TResult>(out JsonTypeInfo<TResult> typeInfo)
    {
        typeInfo = null!;
        try
        {
            if (!options.TryGetTypeInfo(typeof(TResult), out var untyped) || untyped is not JsonTypeInfo<TResult> typed)
                return false;

            typeInfo = typed;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
