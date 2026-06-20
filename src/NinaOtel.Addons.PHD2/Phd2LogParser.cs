using System.Globalization;
using System.Text.RegularExpressions;

namespace NinaOtel.Addons.PHD2;

internal static class Phd2LogParser
{
    private const string Source = "phd2";
    private const int FullTimestampPrefixLength = 23;
    private const int TimeOfDayTimestampPrefixLength = 12;
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
