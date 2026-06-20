using FluentAssertions;
using NinaOtel.Addons.NightSummary;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class NightSummaryLogParserTests
{
    private const string SourcePath = @"C:\NINA\Logs\nina.log";

    [Theory]
    [InlineData("NightSummary: Session started. SessionId=abc-123", NightSummaryLogEventKind.SessionStarted, "abc-123")]
    [InlineData("NightSummary: Session started. session.id=abc-123", NightSummaryLogEventKind.SessionStarted, "abc-123")]
    [InlineData("NightSummary: Session ended. SessionId=abc-123", NightSummaryLogEventKind.SessionEnded, "abc-123")]
    [InlineData("NightSummary: Stored camera info \u2014 9576\u00d76388px, 3.76\u00b5m, 530mm focal", NightSummaryLogEventKind.CameraInfoStored, null)]
    [InlineData("NightSummary: Equipment captured \u2014 Camera=ASI2600MM, Telescope=FSQ, Mount=EQ6-R", NightSummaryLogEventKind.EquipmentCaptured, null)]
    [InlineData("NightSummary: Event logged \u2014 RoofOpen: Safety monitor changed to Safe", NightSummaryLogEventKind.RoofOpen, null)]
    [InlineData("NightSummary: Event logged \u2014 RoofClosed: Safety monitor changed to Unsafe", NightSummaryLogEventKind.RoofClosed, null)]
    [InlineData("NightSummary: Event logged \u2014 AutoFocus: AutoFocus completed \u2014 Filter: Ha, Temp: -3.2\u00b0C, Position: 12456", NightSummaryLogEventKind.AutoFocusCompleted, null)]
    [InlineData("NightSummary: Event logged \u2014 MeridianFlip: Meridian flip completed", NightSummaryLogEventKind.MeridianFlip, null)]
    [InlineData("NightSummary: Synced TS grading for 12/16 images", NightSummaryLogEventKind.TargetSchedulerGradingSynced, null)]
    [InlineData("NightSummary: TS grading sync failed (non-fatal). Target Scheduler unavailable", NightSummaryLogEventKind.TargetSchedulerGradingFailed, null)]
    [InlineData("NightSummary: Generating report for session abc-123 ...", NightSummaryLogEventKind.ReportGenerating, "abc-123")]
    [InlineData("NightSummary: Delivering report to: email, discord", NightSummaryLogEventKind.ReportDelivering, null)]
    [InlineData("NightSummary: Report saved locally to C:\\Reports\\abc-123.html", NightSummaryLogEventKind.ReportSaved, null)]
    [InlineData("NightSummary: Report sent and session marked as complete", NightSummaryLogEventKind.ReportDelivered, null)]
    [InlineData("NightSummary: Dashboard upload successful", NightSummaryLogEventKind.ReportDelivered, null)]
    [InlineData("NightSummary: Failed to generate/send report. SMTP failed", NightSummaryLogEventKind.ReportFailed, null)]
    [InlineData("NightSummary: Failed to save report locally. Disk full", NightSummaryLogEventKind.ReportFailed, null)]
    [InlineData("NightSummary: Dashboard upload returned 500 \u2014 body", NightSummaryLogEventKind.ReportFailed, null)]
    [InlineData("NightSummary: Failed to upload to dashboard. timeout", NightSummaryLogEventKind.ReportFailed, null)]
    internal void TryParse_WhenLineContainsKnownNightSummaryPhrase_ReturnsExpectedKindAndSessionId(
        string message,
        NightSummaryLogEventKind expectedKind,
        string? expectedSessionId)
    {
        var parsed = NightSummaryLogParser.TryParse(
            $"2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|{message}",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(expectedKind);
        logEvent.SessionId.Should().Be(expectedSessionId);
    }

    [Theory]
    [InlineData("2026-06-18T22:00:00.1250|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123")]
    [InlineData("2026-06-18T22:00:00.125|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123")]
    [InlineData("2026-06-18 22:00:00.1250|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123")]
    [InlineData("2026-06-18 22:00:00.125|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123")]
    public void TryParse_WhenTimestampUsesSupportedFormat_CapturesUtcTimestamp(string line)
    {
        var parsed = NightSummaryLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_WhenLineIsRecognized_CapturesTimestampSourcePathOriginalLineLevelMessageAndSessionId()
    {
        const string line =
            "2026-06-18 22:00:00.1250|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123";

        var parsed = NightSummaryLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(NightSummaryLogEventKind.SessionStarted);
        parsedEvent.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
        parsedEvent.Source.Should().Be("night-summary");
        parsedEvent.SourcePath.Should().Be(SourcePath);
        parsedEvent.OriginalLine.Should().Be(line);
        parsedEvent.Level.Should().Be("INFO");
        parsedEvent.Message.Should().Be("NightSummary: Session started. SessionId=abc-123");
        parsedEvent.SessionId.Should().Be("abc-123");
    }

    [Fact]
    public void TryParse_WhenMessageContainsPipes_PreservesFullMessageRemainder()
    {
        const string message = "NightSummary: Delivering report to: email | discord | dashboard";
        const string line = $"2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|{message}";

        var parsed = NightSummaryLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(NightSummaryLogEventKind.ReportDelivering);
        logEvent.Message.Should().Be(message);
    }

    [Fact]
    public void TryParse_WhenReportCompletionMentionsSessionMarkedAsComplete_DoesNotExtractSessionId()
    {
        const string message = "NightSummary: Report sent and session marked as complete";

        var parsed = NightSummaryLogParser.TryParse(
            $"2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|{message}",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(NightSummaryLogEventKind.ReportDelivered);
        logEvent.SessionId.Should().BeNull();
    }

    [Fact]
    public void TryParse_WhenMalformedTimestampLineIsRecognized_UsesCurrentUtcTime()
    {
        var currentTime = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(currentTime);

        var parsed = NightSummaryLogParser.TryParse(
            "not-a-timestamp|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123",
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Timestamp.Should().Be(currentTime);
    }

    [Fact]
    public void TryParse_WhenLineIsContinuation_ReturnsFalse()
    {
        var parsed = NightSummaryLogParser.TryParse(
            "   at NightSummary.Report.Generate()",
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2026-06-18T22:00:00.0000|INFO|NightSummary.cs")]
    [InlineData("2026-06-18T22:00:00.0000 INFO NightSummary.cs Log 10 NightSummary: Session started. SessionId=abc-123")]
    [InlineData("2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|NINA: unrelated message")]
    [InlineData("2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Routine heartbeat")]
    public void TryParse_WhenLineIsMalformedUnrelatedOrUnrecognized_ReturnsFalse(string? line)
    {
        var parsed = NightSummaryLogParser.TryParse(
            line!,
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData("WARNING", "NightSummary: Session started. SessionId=abc-123", NightSummaryLogEventKind.Warning)]
    [InlineData("ERROR", "NightSummary: Session started. SessionId=abc-123", NightSummaryLogEventKind.Error)]
    internal void TryParse_WhenWarningOrErrorLineAlsoContainsInfoPhrase_ReturnsLevelKind(
        string level,
        string message,
        NightSummaryLogEventKind expectedKind)
    {
        var parsed = NightSummaryLogParser.TryParse(
            $"2026-06-18T22:00:00.0000|{level}|NightSummary.cs|Log|10|{message}",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(expectedKind);
        logEvent.SessionId.Should().Be("abc-123");
    }

    private sealed class FixedTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => currentTime;
    }
}
