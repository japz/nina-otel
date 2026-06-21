using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.NightSummary;
using NinaOtel.Core.Addons;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class NightSummaryTelemetryAddonTests
{
    private const string RecognizedLine =
        "2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123";
    private const string SessionEndedLine =
        "2026-06-18T23:00:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Session ended. SessionId=abc-123";
    private const string ReportGeneratingLine =
        "2026-06-18T23:01:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Generating report for session abc-123 ...";
    private const string ReportDeliveredWithoutSessionIdLine =
        "2026-06-18T23:02:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Report sent and session marked as complete";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StartAsync_WhenNoPathIsConfigured_ReportsDegraded()
    {
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        var context = CreateContext(sink, CancellationToken.None);

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "night-summary") &&
            Equals(record.Attributes["status"], "degraded")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("Night Summary");
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("log path");
    }

    [Fact]
    public async Task StartAsync_WhenConfiguredFileIsMissing_ReportsDegradedWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var missingPath = Path.Combine(temp.Path, "nina.log");
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: missingPath);

        await addon.StartAsync(context, CancellationToken.None);

        var health = await WaitForRecordAsync(sink, record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "night-summary") &&
            Equals(record.Attributes["status"], "degraded"));
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("not found");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenConfiguredFileExists_ReportsRunning()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "night-summary") &&
            Equals(record.Attributes["status"], "running")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("Night Summary");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenOnlyProfileStorageLogPathKeyIsConfigured_ReportsDegraded()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            logPath: temp.Path,
            settingKey: "Addon.night-summary.LogPath");

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "night-summary") &&
            Equals(record.Attributes["status"], "degraded")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("log path");
    }

    [Fact]
    public async Task Tailer_WhenRecognizedLineIsAppended_PublishesParsedLogRecord()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, RecognizedLine + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "night_summary.log_event");

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Source.Should().Be("night-summary");
        record.Severity.Should().Be(TelemetrySeverity.Information);
        record.Body.Should().Be("NightSummary: Session started. SessionId=abc-123");
        record.Attributes["source.file"].Should().Be(temp.Path);
        record.Attributes["event.kind"].Should().Be("session_started");
        record.Attributes["session.id"].Should().Be("abc-123");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenSessionLifecycleLinesAreAppended_PublishesSessionSpansAndCountMetrics()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            RecognizedLine + Environment.NewLine + SessionEndedLine + Environment.NewLine);

        var startedMetric = await WaitForRecordAsync(sink, record => record.Name == "night_summary_session_started_count");
        var endedMetric = await WaitForRecordAsync(sink, record => record.Name == "night_summary_session_ended_count");
        var startSpan = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "night_summary.session" &&
            record.SpanKind == SpanEventKind.Start).Subject;
        var stopSpan = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "night_summary.session" &&
            record.SpanKind == SpanEventKind.Stop).Subject;

        startSpan.SpanId.Should().Be($"night_summary.session|{temp.Path}|abc-123");
        stopSpan.SpanId.Should().Be(startSpan.SpanId);
        startSpan.Attributes["addon.id"].Should().Be("night-summary");
        startSpan.Attributes["source"].Should().Be("night-summary");
        startSpan.Attributes["source.file"].Should().Be(temp.Path);
        startSpan.Attributes["event.kind"].Should().Be("session_started");
        startSpan.Attributes["message"].Should().Be("NightSummary: Session started. SessionId=abc-123");
        startSpan.Attributes["session.id"].Should().Be("abc-123");

        startedMetric.Signal.Should().Be(TelemetrySignal.Metric);
        startedMetric.NumericValue.Should().Be(1);
        startedMetric.Attributes["event.kind"].Should().Be("session_started");
        startedMetric.Attributes["session.id"].Should().Be("abc-123");
        endedMetric.NumericValue.Should().Be(1);
        endedMetric.Attributes["event.kind"].Should().Be("session_ended");
        endedMetric.Attributes["session.id"].Should().Be("abc-123");

        sink.Records.Where(record => record.Name == "night_summary.log_event")
            .Should()
            .HaveCount(2);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenReportDeliveredOmitsSessionId_ReusesActiveReportSpanIdAndSessionId()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            ReportGeneratingLine + Environment.NewLine + ReportDeliveredWithoutSessionIdLine + Environment.NewLine);

        var deliveredMetric = await WaitForRecordAsync(sink, record => record.Name == "night_summary_report_delivered_count");
        var startedMetric = sink.Records.Should().ContainSingle(record =>
            record.Name == "night_summary_report_started_count").Subject;
        var startSpan = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "night_summary.report" &&
            record.SpanKind == SpanEventKind.Start).Subject;
        var stopSpan = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "night_summary.report" &&
            record.SpanKind == SpanEventKind.Stop).Subject;

        startSpan.SpanId.Should().Be($"night_summary.report|{temp.Path}|abc-123");
        stopSpan.SpanId.Should().Be(startSpan.SpanId);
        stopSpan.Attributes["event.kind"].Should().Be("report_delivered");
        stopSpan.Attributes["session.id"].Should().Be("abc-123");
        deliveredMetric.Attributes["session.id"].Should().Be("abc-123");
        deliveredMetric.NumericValue.Should().Be(1);
        startedMetric.NumericValue.Should().Be(1);

        await addon.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(
        "2026-06-18T22:10:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Event logged — AutoFocus: AutoFocus completed — Filter: Ha, Temp: -3.2°C, Position: 12456",
        "night_summary_autofocus_completed_count",
        "autofocus_completed")]
    [InlineData(
        "2026-06-18T22:20:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Event logged — MeridianFlip: Meridian flip completed",
        "night_summary_meridian_flip_count",
        "meridian_flip")]
    public async Task Tailer_WhenCountedEventLineIsAppended_PublishesCountMetric(
        string line,
        string expectedMetricName,
        string expectedEventKind)
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, line + Environment.NewLine);

        var metric = await WaitForRecordAsync(sink, record => record.Name == expectedMetricName);

        metric.Signal.Should().Be(TelemetrySignal.Metric);
        metric.NumericValue.Should().Be(1);
        metric.Attributes["addon.id"].Should().Be("night-summary");
        metric.Attributes["source"].Should().Be("night-summary");
        metric.Attributes["source.file"].Should().Be(temp.Path);
        metric.Attributes["event.kind"].Should().Be(expectedEventKind);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenReportStopHasNoActiveReportContext_UsesDeterministicFallbackSpanId()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(
            temp.Path,
            ReportDeliveredWithoutSessionIdLine + Environment.NewLine + ReportDeliveredWithoutSessionIdLine + Environment.NewLine);

        await WaitForRecordAsync(sink, record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "night_summary.report" &&
            sink.Records.Count(candidate => candidate.Name == "night_summary.report") == 2);
        var spans = sink.Records.Where(record =>
                record.Signal == TelemetrySignal.Span &&
                record.Name == "night_summary.report")
            .ToArray();

        spans.Should().HaveCount(2);
        spans.Select(static record => record.SpanId).Distinct().Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        spans[0].SpanId.Should().NotStartWith($"night_summary.report|{temp.Path}|");
        spans.Should().OnlyContain(record => !record.Attributes.ContainsKey("session.id"));

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenSinkPublishThrows_ContinuesPublishingSubsequentRecords()
    {
        using var temp = new TempLogFile();
        var sink = new ThrowOnceSink(TelemetrySignal.Log);
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, RecognizedLine + Environment.NewLine);

        await WaitForConditionAsync(() => sink.PublishAttempts >= 3);

        sink.Records.Should().Contain(record => record.Signal == TelemetrySignal.Span);
        sink.Records.Should().Contain(record => record.Signal == TelemetrySignal.Metric);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundTailWorkerPromptly()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await addon.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));
        await File.AppendAllTextAsync(temp.Path, RecognizedLine + Environment.NewLine);
        await Task.Delay(TimeSpan.FromMilliseconds(80));

        sink.Records.Should().NotContain(record => record.Name == "night_summary.log_event");
    }

    private static AddonContext CreateContext(
        ITelemetrySink sink,
        CancellationToken shutdownToken,
        string? logPath = null,
        string settingKey = "LogPath")
    {
        var settings = new Dictionary<string, string>();
        if (logPath is not null)
        {
            settings[settingKey] = logPath;
        }

        return new AddonContext(
            sink,
            TimeProvider.System,
            shutdownToken,
            new AddonConfiguration(settings: settings));
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

    private static async Task WaitForConditionAsync(Func<bool> predicate)
    {
        var stopAt = DateTimeOffset.UtcNow + WaitTimeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException("Expected condition was not satisfied before timeout.");
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

    private sealed class ThrowOnceSink : ITelemetrySink
    {
        private readonly object syncRoot = new();
        private readonly List<TelemetryRecord> records = [];
        private readonly TelemetrySignal signalToThrow;
        private bool hasThrown;

        public int PublishAttempts { get; private set; }

        public ThrowOnceSink(TelemetrySignal signalToThrow)
        {
            this.signalToThrow = signalToThrow;
        }

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
                PublishAttempts++;
                if (!hasThrown && record.Signal == signalToThrow)
                {
                    hasThrown = true;
                    throw new InvalidOperationException("Sink publish failed.");
                }

                records.Add(record);
            }

            return true;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-night-summary-" + Guid.NewGuid().ToString("N"));
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
            Path = System.IO.Path.Combine(directory.Path, "nina.log");
            File.WriteAllText(Path, "2026-06-18T21:59:00.0000|INFO|NightSummary.cs|Log|1|Historical line" + Environment.NewLine);
        }

        public string Path { get; }

        public void Dispose() => directory.Dispose();
    }
}
