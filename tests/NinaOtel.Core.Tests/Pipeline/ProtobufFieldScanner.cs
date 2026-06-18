using System.Text;

namespace NinaOtel.Core.Tests.Pipeline;

internal static class ProtobufFieldScanner
{
    public static IReadOnlyList<ulong> FindVarints(byte[] payload, string path)
    {
        var values = new List<ulong>();
        Scan(payload, string.Empty, path, values, null, null, depth: 0);
        return values;
    }

    public static IReadOnlyList<byte[]> FindBytes(byte[] payload, string path)
    {
        var values = new List<byte[]>();
        Scan(payload, string.Empty, path, null, null, values, depth: 0);
        return values;
    }

    public static IReadOnlyList<ulong> FindFixed64(byte[] payload, string path)
    {
        var values = new List<ulong>();
        Scan(payload, string.Empty, path, null, values, null, depth: 0);
        return values;
    }

    public static IReadOnlyList<string> FindStrings(byte[] payload, string path) =>
        FindBytes(payload, path)
            .Select(static bytes => Encoding.UTF8.GetString(bytes))
            .ToArray();

    private static void Scan(
        ReadOnlySpan<byte> payload,
        string currentPath,
        string targetPath,
        List<ulong>? varintValues,
        List<ulong>? fixed64Values,
        List<byte[]>? bytesValues,
        int depth)
    {
        if (depth > 16)
        {
            return;
        }

        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var fieldNumber = key >> 3;
            var wireType = key & 0x7;
            var fieldPath = string.IsNullOrEmpty(currentPath)
                ? fieldNumber.ToString()
                : $"{currentPath}.{fieldNumber}";

            switch (wireType)
            {
                case 0:
                    var value = ReadVarint(payload, ref offset);
                    if (fieldPath == targetPath)
                    {
                        varintValues?.Add(value);
                    }
                    break;
                case 1:
                    if (offset + sizeof(ulong) > payload.Length)
                    {
                        return;
                    }

                    if (fieldPath == targetPath)
                    {
                        fixed64Values?.Add(BitConverter.ToUInt64(payload.Slice(offset, sizeof(ulong))));
                    }

                    offset += sizeof(ulong);
                    break;
                case 2:
                    var length = checked((int)ReadVarint(payload, ref offset));
                    if (length < 0 || offset + length > payload.Length)
                    {
                        return;
                    }

                    var child = payload.Slice(offset, length);
                    if (fieldPath == targetPath)
                    {
                        bytesValues?.Add(child.ToArray());
                    }

                    try
                    {
                        Scan(child, fieldPath, targetPath, varintValues, fixed64Values, bytesValues, depth + 1);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    offset += length;
                    break;
                default:
                    return;
            }
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> payload, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        while (offset < payload.Length)
        {
            var value = payload[offset++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidOperationException("Truncated protobuf varint.");
    }
}
