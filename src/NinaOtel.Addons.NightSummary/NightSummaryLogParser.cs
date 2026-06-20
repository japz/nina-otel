using System.Globalization;

namespace NinaOtel.Addons.NightSummary;

internal static class NightSummaryLogParser
{
    private const string Source = "night-summary";

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
        out NightSummaryLogEvent? parsed)
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
        parsed = new NightSummaryLogEvent(
            kind,
            ParseTimestamp(parts[0], timeProvider),
            Source,
            sourcePath ?? string.Empty,
            line,
            level,
            message,
            ExtractSessionId(message));
        return true;
    }

    private static bool TryGetKind(string level, string message, out NightSummaryLogEventKind kind)
    {
        if (!Contains(message, "NightSummary:"))
        {
            kind = default;
            return false;
        }

        if (EqualsIgnoreCase(level, "ERROR"))
        {
            kind = NightSummaryLogEventKind.Error;
            return true;
        }

        if (EqualsIgnoreCase(level, "WARNING"))
        {
            kind = NightSummaryLogEventKind.Warning;
            return true;
        }

        if (Contains(message, "Session started"))
        {
            kind = NightSummaryLogEventKind.SessionStarted;
            return true;
        }

        if (Contains(message, "Session ended"))
        {
            kind = NightSummaryLogEventKind.SessionEnded;
            return true;
        }

        if (Contains(message, "Stored camera info"))
        {
            kind = NightSummaryLogEventKind.CameraInfoStored;
            return true;
        }

        if (Contains(message, "Equipment captured"))
        {
            kind = NightSummaryLogEventKind.EquipmentCaptured;
            return true;
        }

        if (Contains(message, "RoofOpen:"))
        {
            kind = NightSummaryLogEventKind.RoofOpen;
            return true;
        }

        if (Contains(message, "RoofClosed:"))
        {
            kind = NightSummaryLogEventKind.RoofClosed;
            return true;
        }

        if (Contains(message, "AutoFocus completed"))
        {
            kind = NightSummaryLogEventKind.AutoFocusCompleted;
            return true;
        }

        if (Contains(message, "Meridian flip completed"))
        {
            kind = NightSummaryLogEventKind.MeridianFlip;
            return true;
        }

        if (Contains(message, "Synced TS grading for"))
        {
            kind = NightSummaryLogEventKind.TargetSchedulerGradingSynced;
            return true;
        }

        if (Contains(message, "TS grading sync failed"))
        {
            kind = NightSummaryLogEventKind.TargetSchedulerGradingFailed;
            return true;
        }

        if (Contains(message, "Generating report for session"))
        {
            kind = NightSummaryLogEventKind.ReportGenerating;
            return true;
        }

        if (Contains(message, "Delivering report to:"))
        {
            kind = NightSummaryLogEventKind.ReportDelivering;
            return true;
        }

        if (Contains(message, "Report saved locally to"))
        {
            kind = NightSummaryLogEventKind.ReportSaved;
            return true;
        }

        if (Contains(message, "Report sent and session marked as complete"))
        {
            kind = NightSummaryLogEventKind.ReportDelivered;
            return true;
        }

        if (Contains(message, "Dashboard upload successful"))
        {
            kind = NightSummaryLogEventKind.ReportDelivered;
            return true;
        }

        if (Contains(message, "Failed to generate/send report") ||
            Contains(message, "Failed to save report locally") ||
            Contains(message, "Dashboard upload returned") ||
            Contains(message, "Failed to upload to dashboard"))
        {
            kind = NightSummaryLogEventKind.ReportFailed;
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

    private static string? ExtractSessionId(string message)
    {
        var explicitSessionId = ReadTokenAfter(message, "SessionId=");
        if (!string.IsNullOrWhiteSpace(explicitSessionId))
        {
            return explicitSessionId;
        }

        var dottedSessionId = ReadTokenAfter(message, "session.id=");
        if (!string.IsNullOrWhiteSpace(dottedSessionId))
        {
            return dottedSessionId;
        }

        return ReadTokenAfter(message, "Generating report for session ");
    }

    private static string? ReadTokenAfter(string value, string marker)
    {
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        while (start < value.Length && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        if (start >= value.Length)
        {
            return null;
        }

        var end = start;
        while (end < value.Length &&
               !char.IsWhiteSpace(value[end]) &&
               value[end] is not ('.' or ','))
        {
            end++;
        }

        return end > start
            ? value[start..end].Trim()
            : null;
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
