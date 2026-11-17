using System.Buffers.Binary;

namespace NetDM.Services.Research;

/// <summary>
/// Append-only durable journal of one round's research ledger. Each record is
/// <c>[length:int32][crc32:uint32][payload]</c> after an 8-byte header. <see cref="Append"/> returns
/// only after the operating system flushed the bytes to the device, so whatever the ledger acknowledged
/// survives a crash. A torn tail, the only damage an interrupted append can leave, is cut off by
/// <see cref="Repair"/>; everything before it was acknowledged and is replayed.
///
/// Damage an interrupted append cannot explain — a whole record header with an impossible length, or a
/// whole record that fails its checksum — is not treated as a tail: the bytes after it were acknowledged
/// too, so shortening the file would lose confirmed records. Such a journal raises
/// <see cref="ResearchJournalCorruptException"/> from the constructor and is left byte for byte as it was.
///
/// Opening only reads. The file changes when its reader has accepted what it replayed and calls
/// <see cref="Repair"/> or appends: a journal that is then refused (another round, a newer format)
/// stays byte for byte as it was, torn tail included.
/// </summary>
public sealed class ResearchJournal : IDisposable
{
    public const int MaxRecordBytes = 64 * 1024;
    private static readonly byte[] Header = "NDMRJ001"u8.ToArray();
    private readonly FileStream _stream;
    private readonly long _validLength;
    private bool _repaired;
    private bool _faulted;

    public ResearchJournal(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        // No buffer inside the stream: an append is one write of the whole batch, and a write the device
        // refused must not stay behind in memory to be written when the stream is closed.
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 0, FileOptions.None);
        try
        {
            Recovered = Read(out _validLength, out var reason);
            TruncatedBytes = Math.Max(0, _stream.Length - _validLength);
            TruncationReason = reason;
        }
        catch
        {
            _stream.Dispose(); // Not a research journal: the file is released as it was.
            throw;
        }
    }

    public string Path { get; }
    /// <summary>Payloads of every complete record, oldest first.</summary>
    public IReadOnlyList<byte[]> Recovered { get; }
    /// <summary>Bytes after the last complete record: cut off by <see cref="Repair"/>, never replayed.</summary>
    public long TruncatedBytes { get; }
    public string? TruncationReason { get; }
    public long Length => _stream.Length;

    /// <summary>
    /// Makes the file ready for appends: cuts off the torn tail and writes the header of a new journal.
    /// Call it once the replayed records were accepted; the first append does it otherwise.
    /// </summary>
    public void Repair()
    {
        if (_repaired)
            return;
        if (_validLength < _stream.Length || _stream.Length < Header.Length)
        {
            _stream.SetLength(_validLength);
            if (_validLength == 0)
            {
                _stream.Position = 0;
                _stream.Write(Header);
            }
            _stream.Flush(true);
        }
        _stream.Position = _stream.Length;
        _repaired = true;
    }

    /// <summary>Writes the records and flushes them to the device; one flush covers the batch.</summary>
    public void Append(IReadOnlyList<byte[]> payloads)
    {
        if (_faulted)
            throw new IOException("The research journal failed earlier; writes stay blocked until it is reopened.");
        Repair();
        var size = 0;
        foreach (var payload in payloads)
        {
            if (payload.Length is 0 or > MaxRecordBytes)
                throw new ArgumentException("A research journal record must hold 1..64 KiB.");
            size += 8 + payload.Length;
        }
        var buffer = new byte[size];
        var offset = 0;
        foreach (var payload in payloads)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), Crc32(payload));
            payload.CopyTo(buffer, offset + 8);
            offset += 8 + payload.Length;
        }
        try
        {
            _stream.Write(buffer);
            _stream.Flush(true);
        }
        catch
        {
            // What reached the disk is unknown: a partial record is cut off by the next open, a complete
            // one is replayed. Nothing more is appended through this handle.
            _faulted = true;
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    private List<byte[]> Read(out long validLength, out string? reason)
    {
        var records = new List<byte[]>();
        reason = null;
        validLength = 0;
        _stream.Position = 0;
        var length = _stream.Length;
        // Replay reads through its own buffer and never asks for bytes past the end of the file: reading
        // what was written must not depend on the region the next append goes to.
        var buffer = new byte[(int)Math.Clamp(length, 1, 64 * 1024)];
        int start = 0, end = 0;
        long fetched = 0;
        bool Fill(Span<byte> target)
        {
            while (target.Length > 0)
            {
                if (start == end)
                {
                    var want = (int)Math.Min(buffer.Length, length - fetched);
                    var got = want > 0 ? _stream.Read(buffer, 0, want) : 0;
                    if (got <= 0)
                        return false;
                    fetched += got;
                    start = 0;
                    end = got;
                }
                var take = Math.Min(target.Length, end - start);
                buffer.AsSpan(start, take).CopyTo(target);
                start += take;
                target = target[take..];
            }
            return true;
        }
        var header = new byte[Header.Length];
        if (length < Header.Length || !Fill(header))
        {
            reason = length == 0 ? null : "short_header";
            return records;
        }
        if (!header.AsSpan().SequenceEqual(Header))
            throw new InvalidDataException($"'{Path}' is not a netDM research journal.");
        validLength = Header.Length;
        var prefix = new byte[8];
        while (true)
        {
            if (length - validLength == 0)
                return records;
            if (length - validLength < 8 || !Fill(prefix))
            {
                reason = "torn_prefix";
                return records;
            }
            var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
            // A whole header was read, so these eight bytes are the ones an append wrote. A length no
            // append ever writes means the record bytes were damaged, not that the append was cut short:
            // the records behind it were acknowledged and must not be thrown away.
            if (size is <= 0 or > MaxRecordBytes)
                throw new ResearchJournalCorruptException(Path, "invalid_length", validLength, records.Count, length);
            var payload = new byte[size];
            if (!Fill(payload))
            {
                reason = "torn_record";
                return records;
            }
            // The whole payload is present, so this is not a partial append either. Its checksum failing
            // means the bytes changed after they were flushed.
            if (Crc32(payload) != crc)
                throw new ResearchJournalCorruptException(Path, "checksum", validLength, records.Count, length);
            records.Add(payload);
            validLength += 8 + size;
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    /// <summary>CRC-32 (IEEE 802.3), the checksum of zip and PNG.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
