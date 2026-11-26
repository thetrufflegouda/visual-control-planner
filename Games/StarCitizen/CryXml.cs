using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

internal static class CryXml
{
    public static XElement Read(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("CryXmlB\0"u8))
        {
            using var stream = new MemoryStream(bytes);
            return XElement.Load(stream);
        }

        if (bytes.Length < 44 || ReadInt(bytes, 8) != bytes.Length)
            throw new InvalidDataException("Invalid Star Citizen control catalog header.");
        int nodeOffset = ReadInt(bytes, 12);
        int nodeCount = ReadInt(bytes, 16);
        int attributeOffset = ReadInt(bytes, 20);
        int attributeCount = ReadInt(bytes, 24);
        int childOffset = ReadInt(bytes, 28);
        int childCount = ReadInt(bytes, 32);
        int stringOffset = ReadInt(bytes, 36);
        int stringSize = ReadInt(bytes, 40);

        if (nodeCount <= 0 || nodeCount > 20_000 ||
            !Fits(nodeOffset, nodeCount, 28) || !Fits(attributeOffset, attributeCount, 8) ||
            !Fits(childOffset, childCount, 4) || !Fits(stringOffset, stringSize, 1))
            throw new InvalidDataException("Invalid Star Citizen control catalog tables.");

        return ReadNode(0, 0);

        bool Fits(int start, int count, int stride) => start >= 44 && count >= 0 &&
            start + (long)count * stride <= bytes.Length;

        string ReadString(int offset)
        {
            if (offset < 0 || offset >= stringSize)
                throw new InvalidDataException("Invalid Star Citizen control catalog string.");
            var data = bytes.AsSpan(stringOffset + offset, stringSize - offset);
            int length = data.IndexOf((byte)0);
            if (length < 0)
                throw new InvalidDataException("Unterminated Star Citizen control catalog string.");
            return Encoding.UTF8.GetString(data[..length]);
        }

        XElement ReadNode(int index, int depth)
        {
            if (index < 0 || index >= nodeCount || depth > 64)
                throw new InvalidDataException("Invalid Star Citizen control catalog structure.");
            int node = nodeOffset + index * 28;
            var element = new XElement(ReadString(ReadInt(bytes, node)));
            int attributes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(node + 8));
            int children = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(node + 10));
            int firstAttribute = ReadInt(bytes, node + 16);
            int firstChild = ReadInt(bytes, node + 20);
            if (firstAttribute < 0 || firstAttribute > attributeCount - attributes ||
                firstChild < 0 || firstChild > childCount - children)
                throw new InvalidDataException("Invalid Star Citizen control catalog node.");
            for (int i = 0; i < attributes; i++)
            {
                int field = attributeOffset + (firstAttribute + i) * 8;
                element.SetAttributeValue(ReadString(ReadInt(bytes, field)), ReadString(ReadInt(bytes, field + 4)));
            }
            for (int i = 0; i < children; i++)
                element.Add(ReadNode(ReadInt(bytes, childOffset + (firstChild + i) * 4), depth + 1));
            return element;
        }
    }

    private static int ReadInt(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
