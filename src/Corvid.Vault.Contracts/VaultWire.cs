using System.Buffers.Binary;
using System.Text.Json;

namespace Corvid.Vault.Contracts;

/// <summary>
/// Framing on the Vault socket: a 4-byte big-endian length, then that many bytes of UTF-8 JSON. One request, one
/// response, in order, on a connection.
/// </summary>
public static class VaultWire
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public static ValueTask WriteRequestAsync(Stream stream, VaultRequest request, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, VaultJsonContext.Default.VaultRequest), cancellationToken);

    public static ValueTask WriteResponseAsync(Stream stream, VaultResponse response, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, VaultJsonContext.Default.VaultResponse), cancellationToken);

    /// <summary>Reads one request, or null at a clean end of stream.</summary>
    public static async ValueTask<VaultRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[]? frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        return frame is null ? null : JsonSerializer.Deserialize(frame, VaultJsonContext.Default.VaultRequest);
    }

    /// <summary>Reads one response, or null at a clean end of stream.</summary>
    public static async ValueTask<VaultResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[]? frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        return frame is null ? null : JsonSerializer.Deserialize(frame, VaultJsonContext.Default.VaultResponse);
    }

    private static async ValueTask WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        int read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < 4)
        {
            throw new EndOfStreamException("Connection closed inside a frame header.");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is outside 0..{MaxFrameBytes}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
