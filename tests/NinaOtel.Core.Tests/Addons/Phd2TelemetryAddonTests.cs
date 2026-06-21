using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.PHD2;
using NinaOtel.Core.Addons;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class Phd2TelemetryAddonTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StartAsync_WhenPathIsConfigured_ReturnsPromptly()
    {
        using var temp = new TempLogFile();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(new RecordingSink(), shutdown.Token, debugLogPath: temp.Path);

        var startTask = addon.StartAsync(context, CancellationToken.None);

        await startTask.WaitAsync(TimeSpan.FromMilliseconds(200));
        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenNoPathsAreConfigured_ReportsWaitingForLogPaths()
    {
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        var context = CreateContext(sink, CancellationToken.None);

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "phd2") &&
            Equals(record.Attributes["status"], "waiting")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("PHD2 log path");
        health.Priority.Should().Be(TelemetryPriority.Routine);
    }

    [Fact]
    public async Task StartAsync_WhenConfiguredFileIsMissing_ReportsWaitingAndKeepsRunning()
    {
        using var temp = new TempDirectory();
        var missingPath = Path.Combine(temp.Path, "missing-debug-log.txt");
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: missingPath);

        await addon.StartAsync(context, CancellationToken.None);

        var health = await WaitForRecordAsync(sink, record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "phd2") &&
            Equals(record.Attributes["status"], "waiting"));
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("not found");

        await File.AppendAllTextAsync(missingPath, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);
        await File.AppendAllTextAsync(missingPath, "2026-06-18 22:00:05.000 Guiding Stopped" + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_stopped");
        record.Signal.Should().Be(TelemetrySignal.Log);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenRecognizedGuidingLineIsAppended_PublishesNormalizedLogRecord()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path, rawForwardingEnabled: true);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_started");

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Source.Should().Be("phd2");
        record.Severity.Should().Be(TelemetrySeverity.Information);
        record.Priority.Should().Be(TelemetryPriority.Normal);
        record.Attributes["source.file"].Should().Be(temp.Path);
        record.Attributes["event.kind"].Should().Be("guiding_started");
        record.Attributes["raw.line"].Should().Be("2026-06-18 22:00:00.125 Guiding Begins");

        await addon.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("2026-06-18 22:00:10.000 Dither: started", "phd2.dither", "dither", SpanEventKind.Stop)]
    [InlineData("2026-06-18 22:00:15.000 Settling started", "phd2.settle", "settle_started", SpanEventKind.Start)]
    [InlineData("2026-06-18 22:00:20.500 Settle complete", "phd2.settle", "settle_completed", SpanEventKind.Stop)]
    public async Task Tailer_WhenSpanEventLineIsAppended_PublishesSpan(
        string line,
        string expectedName,
        string expectedKind,
        SpanEventKind expectedSpanKind)
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, line + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == expectedName);

        record.Signal.Should().Be(TelemetrySignal.Span);
        record.Source.Should().Be("phd2");
        record.SpanKind.Should().Be(expectedSpanKind);
        record.SpanId.Should().NotBeNullOrWhiteSpace();
        record.Priority.Should().Be(TelemetryPriority.Normal);
        record.Attributes["event.kind"].Should().Be(expectedKind);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenMissingFileIsCreatedAfterStart_ReadsNewFileFromBeginning()
    {
        using var temp = new TempDirectory();
        var missingPath = Path.Combine(temp.Path, "PHD2_DebugLog.txt");
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(TimeSpan.FromMilliseconds(200));
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: missingPath);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(missingPath, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_started");
        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Attributes["source.file"].Should().Be(missingPath);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenLogFileIsReplaced_ReopensAndReadsNewFileFromBeginning()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);
        await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_started");

        File.Move(temp.Path, temp.Path + ".old");
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:30.000 capture failed: star lost" + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "phd2.capture_error");
        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Priority.Should().Be(TelemetryPriority.Important);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenCaptureErrorLineIsAppended_PublishesImportantErrorLog()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:30.000 capture failed: star lost" + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "phd2.capture_error");

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Severity.Should().Be(TelemetrySeverity.Error);
        record.Priority.Should().Be(TelemetryPriority.Important);
        record.Attributes["event.kind"].Should().Be("capture_error");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenCalledAgain_DoesNotLeakEarlierTailers()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);

        await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_started");
        await Task.Delay(TimeSpan.FromMilliseconds(120));

        sink.Records.Count(record => record.Name == "phd2.guiding_started").Should().Be(1);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WhenOneTailerTimesOut_CancelsLaterTailersBeforeReturning()
    {
        using var first = new TempLogFile();
        using var second = new TempLogFile();
        using var sink = new BlockingSink(first.Path);
        var addon = new Phd2TelemetryAddon(PollInterval, TimeSpan.FromMilliseconds(50));
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: first.Path, guideLogPath: second.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(first.Path, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);
        sink.WaitUntilBlocked();

        await addon.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await File.AppendAllTextAsync(second.Path, "2026-06-18 22:00:30.000 capture failed: star lost" + Environment.NewLine);
        await Task.Delay(TimeSpan.FromMilliseconds(120));
        sink.Release();

        sink.Records.Should().NotContain(record =>
            record.Name == "phd2.capture_error" &&
            Equals(record.Attributes["source.file"], second.Path));
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundTailWorkers()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, debugLogPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await addon.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));
        await File.AppendAllTextAsync(temp.Path, "2026-06-18 22:00:00.125 Guiding Begins" + Environment.NewLine);
        await Task.Delay(TimeSpan.FromMilliseconds(80));

        sink.Records.Should().NotContain(record => record.Name == "phd2.guiding_started");
    }

    [Fact]
    public async Task Tailer_WhenGuideLogSessionEnds_PublishesAggregatedGuideSummaryMetrics()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero));
        var context = CreateContext(
            sink,
            shutdown.Token,
            guideLogPath: temp.Path,
            timeProvider: timeProvider);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            string.Join(
                Environment.NewLine,
                [
                    "Guiding Begins at 2026-06-18 22:00:00",
                    "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,SNR,ErrorCode,StarMass",
                    "1,1.000,Mount,0.1,-0.2,3,4,0.5,-0.6,100,W,200,N,30,0,5000",
                    "2,2.000,Mount,-0.1,0.3,0,0,0.0,0.0,0,E,0,S,31,0,5100",
                    "Guiding Ends at 2026-06-18 22:00:03",
                    string.Empty,
                ]));

        await WaitForRecordAsync(sink, record => record.Name == "phd2_guide_rms_pixel");

        var metrics = sink.Records
            .Where(record => record.Signal == TelemetrySignal.Metric)
            .ToArray();
        metrics.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_ra_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(4.5), 1e-9);
        metrics.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_dec_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(8), 1e-9);
        metrics.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(12.5), 1e-9);
        metrics.Should()
            .ContainSingle(record => record.Name == "phd2_guide_sample_count")
            .Which.NumericValue.Should().Be(2);

        string[] summaryMetricNames =
        [
            "phd2_guide_rms_ra_pixel",
            "phd2_guide_rms_dec_pixel",
            "phd2_guide_rms_pixel",
            "phd2_guide_sample_count",
        ];
        var summaryMetrics = metrics
            .Where(record => summaryMetricNames.Contains(record.Name, StringComparer.Ordinal))
            .ToArray();
        summaryMetrics.Should().HaveCount(4);
        foreach (var record in summaryMetrics)
        {
            record.Source.Should().Be("phd2");
            record.Priority.Should().Be(TelemetryPriority.Normal);
            record.Attributes.Should().Contain("addon.id", "phd2");
            record.Attributes.Should().Contain("guider_name", "PHD2");
            record.Attributes.Should().Contain("source.file", temp.Path);
            record.Attributes.Should().Contain("phd2.session_start", "2026-06-18T22:00:00.0000000+00:00");
        }

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenGuideLogSamplesAreAppended_PublishesPulseMetricsAtSampleTimestamps()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            guideLogPath: temp.Path,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero)));

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            string.Join(
                Environment.NewLine,
                [
                    "Guiding Begins at 2026-06-18 22:00:00",
                    "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,SNR,ErrorCode,StarMass",
                    "11,1.250,Mount,0.1,-0.2,3,4,0.5,-0.6,100,W,200,N,30,0,5000",
                    "12,2.500,Mount,-0.1,0.3,6,8,-0.7,0.8,125,E,225,S,31,0,5100",
                    "Guiding Ends at 2026-06-18 22:00:03",
                    string.Empty,
                ]));

        await WaitForRecordAsync(sink, record =>
            record.Name == "phd2_guide_dec_pulse_duration_ms" &&
            record.NumericValue == 225);
        await WaitForRecordAsync(sink, record => record.Name == "phd2_guide_rms_pixel");

        var pulseMetrics = sink.Records
            .Where(record => record.Signal == TelemetrySignal.Metric &&
                record.Name.Contains("_pulse_", StringComparison.Ordinal))
            .ToArray();

        pulseMetrics.Should().HaveCount(8);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_ra_pulse_distance_pixel",
            0.5,
            new DateTimeOffset(2026, 6, 18, 22, 0, 1, 250, TimeSpan.Zero),
            "W",
            "N",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_ra_pulse_duration_ms",
            100,
            new DateTimeOffset(2026, 6, 18, 22, 0, 1, 250, TimeSpan.Zero),
            "W",
            "N",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_dec_pulse_distance_pixel",
            -0.6,
            new DateTimeOffset(2026, 6, 18, 22, 0, 1, 250, TimeSpan.Zero),
            "W",
            "N",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_dec_pulse_duration_ms",
            200,
            new DateTimeOffset(2026, 6, 18, 22, 0, 1, 250, TimeSpan.Zero),
            "W",
            "N",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_ra_pulse_distance_pixel",
            -0.7,
            new DateTimeOffset(2026, 6, 18, 22, 0, 2, 500, TimeSpan.Zero),
            "E",
            "S",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_ra_pulse_duration_ms",
            125,
            new DateTimeOffset(2026, 6, 18, 22, 0, 2, 500, TimeSpan.Zero),
            "E",
            "S",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_dec_pulse_distance_pixel",
            0.8,
            new DateTimeOffset(2026, 6, 18, 22, 0, 2, 500, TimeSpan.Zero),
            "E",
            "S",
            temp.Path);
        AssertPulseMetric(
            pulseMetrics,
            "phd2_guide_dec_pulse_duration_ms",
            225,
            new DateTimeOffset(2026, 6, 18, 22, 0, 2, 500, TimeSpan.Zero),
            "E",
            "S",
            temp.Path);

        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_ra_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(22.5), 1e-9);
        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_dec_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(40), 1e-9);
        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_pixel")
            .Which.NumericValue.Should().BeApproximately(Math.Sqrt(62.5), 1e-9);
        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_sample_count")
            .Which.NumericValue.Should().Be(2);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenGuideLogSampleHasNoPulseColumns_PublishesRmsSummaryWithoutPulseMetrics()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            guideLogPath: temp.Path,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero)));

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            string.Join(
                Environment.NewLine,
                [
                    "Guiding Begins at 2026-06-18 22:00:00",
                    "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance",
                    "1,1.000,Mount,0.1,-0.2,3,4",
                    "Guiding Ends at 2026-06-18 22:00:03",
                    string.Empty,
                ]));

        await WaitForRecordAsync(sink, record => record.Name == "phd2_guide_rms_pixel");

        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_ra_pixel")
            .Which.NumericValue.Should().Be(3);
        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_dec_pixel")
            .Which.NumericValue.Should().Be(4);
        sink.Records.Should()
            .ContainSingle(record => record.Name == "phd2_guide_rms_pixel")
            .Which.NumericValue.Should().Be(5);
        sink.Records.Should()
            .NotContain(record =>
                record.Signal == TelemetrySignal.Metric &&
                record.Name.Contains("_pulse_", StringComparison.Ordinal));

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenGuideLogSampleWouldOverflowTotalRms_IgnoresSampleWithoutPublishingSummaryMetrics()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new Phd2TelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            guideLogPath: temp.Path,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero)));

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            string.Join(
                Environment.NewLine,
                [
                    "Guiding Begins at 2026-06-18 22:00:00",
                    "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,SNR,ErrorCode,StarMass",
                    "1,1.000,Mount,0.1,-0.2,1e154,1e154,0.5,-0.6,100,W,200,N,30,0,5000",
                    "Guiding Ends at 2026-06-18 22:00:03",
                    string.Empty,
                ]));

        await WaitForRecordAsync(sink, record => record.Name == "phd2.guiding_stopped");

        sink.Records.Should().NotContain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name.StartsWith("phd2_guide_", StringComparison.Ordinal));

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Validate_AcceptsEmptyAndMissingLogPaths()
    {
        var addon = new Phd2TelemetryAddon(PollInterval);
        var configuration = new AddonConfiguration(settings: new Dictionary<string, string>
        {
            ["DebugLogPath"] = string.Empty,
            ["GuideLogPath"] = @"Z:\missing\guide.txt",
        });

        var result = addon.Validate(configuration);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    private static void AssertPulseMetric(
        IReadOnlyList<TelemetryRecord> records,
        string name,
        double value,
        DateTimeOffset timestamp,
        string raDirection,
        string decDirection,
        string sourcePath)
    {
        var record = records.Should().ContainSingle(candidate =>
            candidate.Name == name &&
            candidate.Timestamp == timestamp &&
            candidate.NumericValue == value).Subject;

        record.Source.Should().Be("phd2");
        record.Priority.Should().Be(TelemetryPriority.Normal);
        record.Attributes.Should().Contain("addon.id", "phd2");
        record.Attributes.Should().Contain("source", "phd2");
        record.Attributes.Should().Contain("source.file", sourcePath);
        record.Attributes.Should().Contain("guider_name", "PHD2");
        record.Attributes.Should().Contain("phd2.session_start", "2026-06-18T22:00:00.0000000+00:00");
        record.Attributes.Should().Contain("phd2.ra_direction", raDirection);
        record.Attributes.Should().Contain("phd2.dec_direction", decDirection);
        record.Attributes.Should().NotContainKey("phd2.frame");
    }

    private static AddonContext CreateContext(
        ITelemetrySink sink,
        CancellationToken shutdownToken,
        string? debugLogPath = null,
        string? guideLogPath = null,
        bool rawForwardingEnabled = false,
        TimeProvider? timeProvider = null)
    {
        var settings = new Dictionary<string, string>();
        if (debugLogPath is not null)
        {
            settings["DebugLogPath"] = debugLogPath;
        }

        if (guideLogPath is not null)
        {
            settings["GuideLogPath"] = guideLogPath;
        }

        return new AddonContext(
            sink,
            timeProvider ?? TimeProvider.System,
            shutdownToken,
            new AddonConfiguration(rawForwardingEnabled: rawForwardingEnabled, settings: settings));
    }

    private static async Task<TelemetryRecord> WaitForRecordAsync(
        RecordingSink sink,
        Func<TelemetryRecord, bool> predicate)
    {
        var stopAt = DateTimeOffset.UtcNow + WaitTimeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            var record = sink.Records.FirstOrDefault(predicate);
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException("Expected telemetry record was not published before timeout.");
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        private readonly object syncRoot = new();
        private readonly List<TelemetryRecord> records = [];

        public IReadOnlyList<TelemetryRecord> Records
        {
            get
            {
                lock (syncRoot)
                {
                    return records.ToArray();
                }
            }
        }

        public bool TryPublish(TelemetryRecord record)
        {
            lock (syncRoot)
            {
                records.Add(record);
            }

            return true;
        }
    }

    private sealed class BlockingSink : ITelemetrySink, IDisposable
    {
        private readonly RecordingSink inner = new();
        private readonly string blockedPath;
        private readonly ManualResetEventSlim blocked = new();
        private readonly ManualResetEventSlim release = new();
        private int hasBlocked;

        public BlockingSink(string blockedPath)
        {
            this.blockedPath = blockedPath;
        }

        public IReadOnlyList<TelemetryRecord> Records => inner.Records;

        public bool TryPublish(TelemetryRecord record)
        {
            record.Attributes.TryGetValue("source.file", out var sourceFile);
            if (Equals(sourceFile, blockedPath) &&
                Interlocked.CompareExchange(ref hasBlocked, 1, 0) == 0)
            {
                blocked.Set();
                release.Wait(TimeSpan.FromSeconds(2));
            }

            return inner.TryPublish(record);
        }

        public void WaitUntilBlocked() =>
            blocked.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("the first tailer should enter publish");

        public void Release() => release.Set();

        public void Dispose()
        {
            blocked.Dispose();
            release.Dispose();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow.ToUniversalTime();
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-phd2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TempLogFile : IDisposable
    {
        private readonly TempDirectory directory = new();

        public TempLogFile()
        {
            Path = System.IO.Path.Combine(directory.Path, "PHD2_DebugLog.txt");
            File.WriteAllText(Path, "2026-06-18 21:59:00.000 Historical line" + Environment.NewLine);
        }

        public string Path { get; }

        public void Dispose() => directory.Dispose();
    }
}
