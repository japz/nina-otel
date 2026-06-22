using System.Globalization;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Logs;

public static class NinaLogParser
{
    private const string Header = "DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE";

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static IReadOnlyList<NinaLogEvent> ParseLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var events = new List<NinaLogEvent>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                string.Equals(line, Header, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseLine(line, out var parsed))
            {
                events.Add(parsed);
                continue;
            }

            if (events.Count > 0 && IsContinuationLine(line))
            {
                var previous = events[^1];
                events[^1] = previous with
                {
                    Message = previous.Message + Environment.NewLine + line,
                    RawLine = previous.RawLine + Environment.NewLine + line,
                };
            }
        }

        return events;
    }

    private static bool TryParseLine(string line, out NinaLogEvent parsed)
    {
        parsed = default!;

        var parts = line.Split(['|'], 6);
        if (parts.Length != 6)
        {
            return false;
        }

        if (!TryParseTimestamp(parts[0], out var timestamp) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
        {
            return false;
        }

        var level = parts[1].Trim();
        var source = parts[2].Trim();
        var member = parts[3].Trim();
        var message = parts[5];

        parsed = new NinaLogEvent(
            Classify(level, source, member, message),
            timestamp,
            level,
            source,
            member,
            lineNumber,
            message,
            line,
            ParseSeverity(level));
        return true;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (DateTime.TryParseExact(
                value.Trim(),
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            timestamp = new DateTimeOffset(
                DateTime.SpecifyKind(parsed, DateTimeKind.Local));
            return true;
        }

        timestamp = default;
        return false;
    }

    private static NinaLogEventKind Classify(string level, string source, string member, string message)
    {
        var context = string.Concat(source, " ", member, " ", message);

        if (HasPluginContext(context) &&
            (Contains(message, "failed to load") ||
             Contains(message, "failed loading") ||
             Contains(message, "could not load") ||
             Contains(message, "couldn't load") ||
             Contains(message, "cannot load") ||
             Contains(message, "load failed") ||
             Contains(message, "plugin load failed")))
        {
            return NinaLogEventKind.PluginLoadFailed;
        }

        if (Contains(message, "successfully loaded plugin"))
        {
            return NinaLogEventKind.PluginLoaded;
        }

        if (Contains(message, "application shutting down") ||
            Contains(message, "application closing") ||
            Contains(message, "application shutdown"))
        {
            return NinaLogEventKind.ApplicationClosing;
        }

        if (Contains(message, "application started") ||
            Contains(message, "application startup complete") ||
            Contains(message, "application startup completed"))
        {
            return NinaLogEventKind.ApplicationStarted;
        }

        if (HasSafetyContext(context))
        {
            if (IsUnsafeMessage(message))
            {
                return NinaLogEventKind.SafetyUnsafe;
            }

            if (IsSafeMessage(message))
            {
                return NinaLogEventKind.SafetySafe;
            }
        }

        if (HasMeridianFlipContext(context))
        {
            if (IsStartMessage(message))
            {
                return NinaLogEventKind.MeridianFlipStarted;
            }

            if (IsFinishMessage(message))
            {
                return NinaLogEventKind.MeridianFlipFinished;
            }
        }

        if (HasAutofocusContext(context))
        {
            if (IsStartMessage(message))
            {
                return NinaLogEventKind.AutofocusStarted;
            }

            if (IsFinishMessage(message))
            {
                return NinaLogEventKind.AutofocusFinished;
            }
        }

        if (HasSequenceContext(context))
        {
            if (IsStartMessage(message))
            {
                return NinaLogEventKind.SequenceStarted;
            }

            if (IsFinishMessage(message))
            {
                return NinaLogEventKind.SequenceFinished;
            }
        }

        if (HasEquipmentContext(context))
        {
            if (ContainsWholeWord(message, "disconnected"))
            {
                return NinaLogEventKind.EquipmentDisconnected;
            }

            if (ContainsWholeWord(message, "connected"))
            {
                return NinaLogEventKind.EquipmentConnected;
            }
        }

        if (EqualsIgnoreCase(level, "FATAL"))
        {
            return NinaLogEventKind.Fatal;
        }

        if (EqualsIgnoreCase(level, "ERROR"))
        {
            return NinaLogEventKind.Error;
        }

        if (EqualsIgnoreCase(level, "WARNING") || EqualsIgnoreCase(level, "WARN"))
        {
            return NinaLogEventKind.Warning;
        }

        return NinaLogEventKind.Unknown;
    }

    private static bool IsContinuationLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("at ", StringComparison.Ordinal) ||
               trimmed.StartsWith("in ", StringComparison.Ordinal) ||
               trimmed.StartsWith("--->", StringComparison.Ordinal) ||
               trimmed.StartsWith("--- End of inner exception stack trace ---", StringComparison.Ordinal) ||
               trimmed.StartsWith("System.", StringComparison.Ordinal) ||
               trimmed.StartsWith("Microsoft.", StringComparison.Ordinal) ||
               trimmed.Contains("Exception:", StringComparison.Ordinal);
    }

    private static TelemetrySeverity? ParseSeverity(string level)
    {
        if (EqualsIgnoreCase(level, "TRACE"))
        {
            return TelemetrySeverity.Trace;
        }

        if (EqualsIgnoreCase(level, "DEBUG"))
        {
            return TelemetrySeverity.Debug;
        }

        if (EqualsIgnoreCase(level, "INFO") || EqualsIgnoreCase(level, "INFORMATION"))
        {
            return TelemetrySeverity.Information;
        }

        if (EqualsIgnoreCase(level, "WARNING") || EqualsIgnoreCase(level, "WARN"))
        {
            return TelemetrySeverity.Warning;
        }

        if (EqualsIgnoreCase(level, "ERROR"))
        {
            return TelemetrySeverity.Error;
        }

        if (EqualsIgnoreCase(level, "FATAL"))
        {
            return TelemetrySeverity.Fatal;
        }

        return null;
    }

    private static bool HasPluginContext(string context) =>
        Contains(context, "plugin");

    private static bool HasEquipmentContext(string context) =>
        Contains(context, "equipment") ||
        Contains(context, "camera") ||
        Contains(context, "mount") ||
        Contains(context, "telescope") ||
        Contains(context, "focuser") ||
        Contains(context, "filterwheel") ||
        Contains(context, "filter wheel") ||
        Contains(context, "rotator") ||
        Contains(context, "dome") ||
        Contains(context, "flatdevice") ||
        Contains(context, "flat device") ||
        Contains(context, "weather") ||
        Contains(context, "guider") ||
        Contains(context, "switch") ||
        Contains(context, "safety monitor");

    private static bool HasSequenceContext(string context) =>
        Contains(context, "sequence") ||
        Contains(context, "sequencer");

    private static bool HasAutofocusContext(string context) =>
        ContainsAutofocus(context);

    private static bool HasMeridianFlipContext(string context) =>
        Contains(context, "meridian flip") ||
        Contains(context, "meridianflip");

    private static bool HasSafetyContext(string context) =>
        Contains(context, "safety") ||
        Contains(context, "safe monitor");

    private static bool ContainsAutofocus(string value) =>
        Contains(value, "autofocus") ||
        Contains(value, "auto focus");

    private static bool IsStartMessage(string message) =>
        !Contains(message, "not started") &&
        (ContainsWholeWord(message, "started") ||
         ContainsWholeWord(message, "starting") ||
         ContainsWholeWord(message, "begin") ||
         ContainsWholeWord(message, "began"));

    private static bool IsFinishMessage(string message) =>
        !ContainsWholeWord(message, "incomplete") &&
        (ContainsWholeWord(message, "finished") ||
         ContainsWholeWord(message, "completed") ||
         ContainsWholeWord(message, "complete"));

    private static bool IsUnsafeMessage(string message) =>
        ContainsWholeWord(message, "unsafe") ||
        Contains(message, "not safe") ||
        Contains(message, "no longer safe");

    private static bool IsSafeMessage(string message) =>
        !Contains(message, "not safe") &&
        !Contains(message, "no longer safe") &&
        !ContainsWholeWord(message, "unsafe") &&
        (Contains(message, "changed to safe") ||
         Contains(message, "is safe") ||
         Contains(message, "safe state") ||
         ContainsWholeWord(message, "safe"));

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWholeWord(string value, string expected)
    {
        var index = value.IndexOf(expected, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var startIsBoundary = index == 0 || !char.IsLetter(value[index - 1]);
            var end = index + expected.Length;
            var endIsBoundary = end == value.Length || !char.IsLetter(value[end]);
            if (startIsBoundary && endIsBoundary)
            {
                return true;
            }

            index = value.IndexOf(expected, index + expected.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
