using FluentAssertions;
using NinaOtel.Addons.TargetScheduler;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class TargetSchedulerLogParserTests
{
    private const string SourcePath = @"C:\NINA\Logs\nina.log";

    [Theory]
    [InlineData("2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started", TargetSchedulerLogEventKind.PlanningStarted)]
    [InlineData("2026-06-18T22:00:05.0000|INFO|Scheduler.cs|Run|20|Target Scheduler: planning run completed", TargetSchedulerLogEventKind.PlanningCompleted)]
    [InlineData("2026-06-18T22:00:10.0000|INFO|Scheduler.cs|Select|30|Target Scheduler: selected target M31 filter L", TargetSchedulerLogEventKind.TargetSelected)]
    [InlineData("2026-06-18T22:00:20.0000|INFO|Scheduler.cs|Plan|40|Target Scheduler: plan started for M31", TargetSchedulerLogEventKind.PlanStarted)]
    [InlineData("2026-06-18T22:00:30.0000|INFO|Scheduler.cs|Plan|50|Target Scheduler: hard stop reached for M31", TargetSchedulerLogEventKind.PlanStopped)]
    [InlineData("2026-06-18T22:01:00.0000|INFO|ImageGrader.cs|Grade|60|Target Scheduler: image grade accepted target=M31 score=0.92", TargetSchedulerLogEventKind.ImageGraded)]
    [InlineData("2026-06-18T22:02:00.0000|WARNING|Scheduler.cs|Run|70|Target Scheduler: rejected target M42 below horizon", TargetSchedulerLogEventKind.Warning)]
    [InlineData("2026-06-18T22:03:00.0000|ERROR|Scheduler.cs|Run|80|Target Scheduler: planning failed timeout", TargetSchedulerLogEventKind.Error)]
    internal void TryParse_WhenLineContainsKnownTargetSchedulerEvent_ReturnsExpectedKind(
        string line,
        TargetSchedulerLogEventKind expectedKind)
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("2026-06-18T22:00:00.1250|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started")]
    [InlineData("2026-06-18T22:00:00.125|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started")]
    [InlineData("2026-06-18 22:00:00.1250|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started")]
    [InlineData("2026-06-18 22:00:00.125|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started")]
    public void TryParse_WhenTimestampUsesSupportedFormat_CapturesUtcTimestamp(string line)
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Target Scheduler: published target M31", TargetSchedulerLogEventKind.PlanStarted)]
    [InlineData("Target Scheduler: plan stopped for M31", TargetSchedulerLogEventKind.PlanStopped)]
    [InlineData("Target Scheduler: min-expire reached for M31", TargetSchedulerLogEventKind.PlanStopped)]
    [InlineData("Target Scheduler: image grading complete target=M31 score=0.92", TargetSchedulerLogEventKind.ImageGraded)]
    internal void TryParse_WhenLineContainsAlternativeKnownPhrase_ReturnsExpectedKind(
        string message,
        TargetSchedulerLogEventKind expectedKind)
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            $"2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|{message}",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void TryParse_WhenLineIsRecognized_CapturesTimestampSourcePathOriginalLineLevelAndMessage()
    {
        const string line = "2026-06-18 22:00:00.1250|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started";

        var parsed = TargetSchedulerLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(TargetSchedulerLogEventKind.PlanningStarted);
        parsedEvent.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
        parsedEvent.Source.Should().Be("target-scheduler");
        parsedEvent.SourcePath.Should().Be(SourcePath);
        parsedEvent.OriginalLine.Should().Be(line);
        parsedEvent.Level.Should().Be("INFO");
        parsedEvent.Message.Should().Be("Target Scheduler: planning run started");
    }

    [Fact]
    public void TryParse_WhenMessageContainsPipes_PreservesFullMessageRemainder()
    {
        const string message = "Target Scheduler: planning run started | target=M31 | filter=L";
        const string line = $"2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|{message}";

        var parsed = TargetSchedulerLogParser.TryParse(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(TargetSchedulerLogEventKind.PlanningStarted);
        logEvent.Message.Should().Be(message);
    }

    [Fact]
    public void TryParse_WhenMalformedTimestampLineIsRecognized_UsesCurrentUtcTime()
    {
        var currentTime = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(currentTime);

        var parsed = TargetSchedulerLogParser.TryParse(
            "not-a-timestamp|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started",
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
        var parsed = TargetSchedulerLogParser.TryParse(
            "   at TargetScheduler.Planner.Run()",
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenRoutineTargetSchedulerInfoLineIsUnrecognized_ReturnsFalse()
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            "2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: evaluating target M31",
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenLineUsesDifferentCasing_MatchesCaseInsensitively()
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            "2026-06-18T22:00:00.0000|info|Scheduler.cs|Run|10|target scheduler: PLANNING RUN STARTED",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(TargetSchedulerLogEventKind.PlanningStarted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2026-06-18T22:00:00.0000|INFO|Scheduler.cs")]
    [InlineData("2026-06-18T22:00:00.0000 INFO Scheduler.cs Run 10 Target Scheduler: planning run started")]
    public void TryParse_WhenLineIsNullEmptyWhitespaceOrMalformed_ReturnsFalse(string? line)
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            line!,
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenWarningLineAlsoContainsInfoPhrase_ReturnsWarning()
    {
        var parsed = TargetSchedulerLogParser.TryParse(
            "2026-06-18T22:00:00.0000|WARNING|Scheduler.cs|Run|10|Target Scheduler: planning run started but degraded",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        logEvent!.Kind.Should().Be(TargetSchedulerLogEventKind.Warning);
    }

    private sealed class FixedTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => currentTime;
    }
}
