using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.TargetScheduler;
using NinaOtel.Core.Addons;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class TargetSchedulerTelemetryAddonTests
{
    private const string PlanningStartedLine =
        "2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started";

    private const string PlanningCompletedLine =
        "2026-06-18T22:00:05.0000|INFO|Scheduler.cs|Run|20|Target Scheduler: planning run completed";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StartAsync_WhenNoPathIsConfigured_ReportsWaitingForLogPath()
    {
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        var context = CreateContext(sink, CancellationToken.None);

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "target-scheduler") &&
            Equals(record.Attributes["status"], "waiting")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("Target Scheduler log path");
        health.Priority.Should().Be(TelemetryPriority.Routine);
    }

    [Fact]
    public async Task StartAsync_WhenConfiguredFileIsMissing_ReportsWaitingAndKeepsRunning()
    {
        using var temp = new TempDirectory();
        var missingPath = Path.Combine(temp.Path, "target-scheduler.log");
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: missingPath);

        await addon.StartAsync(context, CancellationToken.None);

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "target-scheduler") &&
            Equals(record.Attributes["status"], "running"));
        var waiting = await WaitForRecordAsync(sink, record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "target-scheduler") &&
            Equals(record.Attributes["status"], "waiting"));
        waiting.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("not found");

        await File.AppendAllTextAsync(missingPath, PlanningStartedLine + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "target_scheduler.planning_started");
        record.Signal.Should().Be(TelemetrySignal.Log);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenRecognizedLineIsAppended_PublishesNormalizedLogRecord()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path, rawForwardingEnabled: true);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, PlanningStartedLine + Environment.NewLine);

        var record = await WaitForRecordAsync(sink, record => record.Name == "target_scheduler.planning_started");

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Source.Should().Be("target-scheduler");
        record.Severity.Should().Be(TelemetrySeverity.Information);
        record.Priority.Should().Be(TelemetryPriority.Normal);
        record.Attributes["source.file"].Should().Be(temp.Path);
        record.Attributes["event.kind"].Should().Be("planning_started");
        record.Attributes["raw.line"].Should().Be(PlanningStartedLine);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenWarningOrErrorLineIsAppended_PublishesImportantLog()
    {
        const string warningLine =
            "2026-06-18T22:02:00.0000|WARNING|Scheduler.cs|Run|70|Target Scheduler: rejected target M42 below horizon";
        const string errorLine =
            "2026-06-18T22:03:00.0000|ERROR|Scheduler.cs|Run|80|Target Scheduler: planning failed timeout";
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, warningLine + Environment.NewLine);
        await File.AppendAllTextAsync(temp.Path, errorLine + Environment.NewLine);

        var warning = await WaitForRecordAsync(sink, record => record.Name == "target_scheduler.warning");
        var error = await WaitForRecordAsync(sink, record => record.Name == "target_scheduler.error");

        warning.Signal.Should().Be(TelemetrySignal.Log);
        warning.Severity.Should().Be(TelemetrySeverity.Warning);
        warning.Priority.Should().Be(TelemetryPriority.Important);
        warning.Attributes["event.kind"].Should().Be("warning");
        error.Signal.Should().Be(TelemetrySignal.Log);
        error.Severity.Should().Be(TelemetrySeverity.Error);
        error.Priority.Should().Be(TelemetryPriority.Important);
        error.Attributes["event.kind"].Should().Be("error");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tailer_WhenPlanningStartAndCompletedAreAppended_PublishesPlanningSpanStartAndStop()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await File.AppendAllTextAsync(temp.Path, PlanningStartedLine + Environment.NewLine);
        await File.AppendAllTextAsync(temp.Path, PlanningCompletedLine + Environment.NewLine);

        var start = await WaitForRecordAsync(sink, record =>
            record.Name == "target_scheduler.planning" &&
            record.SpanKind == SpanEventKind.Start);
        var stop = await WaitForRecordAsync(sink, record =>
            record.Name == "target_scheduler.planning" &&
            record.SpanKind == SpanEventKind.Stop);

        start.Signal.Should().Be(TelemetrySignal.Span);
        start.Source.Should().Be("target-scheduler");
        start.SpanId.Should().NotBeNullOrWhiteSpace();
        start.Attributes["event.kind"].Should().Be("planning_started");
        stop.Signal.Should().Be(TelemetrySignal.Span);
        stop.Source.Should().Be("target-scheduler");
        stop.SpanId.Should().Be(start.SpanId);
        stop.Attributes["event.kind"].Should().Be("planning_completed");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundTailWorker()
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path);

        await addon.StartAsync(context, CancellationToken.None);
        await addon.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));
        await File.AppendAllTextAsync(temp.Path, PlanningStartedLine + Environment.NewLine);
        await Task.Delay(TimeSpan.FromMilliseconds(80));

        sink.Records.Should().NotContain(record => record.Name == "target_scheduler.planning_started");
    }

    [Fact]
    public void Validate_AcceptsEmptyAndMissingLogPath()
    {
        var addon = new TargetSchedulerTelemetryAddon(PollInterval);
        var configuration = new AddonConfiguration(settings: new Dictionary<string, string>
        {
            ["LogPath"] = string.Empty,
        });

        var result = addon.Validate(configuration);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    private static AddonContext CreateContext(
        ITelemetrySink sink,
        CancellationToken shutdownToken,
        string? logPath = null,
        bool rawForwardingEnabled = false)
    {
        var settings = new Dictionary<string, string>();
        if (logPath is not null)
        {
            settings["LogPath"] = logPath;
        }

        return new AddonContext(
            sink,
            TimeProvider.System,
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-target-scheduler-" + Guid.NewGuid().ToString("N"));
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
            Path = System.IO.Path.Combine(directory.Path, "target-scheduler.log");
            File.WriteAllText(Path, "2026-06-18T21:59:00.0000|INFO|Scheduler.cs|Run|1|Historical line" + Environment.NewLine);
        }

        public string Path { get; }

        public void Dispose() => directory.Dispose();
    }
}
