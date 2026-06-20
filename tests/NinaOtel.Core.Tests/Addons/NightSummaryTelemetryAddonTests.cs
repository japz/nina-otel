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

    [Theory]
    [InlineData("LogPath")]
    [InlineData("Addon.night-summary.LogPath")]
    public async Task StartAsync_WhenConfiguredFileExists_ReportsRunning(string settingKey)
    {
        using var temp = new TempLogFile();
        var sink = new RecordingSink();
        var addon = new NightSummaryTelemetryAddon(PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, logPath: temp.Path, settingKey: settingKey);

        await addon.StartAsync(context, CancellationToken.None);

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "night-summary") &&
            Equals(record.Attributes["status"], "running")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("Night Summary");

        await addon.StopAsync(CancellationToken.None);
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
        record.Attributes["event.kind"].Should().Be("SessionStarted");
        record.Attributes["session.id"].Should().Be("abc-123");

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
