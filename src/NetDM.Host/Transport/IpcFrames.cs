using System.Buffers.Binary;

namespace NetDM.Host.Transport;

internal sealed class IpcFrames(Stream stream, bool framed)
{
    public const int MaxBodyBytes = 48 * 1024;
    public const int LegacyMaxBodyBytes = 60 * 1024;
    private const int LegacyMaxRequestBytes = 64 * 1024;
    private readonly byte[] _input = new byte[4096];
    private readonly byte[] _header = new byte[4];
    private int _position, _length;

    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
    {
        if (!framed)
            return await ReadLineAsync(ct);

        if (await stream.ReadAsync(_header.AsMemory(0, 1), ct) == 0)
            return null;
        await stream.ReadExactlyAsync(_header.AsMemory(1), ct);
        var size = BinaryPrimitives.ReadUInt32BigEndian(_header);
        if (size is 0 or > MaxBodyBytes)
            throw new InvalidDataException("Invalid IPC frame length.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    private async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_position == _length)
            {
                _length = await stream.ReadAsync(_input, ct);
                _position = 0;
                if (_length == 0)
                    return line.Length == 0 ? null : FinishLine(line);
            }

            var end = Array.IndexOf(_input, (byte)'\n', _position, _length - _position);
            var count = (end < 0 ? _length : end) - _position;
            if (line.Length + count > LegacyMaxRequestBytes + 1)
                throw new InvalidDataException("Oversized development JSON line.");
            line.Write(_input, _position, count);
            _position += count;
            if (end < 0)
                continue;

            _position++;
            return FinishLine(line);
        }
    }

    private static byte[] FinishLine(MemoryStream line)
    {
        if (line.Length > LegacyMaxRequestBytes && line.GetBuffer()[line.Length - 1] != '\r')
            throw new InvalidDataException("Oversized development JSON line.");
        return line.ToArray();
    }

    // The caller serializes concurrent writers.
    public ValueTask WriteAsync(byte[] body, CancellationToken ct) => WriteBatchAsync([body], ct);

    /// <summary>
    /// Writes complete frames with one stream write. A peer that reads one pipe
    /// message per call then receives every queued reply at once.
    /// </summary>
    public async ValueTask WriteBatchAsync(IReadOnlyList<byte[]> bodies, CancellationToken ct)
    {
        var size = 0;
        foreach (var body in bodies)
            size += body.Length + (framed ? 4 : 1);
        var buffer = new byte[size];
        var offset = 0;
        foreach (var body in bodies)
        {
            if (framed)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), (uint)body.Length);
                offset += 4;
            }
            body.CopyTo(buffer, offset);
            offset += body.Length;
            if (!framed)
                buffer[offset++] = (byte)'\n';
        }
        await stream.WriteAsync(buffer, ct);
    }
}
