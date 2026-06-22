using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Logs;
using Xunit;

namespace NinaOtel.Core.Tests.Logs;

public sealed class NinaLogParserTests
{
    [Fact]
    public void ParseLines_WhenInputStartsWithHeader_SkipsHeaderAndParsesRows()
    {
        var events = NinaLogParser.ParseLines(
        [
            "DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE",
            "2026-06-18 22:00:00.1250|INFO|NINA.Core.App|Start|42|Application started",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(NinaLogEventKind.ApplicationStarted);
        var expectedWallClock = new DateTime(2026, 6, 18, 22, 0, 0, 125);
        events[0].Timestamp.Should().Be(new DateTimeOffset(
            expectedWallClock,
            TimeZoneInfo.Local.GetUtcOffset(expectedWallClock)));
        events[0].Level.Should().Be("INFO");
        events[0].Source.Should().Be("NINA.Core.App");
        events[0].Member.Should().Be("Start");
        events[0].LineNumber.Should().Be(42);
        events[0].Message.Should().Be("Application started");
    }

    [Fact]
    public void ParseLines_WhenMessageContainsPipes_PreservesFullMessageRemainder()
    {
        const string message = "Successfully loaded plugin NinaOtel | version=0.1.0 | path=C:\\NINA\\Plugins";

        var events = NinaLogParser.ParseLines(
        [
            $"2026-06-18T22:00:00.0000|INFO|PluginLoader|Load|10|{message}",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(NinaLogEventKind.PluginLoaded);
        events[0].Message.Should().Be(message);
    }

    [Fact]
    public void ParseLines_WhenRowsAreMalformed_IgnoresThemWithoutThrowing()
    {
        IReadOnlyList<NinaLogEvent> events = [];
        Action parse = () => events = NinaLogParser.ParseLines(
        [
            "",
            "not a nina log line",
            "2026-06-18T22:00:00.0000|INFO|Application",
            "2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Start|12|Application started",
        ]);

        parse.Should().NotThrow();
        events.Should().ContainSingle(e => e.Kind == NinaLogEventKind.ApplicationStarted);
    }

    [Fact]
    public void ParseLines_WhenContinuationLinesFollowParsedEvent_AppendsThemToPreviousMessage()
    {
        var events = NinaLogParser.ParseLines(
        [
            "2026-06-18T22:00:00.0000|ERROR|NINA.Core.PluginLoader|Load|10|Could not load plugin NinaOtel",
            "System.InvalidOperationException: Plugin manifest is invalid",
            " ---> System.IO.FileNotFoundException: Manifest file is missing",
            "   at NINA.Core.PluginLoader.Load(String path)",
            "   at NINA.Core.App.Start()",
            "2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Stop|11|Application shutting down",
        ]);

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(NinaLogEventKind.PluginLoadFailed);
        events[0].Message.Should().Be(
            "Could not load plugin NinaOtel" + Environment.NewLine +
            "System.InvalidOperationException: Plugin manifest is invalid" + Environment.NewLine +
            " ---> System.IO.FileNotFoundException: Manifest file is missing" + Environment.NewLine +
            "   at NINA.Core.PluginLoader.Load(String path)" + Environment.NewLine +
            "   at NINA.Core.App.Start()");
        events[1].Kind.Should().Be(NinaLogEventKind.ApplicationClosing);
        events[1].Message.Should().Be("Application shutting down");
    }

    [Fact]
    public void ParseLines_WhenTimestampHasNoOffset_PreservesLocalWallClockOffset()
    {
        var events = NinaLogParser.ParseLines(
        [
            "2026-06-18T22:00:00.0000|INFO|NINA.Core.App|Start|10|Application started",
        ]);

        var localWallClock = new DateTime(2026, 6, 18, 22, 0, 0);
        events.Should().ContainSingle();
        events[0].Timestamp.Should().Be(new DateTimeOffset(
            localWallClock,
            TimeZoneInfo.Local.GetUtcOffset(localWallClock)));
    }

    [Fact]
    public void ParseLines_WhenErrorRowHasSemanticKind_PreservesSeveritySeparately()
    {
        var events = NinaLogParser.ParseLines(
        [
            "2026-06-18T22:00:00.0000|ERROR|NINA.Equipment.Camera|Disconnect|10|Camera disconnected",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(NinaLogEventKind.EquipmentDisconnected);
        events[0].Severity.Should().Be(TelemetrySeverity.Error);
    }

    [Theory]
    [InlineData("NINA.Sequencer.AutoFocus", "Run", "Started", NinaLogEventKind.AutofocusStarted)]
    [InlineData("NINA.Sequencer.AutoFocus", "Run", "Completed", NinaLogEventKind.AutofocusFinished)]
    [InlineData("NINA.Sequencer.MeridianFlip", "Run", "Started", NinaLogEventKind.MeridianFlipStarted)]
    [InlineData("NINA.Sequencer.MeridianFlip", "Run", "Completed", NinaLogEventKind.MeridianFlipFinished)]
    [InlineData("NINA.Sequencer.Sequence", "Run", "Started", NinaLogEventKind.SequenceStarted)]
    [InlineData("NINA.Sequencer.Sequence", "Run", "Completed", NinaLogEventKind.SequenceFinished)]
    internal void ParseLines_WhenSourceIdentifiesWorkflowAndMessageIsTerse_ClassifiesExpectedKind(
        string source,
        string member,
        string message,
        NinaLogEventKind expectedKind)
    {
        var events = NinaLogParser.ParseLines(
        [
            $"2026-06-18T22:00:00.0000|INFO|{source}|{member}|10|{message}",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("WARNING", "NINA.Core.App", "Warn", "Routine warning", NinaLogEventKind.Warning)]
    [InlineData("ERROR", "NINA.Core.App", "Error", "Routine error", NinaLogEventKind.Error)]
    [InlineData("FATAL", "NINA.Core.App", "Fatal", "Routine fatal", NinaLogEventKind.Fatal)]
    [InlineData("INFO", "NINA.Core.App", "Start", "Application started", NinaLogEventKind.ApplicationStarted)]
    [InlineData("INFO", "NINA.Core.App", "Stop", "Application shutting down", NinaLogEventKind.ApplicationClosing)]
    [InlineData("INFO", "NINA.Core.PluginLoader", "Load", "Successfully loaded plugin NinaOtel", NinaLogEventKind.PluginLoaded)]
    [InlineData("INFO", "NINA.Core.PluginLoader", "Load", "Failed to load plugin NinaOtel", NinaLogEventKind.PluginLoadFailed)]
    [InlineData("INFO", "NINA.Equipment.Camera", "Connect", "Camera connected", NinaLogEventKind.EquipmentConnected)]
    [InlineData("INFO", "NINA.Equipment.Mount", "Disconnect", "Mount disconnected", NinaLogEventKind.EquipmentDisconnected)]
    [InlineData("INFO", "NINA.Sequencer.Sequence", "Start", "Sequence started", NinaLogEventKind.SequenceStarted)]
    [InlineData("INFO", "NINA.Sequencer.Sequence", "Finish", "Sequence finished", NinaLogEventKind.SequenceFinished)]
    [InlineData("INFO", "NINA.Sequencer.AutoFocus", "Start", "Autofocus started", NinaLogEventKind.AutofocusStarted)]
    [InlineData("INFO", "NINA.Sequencer.AutoFocus", "Finish", "Autofocus finished", NinaLogEventKind.AutofocusFinished)]
    [InlineData("INFO", "NINA.Sequencer.MeridianFlip", "Start", "Meridian flip started", NinaLogEventKind.MeridianFlipStarted)]
    [InlineData("INFO", "NINA.Sequencer.MeridianFlip", "Finish", "Meridian flip finished", NinaLogEventKind.MeridianFlipFinished)]
    [InlineData("INFO", "NINA.Equipment.SafetyMonitor", "Update", "Safety monitor changed to Unsafe", NinaLogEventKind.SafetyUnsafe)]
    [InlineData("INFO", "NINA.Equipment.SafetyMonitor", "Update", "Safety monitor changed to Safe", NinaLogEventKind.SafetySafe)]
    internal void ParseLines_WhenKnownNinaEventIsPresent_ClassifiesExpectedKind(
        string level,
        string source,
        string member,
        string message,
        NinaLogEventKind expectedKind)
    {
        var events = NinaLogParser.ParseLines(
        [
            $"2026-06-18T22:00:00.0000|{level}|{source}|{member}|10|{message}",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(expectedKind);
        events[0].Severity.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Safety monitor is not safe")]
    [InlineData("Safety monitor is no longer safe")]
    [InlineData("Safety monitor has become unsafe")]
    internal void ParseLines_WhenSafetyMessageNegatesSafe_DoesNotEmitSafeKind(string message)
    {
        var events = NinaLogParser.ParseLines(
        [
            $"2026-06-18T22:00:00.0000|WARNING|NINA.Equipment.SafetyMonitor|Update|10|{message}",
        ]);

        events.Should().ContainSingle();
        events[0].Kind.Should().NotBe(NinaLogEventKind.SafetySafe);
    }
}
