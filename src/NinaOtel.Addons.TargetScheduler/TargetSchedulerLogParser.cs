using System.Globalization;
using System.Text.RegularExpressions;

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

    private static readonly Regex TargetKeyRegex = new(
        @"(?:^|[\s|])target=(?<value>[^\s|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FilterKeyRegex = new(
        @"(?:^|[\s|])filter=(?<value>[^\s|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ScoreKeyRegex = new(
        @"(?:^|[\s|])score=(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SelectedTargetWithFilterRegex = new(
        @"\bselected\s+target\s+(?<target>[^\r\n|]+?)\s+filter\s+(?<filter>[^\s\r\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SelectedTargetRegex = new(
        @"\bselected\s+target\s+(?<target>[^\r\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TargetAfterPhraseRegex = new(
        @"\b(?:plan\s+started\s+for|published\s+target|hard\s+stop\s+reached\s+for|min-expire\s+reached\s+for|plan\s+stopped\s+for)\s+(?<target>[^\r\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RejectedTargetRegex = new(
        @"\brejected\s+target\s+(?<target>[^\s\r\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ImageGradeStatusRegex = new(
        @"\bimage\s+grad(?:e|ing)\s+(?<status>[a-z][a-z0-9_-]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> KnownGradeStatuses = new(StringComparer.Ordinal)
    {
        "accepted",
        "rejected",
        "complete",
        "completed",
        "failed",
        "pass",
        "passed",
        "fail",
    };

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

        var targetName = ExtractTargetName(message);
        var filterName = ExtractFilterName(message);
        var gradeStatus = ExtractGradeStatus(message);
        var gradeScore = ExtractGradeScore(message);
        var stopReason = ExtractStopReason(message);

        timeProvider ??= TimeProvider.System;
        parsed = new TargetSchedulerLogEvent(
            kind,
            ParseTimestamp(parts[0], timeProvider),
            Source,
            sourcePath ?? string.Empty,
            line,
            level,
            message,
            targetName,
            filterName,
            gradeStatus,
            gradeScore,
            stopReason);
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

        if (Contains(message, "rejected target"))
        {
            kind = TargetSchedulerLogEventKind.Warning;
            return true;
        }

        kind = default;
        return false;
    }

    private static string? ExtractTargetName(string message) =>
        FirstGroupValue(TargetKeyRegex, message, "value") ??
        FirstGroupValue(SelectedTargetWithFilterRegex, message, "target") ??
        FirstGroupValue(SelectedTargetRegex, message, "target") ??
        FirstGroupValue(TargetAfterPhraseRegex, message, "target") ??
        FirstGroupValue(RejectedTargetRegex, message, "target");

    private static string? ExtractFilterName(string message) =>
        FirstGroupValue(FilterKeyRegex, message, "value") ??
        FirstGroupValue(SelectedTargetWithFilterRegex, message, "filter");

    private static string? ExtractGradeStatus(string message)
    {
        if (Contains(message, "rejected target"))
        {
            return "rejected";
        }

        var status = NormalizeToken(FirstGroupValue(ImageGradeStatusRegex, message, "status"));
        return status is not null && KnownGradeStatuses.Contains(status) ? status : null;
    }

    private static double? ExtractGradeScore(string message)
    {
        var value = FirstGroupValue(ScoreKeyRegex, message, "value");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;
    }

    private static string? ExtractStopReason(string message)
    {
        if (Contains(message, "hard stop"))
        {
            return "hard_stop";
        }

        if (Contains(message, "min-expire"))
        {
            return "min_expire";
        }

        return Contains(message, "plan stopped") ? "plan_stopped" : null;
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

    private static string? FirstGroupValue(Regex regex, string value, string groupName)
    {
        var match = regex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var group = match.Groups[groupName];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value)
            ? group.Value.Trim()
            : null;
    }

    private static string? NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
