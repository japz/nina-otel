using System.Reflection;
using FluentAssertions;
using NinaOtel.Addons.PHD2;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class Phd2LogParserTests
{
    private const string SourcePath = @"C:\PHD2\PHD2_DebugLog.txt";
    private static readonly TimeZoneInfo UtcTimeZone =
        TimeZoneInfo.CreateCustomTimeZone("UTC", TimeSpan.Zero, "UTC", "UTC");
    private static readonly TimeZoneInfo PlusTwoTimeZone =
        TimeZoneInfo.CreateCustomTimeZone("UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");

    [Theory]
    [InlineData("2026-06-18 22:00:00.125 Guiding Begins", Phd2LogEventKind.GuidingStarted)]
    [InlineData("2026-06-18 22:00:05.000 Guiding Stopped", Phd2LogEventKind.GuidingStopped)]
    [InlineData("2026-06-18 22:00:10.000 Dither: started", Phd2LogEventKind.Dither)]
    [InlineData("2026-06-18 22:00:15.000 Settling started", Phd2LogEventKind.SettleStarted)]
    [InlineData("2026-06-18 22:00:20.500 Settle complete", Phd2LogEventKind.SettleCompleted)]
    [InlineData("2026-06-18 22:00:30.000 capture failed: star lost", Phd2LogEventKind.CaptureError)]
    [InlineData("2026-06-18 22:00:31.000 camera error: timeout", Phd2LogEventKind.CaptureError)]
    [InlineData("2026-06-18 22:00:35.000 GUIDING BEGINS", Phd2LogEventKind.GuidingStarted)]
    [InlineData("2026-06-18 22:00:36.000 guiding stopped", Phd2LogEventKind.GuidingStopped)]
    [InlineData("2026-06-18 22:00:37.000 dItHeR: started", Phd2LogEventKind.Dither)]
    [InlineData("2026-06-18 22:00:38.000 SETTLING STARTED", Phd2LogEventKind.SettleStarted)]
    [InlineData("2026-06-18 22:00:39.000 settle COMPLETE", Phd2LogEventKind.SettleCompleted)]
    [InlineData("2026-06-18 22:00:40.000 CAMERA ERROR: timeout", Phd2LogEventKind.CaptureError)]
    [InlineData("2026-06-18 22:01:00.000 StartGuiding", Phd2LogEventKind.GuidingStarted)]
    [InlineData("2026-06-18 22:01:05.000 GuidingStopped", Phd2LogEventKind.GuidingStopped)]
    [InlineData("2026-06-18 22:01:10.000 Guiding Ends at 2026-06-18 22:01:10", Phd2LogEventKind.GuidingStopped)]
    [InlineData("2026-06-18 22:01:15.000 SettleBegin", Phd2LogEventKind.SettleStarted)]
    [InlineData("2026-06-18 22:01:20.000 SettleDone", Phd2LogEventKind.SettleCompleted)]
    [InlineData("2026-06-18 22:01:25.000 Settling complete", Phd2LogEventKind.SettleCompleted)]
    [InlineData("2026-06-18 22:01:30.000 startguiding", Phd2LogEventKind.GuidingStarted)]
    [InlineData("2026-06-18 22:01:35.000 GUIDINGSTOPPED", Phd2LogEventKind.GuidingStopped)]
    [InlineData("2026-06-18 22:01:40.000 settlebegin", Phd2LogEventKind.SettleStarted)]
    [InlineData("2026-06-18 22:01:45.000 SETTLEDONE", Phd2LogEventKind.SettleCompleted)]
    internal void TryParseDebugLine_WhenLineContainsKnownDebugEvent_ReturnsExpectedKind(
        string line,
        Phd2LogEventKind expectedKind)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("22:00:00.125 00.000 1234 Guiding Begins", Phd2LogEventKind.GuidingStarted, 0, 125)]
    [InlineData("22:00:05.000 00.003 1234 Guiding Ends at 22:00:05", Phd2LogEventKind.GuidingStopped, 5, 0)]
    [InlineData("22:00:10.000 00.003 1234 evsrv: StartGuiding", Phd2LogEventKind.GuidingStarted, 10, 0)]
    [InlineData("22:00:15.000 00.003 1234 evsrv: GuidingStopped", Phd2LogEventKind.GuidingStopped, 15, 0)]
    [InlineData("22:00:18.000 00.003 1234 evsrv: {\"Event\":\"GuidingDithered\",\"Timestamp\":1718745246.000}", Phd2LogEventKind.Dither, 18, 0)]
    [InlineData("22:00:20.000 00.003 1234 evsrv: SettleBegin", Phd2LogEventKind.SettleStarted, 20, 0)]
    [InlineData("22:00:25.000 00.003 1234 evsrv: SettleDone", Phd2LogEventKind.SettleCompleted, 25, 0)]
    [InlineData("22:00:30.000 00.003 1234 Settling complete", Phd2LogEventKind.SettleCompleted, 30, 0)]
    internal void TryParseDebugLine_WhenLineUsesPhd2TimeOfDayPrefix_CombinesTimeWithCurrentUtcDate(
        string line,
        Phd2LogEventKind expectedKind,
        int expectedSecond,
        int expectedMillisecond)
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 21, 59, 59, TimeSpan.Zero));

        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(expectedKind);
        parsedEvent.Timestamp.Should().Be(
            new DateTimeOffset(2026, 6, 18, 22, 0, expectedSecond, expectedMillisecond, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("23:59:59.900 00.003 1234 GuidingStopped", "2026-06-18T00:00:10Z", "2026-06-17T23:59:59.900Z")]
    [InlineData("00:00:10.100 00.003 1234 StartGuiding", "2026-06-17T23:59:59Z", "2026-06-18T00:00:10.100Z")]
    public void TryParseDebugLine_WhenTimeOfDayPrefixCrossesMidnight_UsesNearestUtcDate(
        string line,
        string now,
        string expectedTimestamp)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse(now));

        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Timestamp.Should().Be(DateTimeOffset.Parse(expectedTimestamp));
    }

    [Theory]
    [InlineData("2026-06-18 22:00:10.000 Dither: started")]
    [InlineData("2026-06-18 22:00:11.000 evsrv: {\"Event\":\"GuidingDithered\",\"Timestamp\":1718745246.000}")]
    [InlineData("22:00:12.000 00.003 1234 dither started")]
    [InlineData("22:00:13.000 00.003 1234 PhdController::Dither begins")]
    [InlineData("22:00:14.000 00.003 1234 dither: size=5.00, dRA=-4.99 dDec=0.64")]
    [InlineData("INFO: DITHER by -4.987, 0.636, new lock pos = 636.680, 509.874")]
    [InlineData("22:00:15.000 00.003 1234 Mount: notify guiding dithered (-5.0, 0.6)")]
    public void TryParseDebugLine_WhenLineContainsDitherEvent_ReturnsDither(string line)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(Phd2LogEventKind.Dither);
    }

    [Theory]
    [InlineData("2026-06-18 22:00:10.000 Dither scale = 1.000")]
    [InlineData("2026-06-18 22:00:11.000 Dither RA only setting changed")]
    [InlineData("22:00:12.000 00.003 1234 Checking if Dither is enabled")]
    [InlineData("22:00:13.000 00.003 1234 reset dither spiral")]
    [InlineData("22:00:14.000 00.003 1234 Dither = both axes, Dither scale = 1.000")]
    public void TryParseDebugLine_WhenLineContainsDitherConfigurationOrStatus_ReturnsFalse(string line)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData("Guiding Begins at 2017-03-10 20:22:52", Phd2LogEventKind.GuidingStarted, "2017-03-10T20:22:52Z")]
    [InlineData("Guiding Ends at 2017-03-10 21:22:52", Phd2LogEventKind.GuidingStopped, "2017-03-10T21:22:52Z")]
    internal void TryParseDebugLine_WhenGuideLogLineContainsTimestamp_UsesGuideLogTimestamp(
        string line,
        Phd2LogEventKind expectedKind,
        string expectedTimestamp)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(expectedKind);
        parsedEvent.Timestamp.Should().Be(DateTimeOffset.Parse(expectedTimestamp));
    }

    [Fact]
    public void TryParseGuideSampleLine_WhenSampleTimeWouldOverflow_ReturnsFalseWithoutThrowing()
    {
        var act = () => Phd2LogParser.TryParseGuideSampleLine(
            "1,1e300,Mount,0.1,-0.2,3,4,0.5,-0.6,100,W,200,N,0,0,5000,30,0",
            DateTimeOffset.UnixEpoch,
            SourcePath,
            out _);

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [Fact]
    public void TryParseGuideSampleLine_WhenRowIsValid_ReturnsGuideSample()
    {
        var sessionStartedAt = DateTimeOffset.Parse("2026-06-18T22:00:00Z");

        var parsed = Phd2LogParser.TryParseGuideSampleLine(
            "1,1.500,\"Mount,USB\",0.1,-0.2,3,4,0.5,-0.6,100,W,200,N,0,0,5000,30,0",
            sessionStartedAt,
            SourcePath,
            out var sample);

        parsed.Should().BeTrue();
        sample.Should().NotBeNull();
        var guideSample = sample!;
        guideSample.Timestamp.Should().Be(DateTimeOffset.Parse("2026-06-18T22:00:01.500Z"));
        guideSample.RaDistancePixel.Should().Be(3);
        guideSample.DecDistancePixel.Should().Be(4);
        guideSample.Pulse.Should().NotBeNull();
        guideSample.Pulse!.RaDistancePixel.Should().Be(0.5);
        guideSample.Pulse.RaDurationMs.Should().Be(100);
        guideSample.Pulse.RaDirection.Should().Be("W");
        guideSample.Pulse.DecDistancePixel.Should().Be(-0.6);
        guideSample.Pulse.DecDurationMs.Should().Be(200);
        guideSample.Pulse.DecDirection.Should().Be("N");
        guideSample.Source.Should().Be("phd2");
        guideSample.SourcePath.Should().Be(SourcePath);
    }

    [Fact]
    public void TryParseGuideSampleLine_WhenPulseColumnsAreMissing_ReturnsGuideSampleWithoutPulse()
    {
        var sessionStartedAt = DateTimeOffset.Parse("2026-06-18T22:00:00Z");

        var parsed = Phd2LogParser.TryParseGuideSampleLine(
            "1,1.500,Mount,0.1,-0.2,3,4",
            sessionStartedAt,
            SourcePath,
            out var sample);

        parsed.Should().BeTrue();
        sample.Should().NotBeNull();
        var guideSample = sample!;
        guideSample.Timestamp.Should().Be(DateTimeOffset.Parse("2026-06-18T22:00:01.500Z"));
        guideSample.RaDistancePixel.Should().Be(3);
        guideSample.DecDistancePixel.Should().Be(4);
        guideSample.Pulse.Should().BeNull();
    }

    [Theory]
    [InlineData("1,1.000,Mount,0.1,-0.2,not-a-number,4,0.5,-0.6,100,W,200,N,0,0,5000,30,0")]
    [InlineData("1,1.000,Mount,0.1,-0.2,3,not-a-number,0.5,-0.6,100,W,200,N,0,0,5000,30,0")]
    [InlineData("1,1.000,Mount,0.1,-0.2,1e308,4,0.5,-0.6,100,W,200,N,0,0,5000,30,0")]
    [InlineData("1,1.000,Mount,0.1,-0.2,3,1e308,0.5,-0.6,100,W,200,N,0,0,5000,30,0")]
    public void TryParseGuideSampleLine_WhenDistancesAreMalformedOrTooLarge_ReturnsFalse(string line)
    {
        var act = () => Phd2LogParser.TryParseGuideSampleLine(
            line,
            DateTimeOffset.UnixEpoch,
            SourcePath,
            out _);

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-06-18 22:00:00.125 Guiding Begins", "2026-06-18T20:00:00.125Z")]
    [InlineData("Guiding Begins at 2026-06-18 22:00:00", "2026-06-18T20:00:00Z")]
    public void TryParseDebugLine_WhenLineTimestampHasNoOffset_TreatsItAsLocalWallClock(
        string line,
        string expectedTimestamp)
    {
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 6, 18, 20, 0, 1, TimeSpan.Zero),
            PlusTwoTimeZone);

        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Timestamp.Should().Be(DateTimeOffset.Parse(expectedTimestamp));
    }

    [Fact]
    public void TryParseDebugLine_WhenTimeOfDayTimestampHasNoOffset_UsesLocalCurrentDate()
    {
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 6, 18, 20, 0, 1, TimeSpan.Zero),
            PlusTwoTimeZone);

        var parsed = Phd2LogParser.TryParseDebugLine(
            "22:00:00.125 00.003 1234 GuidingStopped",
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Timestamp.Should().Be(DateTimeOffset.Parse("2026-06-18T20:00:00.125Z"));
    }

    [Theory]
    [InlineData("22:00:00.000 00.003 1234 StartGuidingNotAnEvent")]
    [InlineData("22:00:00.000 00.003 1234 NotGuidingStoppedYet")]
    [InlineData("22:00:00.000 00.003 1234 PreSettleBeginCheck")]
    [InlineData("22:00:00.000 00.003 1234 SettleDoneFlag=false")]
    public void TryParseDebugLine_WhenEventNameIsOnlyPartOfLargerToken_ReturnsFalse(string line)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParseDebugLine_WhenSettlingFailedLineIsSeen_ReturnsFalse()
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            "INFO: SETTLING STATE CHANGE, Settling failed",
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData("23:15:00.000 00.100 12345678 ASCOM_StartExposure failed: [80004005] Unspecified error")]
    [InlineData("23:15:05.000 05.000 12345678 SVB: exposure failed, giving up")]
    [InlineData("23:15:10.000 00.100 12345678 failed to start exposure: camera busy")]
    [InlineData("23:15:15.000 00.100 12345678 cannot capture single frame when camera is not connected")]
    public void TryParseDebugLine_WhenLineContainsCameraOrExposureFailure_ReturnsCaptureError(string line)
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            TimeProvider.System,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Kind.Should().Be(Phd2LogEventKind.CaptureError);
    }

    [Fact]
    public void TryParseDebugLine_IsInternalAndUsesNullableParsedOutParameter()
    {
        var method = typeof(Phd2LogParser).GetMethod(
            nameof(Phd2LogParser.TryParseDebugLine),
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.IsAssembly.Should().BeTrue();

        var parsedParameter = method.GetParameters().Single(static parameter => parameter.Name == "parsed");
        var nullability = new NullabilityInfoContext().Create(parsedParameter);

        nullability.WriteState.Should().Be(NullabilityState.Nullable);
    }

    [Fact]
    public void TryParseDebugLine_WhenInfoLineIsUnrecognized_ReturnsFalse()
    {
        var parsed = Phd2LogParser.TryParseDebugLine(
            "2026-06-18 22:00:00.125 INFO exposure started",
            SourcePath,
            TimeProvider.System,
            out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParseDebugLine_WhenLineIsRecognized_CapturesSourcePathOriginalLineAndTimestamp()
    {
        const string line = "2026-06-18 22:00:00.125 Guiding Begins";

        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Source.Should().Be("phd2");
        parsedEvent.SourcePath.Should().Be(SourcePath);
        parsedEvent.OriginalLine.Should().Be(line);
        parsedEvent.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
    }

    [Fact]
    public void TryParseDebugLine_WhenTimestampUsesSeparatorT_CapturesTimestamp()
    {
        const string line = "2026-06-18T22:00:00.125 Guiding Begins";

        var parsed = Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 125, TimeSpan.Zero));
    }

    [Fact]
    public void TryParseDebugLine_WhenTimestampPrefixIsMalformed_UsesCurrentUtcTime()
    {
        var currentTime = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(currentTime);

        var parsed = Phd2LogParser.TryParseDebugLine(
            "2026-99-99 99:99:99.999 Guiding Begins",
            SourcePath,
            timeProvider,
            out var logEvent);

        parsed.Should().BeTrue();
        logEvent.Should().NotBeNull();
        var parsedEvent = logEvent!;

        parsedEvent.Timestamp.Should().Be(currentTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a useful PHD2 debug line")]
    [InlineData("2026-06-18 22:00")]
    public void TryParseDebugLine_WhenLineIsMalformed_DoesNotThrowAndReturnsFalse(string line)
    {
        var act = () => Phd2LogParser.TryParseDebugLine(
            line,
            SourcePath,
            TimeProvider.System,
            out _);

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    private sealed class FixedTimeProvider(DateTimeOffset currentTime, TimeZoneInfo? localTimeZone = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => currentTime;
        public override TimeZoneInfo LocalTimeZone => localTimeZone ?? UtcTimeZone;
    }
}
