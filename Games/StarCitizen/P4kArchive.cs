using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace VisualControlsBinder.Games;

internal sealed class P4kArchive : IDisposable
{
    private const uint CentralEntry = 0x02014b50;
    private const uint LocalEntry = 0x04034b50;
    private const uint ZipEnd = 0x06054b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint Zip64End = 0x06064b50;
    private const int MaximumEntrySize = 32 * 1024 * 1024;
    private readonly FileStream stream;
    private readonly Dictionary<string, Entry> entries;

    private sealed record Entry(long Offset, int CompressedSize, int Size, ushort Method, ushort Flags);

    public P4kArchive(string path, params string[] members)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        try
        {
            entries = ReadIndex(members);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public byte[] Read(string member)
    {
        if (!entries.TryGetValue(member, out var entry))
            throw new InvalidDataException($"Star Citizen archive is missing {member}.");
        if ((entry.Flags & 1) != 0)
            throw new InvalidDataException($"Star Citizen archive entry is encrypted: {member}.");

        stream.Position = entry.Offset;
        Span<byte> header = stackalloc byte[30];
        stream.ReadExactly(header);
        if (U32(header, 0) != LocalEntry || U16(header, 8) != entry.Method)
            throw new InvalidDataException($"Invalid Star Citizen archive entry: {member}.");

        stream.Position += U16(header, 26) + U16(header, 28);
        var compressed = new byte[entry.CompressedSize];
        stream.ReadExactly(compressed);
        var result = entry.Method switch
        {
            0 => compressed,
            8 => Inflate(compressed, entry.Size),
            93 or 100 => DecompressZstandard(compressed, entry.Size),
            _ => throw new InvalidDataException($"Star Citizen archive uses compression method {entry.Method}.")
        };
        if (result.Length != entry.Size)
            throw new InvalidDataException($"Star Citizen archive entry has an unexpected size: {member}.");
        return result;
    }

    private Dictionary<string, Entry> ReadIndex(string[] members)
    {
        var (offset, size) = ReadDirectoryLocation();
        long end = offset + size;
        stream.Position = offset;

        // The archive has over a million entries. Inspect names of the requested lengths
        // and skip the unrelated metadata, especially the larger proprietary extra fields.
        var namesByLength = members.ToLookup(Encoding.ASCII.GetByteCount);
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Span<byte> header = stackalloc byte[46];
        var nameBuffer = new byte[members.Max(Encoding.ASCII.GetByteCount)];
        while (stream.Position <= end - header.Length && found.Count < members.Length)
        {
            stream.ReadExactly(header);
            if (U32(header, 0) != CentralEntry)
                throw new InvalidDataException("Invalid Star Citizen archive directory.");
            int nameLength = U16(header, 28);
            int extraLength = U16(header, 30);
            int commentLength = U16(header, 32);
            long next = stream.Position + nameLength + extraLength + commentLength;
            if (next > end)
                throw new InvalidDataException("Truncated Star Citizen archive directory.");

            if (namesByLength.Contains(nameLength))
            {
                stream.ReadExactly(nameBuffer.AsSpan(0, nameLength));
                string name = Encoding.ASCII.GetString(nameBuffer, 0, nameLength).Replace('\\', '/');
                if (members.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    var extra = new byte[extraLength];
                    stream.ReadExactly(extra);
                    found[name] = ReadEntry(header, extra);
                }
            }
            stream.Position = next;
        }
        return found;
    }

    private (long Offset, long Size) ReadDirectoryLocation()
    {
        // P4K appends a signature after the ZIP comment, so do not assume EOCD is at EOF.
        var tail = new byte[(int)Math.Min(stream.Length, 128 * 1024)];
        long tailOffset = stream.Length - tail.Length;
        stream.Position = tailOffset;
        stream.ReadExactly(tail);
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (U32(tail, index) != ZipEnd || index + 22 + U16(tail, index + 20) > tail.Length)
                continue;

            long size = U32(tail, index + 12);
            long offset = U32(tail, index + 16);
            if (index >= 20 && U32(tail, index - 20) == Zip64Locator)
            {
                long zip64Offset = I64(tail, index - 12);
                if (zip64Offset < 0 || zip64Offset > tailOffset + index - 76)
                    continue;
                Span<byte> record = new byte[56];
                stream.Position = zip64Offset;
                stream.ReadExactly(record);
                if (U32(record, 0) != Zip64End)
                    continue;
                size = I64(record, 40);
                offset = I64(record, 48);
            }
            if (offset >= 0 && size >= 46 && offset <= tailOffset + index - size)
                return (offset, size);
        }
        throw new InvalidDataException("Could not locate the Star Citizen archive directory.");
    }

    private static Entry ReadEntry(ReadOnlySpan<byte> header, ReadOnlySpan<byte> extra)
    {
        long size = U32(header, 24);
        long compressedSize = U32(header, 20);
        long offset = U32(header, 42);
        if (size == uint.MaxValue || compressedSize == uint.MaxValue || offset == uint.MaxValue)
        {
            var zip64 = ReadZip64Extra(extra);
            int index = 0;
            size = ReadExtended(size, zip64, ref index);
            compressedSize = ReadExtended(compressedSize, zip64, ref index);
            offset = ReadExtended(offset, zip64, ref index);
        }
        if (offset < 0 || size < 0 || size > MaximumEntrySize ||
            compressedSize < 0 || compressedSize > MaximumEntrySize)
            throw new InvalidDataException("Star Citizen archive entry is too large or has invalid metadata.");
        return new Entry(offset, (int)compressedSize, (int)size, U16(header, 10), U16(header, 8));
    }

    private static ReadOnlySpan<byte> ReadZip64Extra(ReadOnlySpan<byte> extra)
    {
        while (extra.Length >= 4)
        {
            int length = U16(extra, 2);
            if (extra.Length < 4 + length)
                break;
            if (U16(extra, 0) == 1)
                return extra.Slice(4, length);
            extra = extra[(4 + length)..];
        }
        throw new InvalidDataException("Star Citizen archive entry is missing ZIP64 metadata.");
    }

    private static long ReadExtended(long original, ReadOnlySpan<byte> extra, ref int index)
    {
        if (original != uint.MaxValue)
            return original;
        if (index + 8 > extra.Length)
            throw new InvalidDataException("Truncated Star Citizen ZIP64 metadata.");
        long value = I64(extra, index);
        index += 8;
        return value;
    }

    private static byte[] Inflate(byte[] compressed, int size)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var result = new byte[size];
        deflate.ReadExactly(result);
        if (deflate.ReadByte() >= 0)
            throw new InvalidDataException("Star Citizen archive entry exceeded its declared size.");
        return result;
    }

    private static byte[] DecompressZstandard(byte[] compressed, int size)
    {
        using var decompressor = new Decompressor();
        var result = new byte[size];
        if (decompressor.Unwrap(compressed, result) != size)
            throw new InvalidDataException("Star Citizen archive entry did not match its declared size.");
        return result;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);

    public void Dispose() => stream.Dispose();
}
