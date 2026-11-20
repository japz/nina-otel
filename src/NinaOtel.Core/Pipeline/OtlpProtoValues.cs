namespace NinaOtel.Core.Pipeline;

internal static class OtlpProtoValues
{
    public static void WriteKeyValue(OtlpProtoWriter writer, int fieldNumber, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteMessage(fieldNumber, keyValue =>
        {
            keyValue.WriteString(1, key);
            keyValue.WriteMessage(2, anyValue => WriteAnyValue(anyValue, value));
        });
    }

    public static void WriteAnyValue(OtlpProtoWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteString(1, text);
                break;
            case bool boolean:
                writer.WriteBool(2, boolean);
                break;
            case byte number:
                writer.WriteSignedVarint(3, number);
                break;
            case sbyte number:
                writer.WriteSignedVarint(3, number);
                break;
            case short number:
                writer.WriteSignedVarint(3, number);
                break;
            case ushort number:
                writer.WriteSignedVarint(3, number);
                break;
            case int number:
                writer.WriteSignedVarint(3, number);
                break;
            case uint number:
                writer.WriteSignedVarint(3, number);
                break;
            case long number:
                writer.WriteSignedVarint(3, number);
                break;
            case ulong number when number <= long.MaxValue:
                writer.WriteSignedVarint(3, (long)number);
                break;
            case float number:
                writer.WriteDouble(4, number);
                break;
            case double number:
                writer.WriteDouble(4, number);
                break;
            case decimal number:
                writer.WriteDouble(4, (double)number);
                break;
            default:
                writer.WriteString(1, value.ToString() ?? string.Empty);
                break;
        }
    }

    public static ulong ToUnixNanoseconds(DateTimeOffset timestamp) =>
        (ulong)((timestamp.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100);
}
