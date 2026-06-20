using System.Globalization;

namespace NinaOtel.Addons.TargetScheduler;

internal static class TargetSchedulerLogParser
{
    private const string Source = "target-scheduler";

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fff",
    ];

    internal static bool TryParse(
        string line,
        string sourcePath,
        TimeProvider timeProvider,
        out TargetSchedulerLogEvent? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split(['|'], 6);
        if (parts.Length != 6)
        {
            return false;
        }

        var level = parts[1];
        var message = parts[5];

        if (!TryGetKind(level, message, out var kind))
        {
            return false;
        }

        timeProvider ??= TimeProvider.System;
        parsed = new TargetSchedulerLogEvent(
            kind,
            ParseTimestamp(parts[0], timeProvider),
            Source,
            sourcePath ?? string.Empty,
            line,
            level,
            message);
        return true;
    }

    private static bool TryGetKind(string level, string message, out TargetSchedulerLogEventKind kind)
    {
        if (!Contains(message, "Target Scheduler"))
        {
            kind = default;
            return false;
        }

        if (EqualsIgnoreCase(level, "ERROR"))
        {
            kind = TargetSchedulerLogEventKind.Error;
            return true;
        }

        if (EqualsIgnoreCase(level, "WARNING"))
        {
            kind = TargetSchedulerLogEventKind.Warning;
            return true;
        }

        if (Contains(message, "planning run started"))
        {
            kind = TargetSchedulerLogEventKind.PlanningStarted;
            return true;
        }

        if (Contains(message, "planning run completed"))
        {
            kind = TargetSchedulerLogEventKind.PlanningCompleted;
            return true;
        }

        if (Contains(message, "selected target"))
        {
            kind = TargetSchedulerLogEventKind.TargetSelected;
            return true;
        }

        if (Contains(message, "plan started") ||
            Contains(message, "published target"))
        {
            kind = TargetSchedulerLogEventKind.PlanStarted;
            return true;
        }

        if (Contains(message, "plan stopped") ||
            Contains(message, "min-expire") ||
            Contains(message, "hard stop"))
        {
            kind = TargetSchedulerLogEventKind.PlanStopped;
            return true;
        }

        if (Contains(message, "image grade") ||
            Contains(message, "image grading"))
        {
            kind = TargetSchedulerLogEventKind.ImageGraded;
            return true;
        }

        kind = default;
        return false;
    }

    private static DateTimeOffset ParseTimestamp(string value, TimeProvider timeProvider)
    {
        if (DateTime.TryParseExact(
                value,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        }

        return timeProvider.GetUtcNow();
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
