using System.Text;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpProtoWriter
{
    private const int VarintWireType = 0;
    private const int Fixed64WireType = 1;
    private const int LengthDelimitedWireType = 2;

    private readonly List<byte> buffer = [];

    public byte[] ToArray() => [.. buffer];

    public void WriteBool(int fieldNumber, bool value) =>
        WriteVarint(fieldNumber, value ? 1UL : 0UL);

    public void WriteBytes(int fieldNumber, byte[] value)
    {
        WriteTag(fieldNumber, LengthDelimitedWireType);
        WriteRawVarint((ulong)value.Length);
        buffer.AddRange(value);
    }

    public void WriteDouble(int fieldNumber, double value)
    {
        WriteTag(fieldNumber, Fixed64WireType);
        var bits = BitConverter.DoubleToUInt64Bits(value);
        WriteLittleEndianUInt64(bits);
    }

    public void WriteFixed64(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, Fixed64WireType);
        WriteLittleEndianUInt64(value);
    }

    private void WriteLittleEndianUInt64(ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            buffer.Add((byte)(value >> shift));
        }
    }

    public void WriteMessage(int fieldNumber, Action<OtlpProtoWriter> write)
    {
        var child = new OtlpProtoWriter();
        write(child);
        var payload = child.ToArray();
        WriteTag(fieldNumber, LengthDelimitedWireType);
        WriteRawVarint((ulong)payload.Length);
        buffer.AddRange(payload);
    }

    public void WriteSignedVarint(int fieldNumber, long value) =>
        WriteVarint(fieldNumber, unchecked((ulong)value));

    public void WriteString(int fieldNumber, string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        WriteTag(fieldNumber, LengthDelimitedWireType);
        WriteRawVarint((ulong)payload.Length);
        buffer.AddRange(payload);
    }

    public void WriteVarint(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, VarintWireType);
        WriteRawVarint(value);
    }

    private void WriteTag(int fieldNumber, int wireType) =>
        WriteRawVarint(((ulong)fieldNumber << 3) | (uint)wireType);

    private void WriteRawVarint(ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}
