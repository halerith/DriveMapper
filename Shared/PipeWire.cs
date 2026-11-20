using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DriveMapper;

internal static class PipeWire
{
    private const int MaximumPacket = 128 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumPacket) throw new InvalidDataException("Agent packet is too large.");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length), cancellation);
        await stream.WriteAsync(data, cancellation);
        await stream.FlushAsync(cancellation);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation);
        int length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaximumPacket) throw new InvalidDataException("Invalid Agent packet length.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellation);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty Agent packet.");
    }
}
