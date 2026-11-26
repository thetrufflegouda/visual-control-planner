using System.Buffers.Binary;
using System.IO;
using System.Text;
using ZstdSharp;

namespace VisualControlsBinder.Games;

internal static class WarThunderArchive
{
    private const int MaximumArchiveSize = 128 * 1024 * 1024;

    public static Dictionary<string, string> ReadTexts(string path, Func<string, bool> select)
    {
        var bytes = ReadArchive(path);
        int names = Offset(bytes, 0), entries = Offset(bytes, 16), count = Offset(bytes, 4);
        if (count != Offset(bytes, 20) || count > (bytes.Length - names) / 8 || count > (bytes.Length - entries) / 16)
            throw new InvalidDataException($"Invalid file table in {Path.GetFileName(path)}.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int nameOffset = Offset(bytes, names + i * 8);
            var nameBytes = bytes.AsSpan(nameOffset, Math.Min(512, bytes.Length - nameOffset));
            int end = nameBytes.IndexOf((byte)0);
            if (end <= 0)
                continue;
            string name = Encoding.UTF8.GetString(nameBytes[..end]);
            if (!select(name))
                continue;
            int entry = entries + i * 16;
            int offset = Offset(bytes, entry), size = Offset(bytes, entry + 4);
            if (size > bytes.Length - offset)
                throw new InvalidDataException($"Invalid entry {name} in {Path.GetFileName(path)}.");
            result.Add(name, Encoding.UTF8.GetString(bytes, offset, size).TrimStart('\uFEFF'));
        }

        return result;
    }

    private static byte[] ReadArchive(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        file.ReadExactly(header);
        var signature = header[..4];
        if (!signature.SequenceEqual("VRFx"u8) && !signature.SequenceEqual("VRFs"u8))
            throw new InvalidDataException($"Unsupported War Thunder archive format in {Path.GetFileName(path)}.");
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        int packedSize = (int)(flags & 0x3FFFFFFF);
        if (size is < 32 or > MaximumArchiveSize || packedSize > MaximumArchiveSize)
            throw new InvalidDataException($"Unexpected archive size in {Path.GetFileName(path)}.");

        if (signature.SequenceEqual("VRFx"u8))
        {
            Span<byte> extension = stackalloc byte[2];
            file.ReadExactly(extension);
            int extensionSize = BinaryPrimitives.ReadUInt16LittleEndian(extension);
            if (extensionSize < 8)
                throw new InvalidDataException($"Invalid archive extension in {Path.GetFileName(path)}.");
            file.Position = 16 + extensionSize;
        }

        var contents = new byte[size];
        if (packedSize == 0)
        {
            file.ReadExactly(contents);
            return contents;
        }
        if ((flags & 0x40000000) == 0 || packedSize > file.Length - file.Position)
            throw new InvalidDataException($"Unsupported compression in {Path.GetFileName(path)}.");

        var packed = new byte[packedSize];
        file.ReadExactly(packed);
        // Dagor's public dag_zstdObfuscate.h describes the two masked words at each end of the frame.
        ReadOnlySpan<uint> key = [0xAA55AA55, 0xF00FF00F, 0xAA55AA55, 0x12481248];
        for (int i = 0; i < 4 && packedSize >= 16; i++)
            Xor(packed.AsSpan(i * 4), key[i]);
        for (int i = 0; i < 4 && packedSize >= 32; i++)
            Xor(packed.AsSpan((packedSize / 4 - 4 + i) * 4), key[3 - i]);

        using var decompressor = new Decompressor();
        if (decompressor.Unwrap(packed, contents) != size)
            throw new InvalidDataException($"Invalid decompressed size in {Path.GetFileName(path)}.");
        return contents;
    }

    private static int Offset(byte[] bytes, int position)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
        if (value > bytes.Length)
            throw new InvalidDataException("A War Thunder archive file table points outside the archive.");
        return (int)value;
    }

    private static void Xor(Span<byte> bytes, uint key) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes) ^ key);
}
