using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NinaOtel.Addons.PHD2;

internal static class Phd2LogParser
{
    private const string Source = "phd2";
    private const int FullTimestampPrefixLength = 23;
    private const int TimeOfDayTimestampPrefixLength = 12;
    private const int GuideSampleFrameIndex = 0;
    private const int GuideSampleTimeIndex = 1;
    private const int GuideSampleRaRawDistanceIndex = 5;
    private const int GuideSampleDecRawDistanceIndex = 6;
    private const int GuideSampleRaGuideDistanceIndex = 7;
    private const int GuideSampleDecGuideDistanceIndex = 8;
    private const int GuideSampleRaDurationIndex = 9;
    private const int GuideSampleRaDirectionIndex = 10;
    private const int GuideSampleDecDurationIndex = 11;
    private const int GuideSampleDecDirectionIndex = 12;
    private static readonly TimeSpan TimestampDateWindow = TimeSpan.FromHours(12);

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.fff",
    ];

    private static readonly Regex DebugLogPrefixPattern = new(
        @"^\d{2}:\d{2}:\d{2}\.\d{3}\s+\d{2}\.\d{3}\s+\d+\s+",
        RegexOptions.CultureInvariant);

    private static readonly Regex GuideLogTimestampPattern = new(
        @"Guiding (?:Begins|Ends) at (?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DitherEventPattern = new(
        @"(?<!\w)Dither(?!\w)(?:\s*:|\s+(?:start(?:ed|ing)?|begin(?:s|ning)?|request(?:ed)?|command|by)|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StartGuidingEventPattern = CreateEventTokenPattern("StartGuiding");
    private static readonly Regex GuidingStoppedEventPattern = CreateEventTokenPattern("GuidingStopped");
    private static readonly Regex SettleBeginEventPattern = CreateEventTokenPattern("SettleBegin");
    private static readonly Regex SettleDoneEventPattern = CreateEventTokenPattern("SettleDone");

    internal static bool TryParseDebugLine(
        string line,
        string sourcePath,
        TimeProvider timeProvider,
        out Phd2LogEvent? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (!TryGetKind(line, out var kind))
        {
            return false;
        }

        timeProvider ??= TimeProvider.System;
        parsed = new Phd2LogEvent(
            kind,
            ParseTimestamp(line, timeProvider),
            Source,
            sourcePath ?? string.Empty,
            line);
        return true;
    }

    internal static bool TryParseGuideSampleLine(
        string line,
        DateTimeOffset sessionStartedAt,
        string sourcePath,
        out Phd2GuideSample? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var fields = SplitCsv(line);
        if (fields.Count <= GuideSampleDecRawDistanceIndex)
        {
            return false;
        }

        if (!double.TryParse(
                fields[GuideSampleTimeIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var sampleSeconds) ||
            !double.IsFinite(sampleSeconds))
        {
            return false;
        }

        if (!double.TryParse(
                fields[GuideSampleRaRawDistanceIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var raDistance) ||
            !double.IsFinite(raDistance))
        {
            return false;
        }

        if (!double.TryParse(
                fields[GuideSampleDecRawDistanceIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var decDistance) ||
            !double.IsFinite(decDistance))
        {
            return false;
        }

        if (!CanSquare(raDistance) ||
            !CanSquare(decDistance) ||
            !double.IsFinite((raDistance * raDistance) + (decDistance * decDistance)))
        {
            return false;
        }

        DateTimeOffset sampleTimestamp;
        try
        {
            sampleTimestamp = sessionStartedAt.AddSeconds(Math.Max(0, sampleSeconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        int? frame = null;
        if (int.TryParse(
                fields[GuideSampleFrameIndex],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedFrame))
        {
            frame = parsedFrame;
        }

        parsed = new Phd2GuideSample(
            sampleTimestamp,
            frame,
            raDistance,
            decDistance,
            TryParseGuidePulse(fields),
            Source,
            sourcePath ?? string.Empty,
            line);
        return true;
    }

    private static Phd2GuidePulse? TryParseGuidePulse(IReadOnlyList<string> fields)
    {
        if (fields.Count <= GuideSampleDecDirectionIndex)
        {
            return null;
        }

        if (!TryParseFiniteDouble(fields[GuideSampleRaGuideDistanceIndex], out var raPulseDistance) ||
            !TryParseFiniteDouble(fields[GuideSampleRaDurationIndex], out var raPulseDuration) ||
            !TryParseFiniteDouble(fields[GuideSampleDecGuideDistanceIndex], out var decPulseDistance) ||
            !TryParseFiniteDouble(fields[GuideSampleDecDurationIndex], out var decPulseDuration))
        {
            return null;
        }

        return new Phd2GuidePulse(
            raPulseDistance,
            raPulseDuration,
            fields[GuideSampleRaDirectionIndex].Trim(),
            decPulseDistance,
            decPulseDuration,
            fields[GuideSampleDecDirectionIndex].Trim());
    }

    private static bool TryParseFiniteDouble(string value, out double parsed) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed) &&
        double.IsFinite(parsed);

    private static bool CanSquare(double value) => double.IsFinite(value * value);

    private static bool TryGetKind(string line, out Phd2LogEventKind kind)
    {
        var message = StripLogPrefix(line);

        if (Contains(message, "capture failed") ||
            Contains(message, "camera error") ||
            Contains(message, "failed to start exposure") ||
            Contains(message, "StartExposure failed") ||
            Contains(message, "exposure failed") ||
            Contains(message, "cannot capture single frame when camera is not connected"))
        {
            kind = Phd2LogEventKind.CaptureError;
            return true;
        }

        if (StartsWith(message, "Guiding Begins"))
        {
            kind = Phd2LogEventKind.GuidingStarted;
            return true;
        }

        if (StartGuidingEventPattern.IsMatch(message))
        {
            kind = Phd2LogEventKind.GuidingStarted;
            return true;
        }

        if (StartsWith(message, "Guiding Stopped"))
        {
            kind = Phd2LogEventKind.GuidingStopped;
            return true;
        }

        if (GuidingStoppedEventPattern.IsMatch(message) || StartsWith(message, "Guiding Ends at"))
        {
            kind = Phd2LogEventKind.GuidingStopped;
            return true;
        }

        if (IsDitherEvent(message))
        {
            kind = Phd2LogEventKind.Dither;
            return true;
        }

        if (Contains(message, "Settling started"))
        {
            kind = Phd2LogEventKind.SettleStarted;
            return true;
        }

        if (SettleBeginEventPattern.IsMatch(message))
        {
            kind = Phd2LogEventKind.SettleStarted;
            return true;
        }

        if (Contains(message, "Settle complete"))
        {
            kind = Phd2LogEventKind.SettleCompleted;
            return true;
        }

        if (Contains(message, "Settling complete") ||
            SettleDoneEventPattern.IsMatch(message))
        {
            kind = Phd2LogEventKind.SettleCompleted;
            return true;
        }

        kind = default;
        return false;
    }

    private static DateTimeOffset ParseTimestamp(string line, TimeProvider timeProvider)
    {
        if (line.Length >= FullTimestampPrefixLength &&
            TryParseLocalTimestamp(
                line[..FullTimestampPrefixLength],
                TimestampFormats,
                timeProvider.LocalTimeZone,
                out var timestamp))
        {
            return timestamp;
        }

        var currentTime = timeProvider.GetUtcNow();

        if (line.Length >= TimeOfDayTimestampPrefixLength &&
            TimeSpan.TryParseExact(
                line[..TimeOfDayTimestampPrefixLength],
                @"hh\:mm\:ss\.fff",
                CultureInfo.InvariantCulture,
                out var timeOfDay))
        {
            var localNow = TimeZoneInfo.ConvertTime(currentTime, timeProvider.LocalTimeZone);
            var localTimeOfDay = new DateTime(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                timeOfDay.Hours,
                timeOfDay.Minutes,
                timeOfDay.Seconds,
                timeOfDay.Milliseconds,
                DateTimeKind.Unspecified);
            var timeOfDayTimestamp = ConvertLocalTimestamp(localTimeOfDay, timeProvider.LocalTimeZone);

            if (timeOfDayTimestamp - currentTime > TimestampDateWindow)
            {
                return ConvertLocalTimestamp(localTimeOfDay.AddDays(-1), timeProvider.LocalTimeZone);
            }

            if (currentTime - timeOfDayTimestamp > TimestampDateWindow)
            {
                return ConvertLocalTimestamp(localTimeOfDay.AddDays(1), timeProvider.LocalTimeZone);
            }

            return timeOfDayTimestamp;
        }

        var guideLogTimestamp = GuideLogTimestampPattern.Match(line);
        if (guideLogTimestamp.Success &&
            TryParseLocalTimestamp(
                guideLogTimestamp.Groups["timestamp"].Value,
                "yyyy-MM-dd HH:mm:ss",
                timeProvider.LocalTimeZone,
                out var guideTimestamp))
        {
            return guideTimestamp;
        }

        return currentTime;
    }

    private static string StripLogPrefix(string line)
    {
        var trimmed = line.TrimStart();

        if (HasFullTimestampPrefixShape(trimmed))
        {
            return trimmed[FullTimestampPrefixLength..].TrimStart();
        }

        var debugPrefix = DebugLogPrefixPattern.Match(trimmed);
        return debugPrefix.Success
            ? trimmed[debugPrefix.Length..].TrimStart()
            : trimmed;
    }

    private static bool IsDitherEvent(string message) =>
        Contains(message, "GuidingDithered") ||
        Contains(message, "PhdController::Dither begins") ||
        Contains(message, "Mount: notify guiding dithered") ||
        StartsWith(message, "dither:") ||
        StartsWith(message, "INFO: DITHER by") ||
        DitherEventPattern.IsMatch(message);

    private static bool Contains(string line, string value) =>
        line.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string line, string value) =>
        line.StartsWith(value, StringComparison.OrdinalIgnoreCase);

    private static bool HasFullTimestampPrefixShape(string line)
    {
        if (line.Length <= FullTimestampPrefixLength ||
            !char.IsWhiteSpace(line[FullTimestampPrefixLength]))
        {
            return false;
        }

        return IsDigitRange(line, start: 0, length: 4) &&
            line[4] == '-' &&
            IsDigitRange(line, start: 5, length: 2) &&
            line[7] == '-' &&
            IsDigitRange(line, start: 8, length: 2) &&
            (line[10] == ' ' || line[10] == 'T') &&
            IsDigitRange(line, start: 11, length: 2) &&
            line[13] == ':' &&
            IsDigitRange(line, start: 14, length: 2) &&
            line[16] == ':' &&
            IsDigitRange(line, start: 17, length: 2) &&
            line[19] == '.' &&
            IsDigitRange(line, start: 20, length: 3);
    }

    private static bool IsDigitRange(string value, int start, int length)
    {
        for (var index = start; index < start + length; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static Regex CreateEventTokenPattern(string eventName) =>
        new($@"(?<![\w]){Regex.Escape(eventName)}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var currentChar = line[index];
            if (currentChar == '"')
            {
                if (inQuotes &&
                    index + 1 < line.Length &&
                    line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (currentChar == ',' && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(currentChar);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static bool TryParseLocalTimestamp(
        string value,
        string format,
        TimeZoneInfo localTimeZone,
        out DateTimeOffset timestamp) =>
        TryParseLocalTimestamp(value, [format], localTimeZone, out timestamp);

    private static bool TryParseLocalTimestamp(
        string value,
        string[] formats,
        TimeZoneInfo localTimeZone,
        out DateTimeOffset timestamp)
    {
        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp))
        {
            timestamp = ConvertLocalTimestamp(localTimestamp, localTimeZone);
            return true;
        }

        timestamp = default;
        return false;
    }

    private static DateTimeOffset ConvertLocalTimestamp(DateTime localTimestamp, TimeZoneInfo localTimeZone)
    {
        var unspecifiedTimestamp = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified);
        return new DateTimeOffset(
            unspecifiedTimestamp,
            localTimeZone.GetUtcOffset(unspecifiedTimestamp)).ToUniversalTime();
    }
}
