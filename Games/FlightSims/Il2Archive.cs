using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VisualControlsBinder.Games;

internal static class Il2Archive
{
    private const int FileHeaderSize = 32;

    internal sealed record Entry(int Offset, int Length);

    public static string? ReadText(string archive, string path)
    {
        if (!File.Exists(archive))
            return null;
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadEntries(file, entry => entry == path).TryGetValue(path, out var item) ? ReadText(file, item) : null;
    }

    // Read the bounded archive index, then seek directly to the small text resources the caller needs.
    public static Dictionary<string, Entry> ReadEntries(FileStream file, Predicate<string> include)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        Span<byte> header = stackalloc byte[64];
        if (file.Length < header.Length)
            return entries;
        file.ReadExactly(header);
        int size = Number(header, 40);
        int start = Number(header, 60);
        if (!header[..4].SequenceEqual("S16R"u8) || !header.Slice(32, 8).SequenceEqual("STRMFAT?"u8)
            || size is <= 0 or > 16 * 1024 * 1024 || start != header.Length + size || start > file.Length)
            return entries;

        var table = new byte[size];
        file.ReadExactly(table);
        int position = 0;
        while (position <= table.Length - FileHeaderSize)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(position);
            int nameSize = Number(entry, 24);
            if (nameSize <= 0 || nameSize > entry.Length - FileHeaderSize || entry[FileHeaderSize + nameSize - 1] != 0)
                return [];
            if (entry[..4].SequenceEqual("FILE"u8))
            {
                int offset = Number(entry, 8);
                int length = Number(entry, 12);
                if (offset >= start && length is > 0 and <= 512 * 1024 && (long)offset + FileHeaderSize + length <= file.Length)
                {
                    string path = Encoding.UTF8.GetString(entry.Slice(FileHeaderSize, nameSize - 1));
                    if (include(path))
                        entries.TryAdd(path, new Entry(offset, length));
                }
            }
            else if (!entry[..4].SequenceEqual("DIR!"u8))
            {
                return [];
            }
            position += FileHeaderSize + nameSize;
        }
        return position == table.Length ? entries : [];
    }

    public static string? ReadText(FileStream file, Entry entry)
    {
        file.Position = entry.Offset;
        Span<byte> header = stackalloc byte[FileHeaderSize];
        file.ReadExactly(header);
        if (!header[..8].SequenceEqual("STRMFILE"u8) || Number(header, 8) != entry.Length)
            return null;
        var text = new byte[entry.Length];
        file.ReadExactly(text);
        return Encoding.UTF8.GetString(text);
    }

    private static int Number(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
}
