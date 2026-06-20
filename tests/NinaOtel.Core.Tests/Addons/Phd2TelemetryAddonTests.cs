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

    private static AddonContext CreateContext(
        RecordingSink sink,
        CancellationToken shutdownToken,
        string? debugLogPath = null,
        string? guideLogPath = null,
        bool rawForwardingEnabled = false)
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
