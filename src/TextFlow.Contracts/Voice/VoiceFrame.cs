using System.Buffers.Binary;
using System.Text.Json;

namespace TextFlow.Contracts.Voice;

/// <summary>
/// Framing of <see cref="VoiceMessage"/> on a stream: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// Not thread-safe: one reader and one writer at a time per stream.
/// </summary>
public static class VoiceFrame
{
    /// <summary>Generous for text and control; anything bigger is a corrupt stream, not a real message.</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    private const int HeaderBytes = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, VoiceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Voice message too large ({payload.Length} bytes).");
        }

        var frame = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, HeaderBytes);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the other side closed the stream between messages.</summary>
    /// <exception cref="InvalidDataException">Truncated frame, bad length or JSON that is not a known message.</exception>
    public static async Task<VoiceMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderBytes];
        var read = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderBytes)
        {
            throw new InvalidDataException("Voice stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Invalid voice frame length {length}.");
        }

        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) < length)
        {
            throw new InvalidDataException("Voice stream ended inside a frame.");
        }

        try
        {
            return JsonSerializer.Deserialize<VoiceMessage>(payload, Json)
                ?? throw new InvalidDataException("Empty voice message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Voice frame is not a known message.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException("Voice frame is not a known message.", ex);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
