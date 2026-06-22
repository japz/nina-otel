using System.Globalization;
using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class NinaLogTelemetryCollectorTests
{
    [Fact]
    public async Task Start_WhenFilteredLogsEnabled_PublishesWarningErrorAndFatalLogRecords()
    {
        using var logFile = TempNinaLog.Create(
        [
            "DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE",
            "2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Routine warning",
            "2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|Routine error",
            "2026-06-18T22:00:02.0000|FATAL|NINA.Core.App|Run|12|Routine fatal",
        ]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, filteredLogsEnabled: true);

        collector.Start();

        var records = await WaitForRecordsAsync(sink, expectedCount: 3);
        records.Should().OnlyContain(static record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.log" &&
            record.Name == "nina.log" &&
            record.Priority == TelemetryPriority.Important);
        records.Select(static record => record.Severity).Should().Equal(
            TelemetrySeverity.Warning,
            TelemetrySeverity.Error,
            TelemetrySeverity.Fatal);
        records.Select(static record => record.Body).Should().Equal(
            "Routine warning",
            "Routine error",
            "Routine fatal");
        AssertLogAttributes(
            records[0],
            level: "WARNING",
            source: "NINA.Core.App",
            member: "Warn",
            lineNumber: 10,
            kind: "warning",
            timestamp: ParsedTimestamp(2026, 6, 18, 22, 0, 0));
    }

    [Fact]
    public async Task Start_WhenFilteredLogsEnabled_PublishesLifecycleBreadcrumbsForClassifiedEvents()
    {
        using var logFile = TempNinaLog.Create(
        [
            "2026-06-18T22:00:00.0000|INFO|NINA.Core.App|Start|10|Application started",
            "2026-06-18T22:00:01.0000|INFO|NINA.Core.PluginLoader|Load|11|Successfully loaded plugin NinaOtel",
            "2026-06-18T22:00:02.0000|INFO|NINA.Equipment.Camera|Connect|12|Camera connected",
            "2026-06-18T22:00:03.0000|INFO|NINA.Sequencer.Sequence|Start|13|Sequence started",
            "2026-06-18T22:00:04.0000|INFO|NINA.Equipment.SafetyMonitor|Update|14|Safety monitor changed to Unsafe",
        ]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, filteredLogsEnabled: true);

        collector.Start();

        var records = await WaitForRecordsAsync(sink, expectedCount: 5);
        records.Select(static record => record.Name).Should().Equal(
            "nina.application.start",
            "nina.plugin.loaded",
            "nina.equipment.connected",
            "nina.sequence.start",
            "nina.safety.unsafe");
        records.Should().OnlyContain(static record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.log" &&
            record.Priority == TelemetryPriority.Normal &&
            record.Severity == TelemetrySeverity.Information);
        AssertLogAttributes(
            records[3],
            level: "INFO",
            source: "NINA.Sequencer.Sequence",
            member: "Start",
            lineNumber: 13,
            kind: "sequence_started",
            timestamp: ParsedTimestamp(2026, 6, 18, 22, 0, 3));
    }

    [Fact]
    public async Task Start_WhenRawForwardingEnabled_PublishesRawParsedNinaLogRecords()
    {
        var warningLine = "2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Routine warning";
        var infoLine = "2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Start|11|Application started";
        using var logFile = TempNinaLog.Create([warningLine, infoLine]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, rawForwardingEnabled: true);

        collector.Start();

        var records = await WaitForRecordsAsync(sink, expectedCount: 2);
        records.Should().OnlyContain(static record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.log" &&
            record.Name == "nina.log.raw" &&
            record.Priority == TelemetryPriority.Debug);
        records.Select(static record => record.Body).Should().Equal(warningLine, infoLine);
        records.Select(static record => record.Severity).Should().Equal(
            TelemetrySeverity.Warning,
            TelemetrySeverity.Information);
        AssertLogAttributes(
            records[1],
            level: "INFO",
            source: "NINA.Core.App",
            member: "Start",
            lineNumber: 11,
            kind: "application_started",
            timestamp: ParsedTimestamp(2026, 6, 18, 22, 0, 1));
    }

    [Fact]
    public async Task Start_WhenLogFileIsMissing_DoesNotThrowAndPublishesNothing()
    {
        var missingPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nina.log");
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(missingPath, sink, filteredLogsEnabled: true);

        var act = () => collector.Start();

        act.Should().NotThrow();
        await Task.Delay(TimeSpan.FromMilliseconds(75));
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispose_StopsPublishingLaterLogRecords()
    {
        using var logFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, filteredLogsEnabled: true);
        collector.Start();

        logFile.Append("2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Routine warning");
        await WaitForRecordsAsync(sink, expectedCount: 1);

        collector.Dispose();
        logFile.Append("2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|Routine error");
        await Task.Delay(TimeSpan.FromMilliseconds(75));

        sink.Records.Should().ContainSingle()
            .Which.Body.Should().Be("Routine warning");
    }

    [Fact]
    public async Task UpdateOptions_WhenStartedWithoutPath_PublishesAfterPathConfigured()
    {
        using var logFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            new CoreTelemetryOptions
            {
                FilteredLogsEnabled = true,
            },
            sink,
            startPosition: NinaLogTailerStartPosition.End);
        collector.Start();

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = logFile.Path,
            FilteredLogsEnabled = true,
        });
        logFile.Append("2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Configured later");

        var records = await WaitForRecordsAsync(sink, expectedCount: 1);
        records.Should().ContainSingle()
            .Which.Body.Should().Be("Configured later");
    }

    [Fact]
    public async Task UpdateOptions_WhenFileDoesNotExistYet_ReadsCreatedFileFromBeginning()
    {
        using var missingLog = TempMissingNinaLog.Create();
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            new CoreTelemetryOptions
            {
                FilteredLogsEnabled = true,
            },
            sink,
            startPosition: NinaLogTailerStartPosition.End,
            pollInterval: TimeSpan.FromSeconds(1));
        collector.Start();

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = missingLog.Path,
            FilteredLogsEnabled = true,
        });
        System.IO.Directory.CreateDirectory(missingLog.Directory);
        File.AppendAllText(
            missingLog.Path,
            "2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Created after prime" + Environment.NewLine);

        var records = await WaitForRecordsAsync(sink, expectedCount: 1);
        records.Should().ContainSingle()
            .Which.Body.Should().Be("Created after prime");
    }

    [Fact]
    public async Task UpdateOptions_WhenDisabledAfterStart_StopsPublishing()
    {
        using var logFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, filteredLogsEnabled: true);
        collector.Start();

        logFile.Append("2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Before disabled");
        await WaitForRecordsAsync(sink, expectedCount: 1);

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = logFile.Path,
            FilteredLogsEnabled = false,
            RawForwardingEnabled = false,
        });
        logFile.Append("2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|After disabled");
        await Task.Delay(TimeSpan.FromMilliseconds(75));

        sink.Records.Should().ContainSingle()
            .Which.Body.Should().Be("Before disabled");
    }

    [Fact]
    public async Task UpdateOptions_WhenDisabledWithHeldEvent_FlushesHeldEventBeforeStopping()
    {
        using var logFile = TempNinaLog.Create(
        [
            "2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Held before disabled",
        ]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            logFile.Path,
            sink,
            filteredLogsEnabled: true,
            pollInterval: TimeSpan.FromSeconds(5));
        collector.Start();

        await WaitForTailerToHoldEventAsync(collector);

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = logFile.Path,
            FilteredLogsEnabled = false,
            RawForwardingEnabled = false,
        });
        logFile.Append("2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|After disabled");
        await Task.Delay(TimeSpan.FromMilliseconds(75));

        sink.Records.Should().ContainSingle()
            .Which.Body.Should().Be("Held before disabled");
    }

    [Fact]
    public async Task UpdateOptions_WhenPathChanges_ReadsOnlyNewPath()
    {
        using var firstLogFile = TempNinaLog.Create([]);
        using var secondLogFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            firstLogFile.Path,
            sink,
            filteredLogsEnabled: true,
            startPosition: NinaLogTailerStartPosition.End);
        collector.Start();

        firstLogFile.Append("2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|First path");
        await WaitForRecordsAsync(sink, expectedCount: 1);

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = secondLogFile.Path,
            FilteredLogsEnabled = true,
        });
        firstLogFile.Append("2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|Old path after switch");
        secondLogFile.Append("2026-06-18T22:00:02.0000|ERROR|NINA.Core.App|Run|12|Second path");

        var records = await WaitForRecordsAsync(sink, expectedCount: 2);
        records.Select(static record => record.Body).Should().Equal("First path", "Second path");
    }

    [Fact]
    public async Task UpdateOptions_WhenPathChangesWithHeldEvent_FlushesHeldEventBeforeSwitching()
    {
        using var firstLogFile = TempNinaLog.Create(
        [
            "2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Held before switch",
        ]);
        using var secondLogFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            firstLogFile.Path,
            sink,
            filteredLogsEnabled: true,
            pollInterval: TimeSpan.FromSeconds(5));
        collector.Start();

        await WaitForTailerToHoldEventAsync(collector);

        collector.UpdateOptions(new CoreTelemetryOptions
        {
            NinaLogPath = secondLogFile.Path,
            FilteredLogsEnabled = true,
        });
        secondLogFile.Append("2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|After switch");
        secondLogFile.Append("2026-06-18T22:00:02.0000|INFO|NINA.Core.App|Tick|12|Sentinel");

        var records = await WaitForRecordsAsync(sink, expectedCount: 2);
        records.Select(static record => record.Body).Should().Equal("Held before switch", "After switch");
    }

    [Fact]
    public async Task Start_WhenLogFileIsTruncated_ReopensAndPublishesNewRows()
    {
        using var logFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(logFile.Path, sink, filteredLogsEnabled: true);
        collector.Start();

        logFile.Append("2026-06-18T22:00:00.0000|WARNING|NINA.Core.App|Warn|10|Before truncate");
        await WaitForRecordsAsync(sink, expectedCount: 1);

        logFile.Replace(
        [
            "2026-06-18T22:00:01.0000|ERROR|NINA.Core.App|Run|11|After truncate with enough extra text to keep the file longer than it was before",
        ]);

        var records = await WaitForRecordsAsync(sink, expectedCount: 2);
        records.Select(static record => record.Body).Should().Equal(
            "Before truncate",
            "After truncate with enough extra text to keep the file longer than it was before");
    }

    [Fact]
    public async Task Start_WhenContinuationLinesAreSplitAcrossTailerReads_AttachesThemToPriorEvent()
    {
        using var logFile = TempNinaLog.Create(
        [
            "2026-06-18T22:00:00.0000|ERROR|NINA.Core.PluginLoader|Load|10|Could not load plugin NinaOtel",
            "System.InvalidOperationException: Plugin manifest is invalid",
            "   at NINA.Core.PluginLoader.Load(String path)",
            "2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Stop|11|Application shutting down",
        ]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            logFile.Path,
            sink,
            filteredLogsEnabled: true,
            readBufferSize: 32);

        collector.Start();

        var records = await WaitForRecordsAsync(sink, expectedCount: 3);
        var filteredError = records.Should()
            .ContainSingle(static record => record.Name == "nina.log")
            .Which;
        filteredError.Severity.Should().Be(TelemetrySeverity.Error);
        filteredError.Body.Should().Be(
            "Could not load plugin NinaOtel" + Environment.NewLine +
            "System.InvalidOperationException: Plugin manifest is invalid" + Environment.NewLine +
            "   at NINA.Core.PluginLoader.Load(String path)");

        var loadFailed = records.Should()
            .ContainSingle(static record => record.Name == "nina.plugin.load_failed")
            .Which;
        loadFailed.Body.Should().Be(
            "Could not load plugin NinaOtel" + Environment.NewLine +
            "System.InvalidOperationException: Plugin manifest is invalid" + Environment.NewLine +
            "   at NINA.Core.PluginLoader.Load(String path)");
        loadFailed.Attributes.Should().Contain("nina.log.kind", "plugin_load_failed");

        records.Should().ContainSingle(static record => record.Name == "nina.application.stop")
            .Which.Body.Should().Be("Application shutting down");
    }

    [Fact]
    public async Task Start_WhenContinuationLineArrivesAfterIdlePoll_AttachesItToPriorEvent()
    {
        using var logFile = TempNinaLog.Create([]);
        var sink = new RecordingTelemetrySink();
        using var collector = CreateCollector(
            logFile.Path,
            sink,
            filteredLogsEnabled: true,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pendingEventFlushDelay: TimeSpan.FromSeconds(5));

        collector.Start();
        logFile.Append("2026-06-18T22:00:00.0000|ERROR|NINA.Core.PluginLoader|Load|10|Could not load plugin NinaOtel");
        await WaitForTailerToHoldEventAsync(collector);
        logFile.Append("System.InvalidOperationException: Plugin manifest is invalid");
        logFile.Append("2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Stop|11|Application shutting down");
        logFile.Append("2026-06-18T22:00:02.0000|INFO|NINA.Core.App|Tick|12|Sentinel");

        var records = await WaitForRecordsAsync(sink, expectedCount: 3);
        var filteredError = records.Should()
            .ContainSingle(static record => record.Name == "nina.log")
            .Which;
        filteredError.Body.Should().Be(
            "Could not load plugin NinaOtel" + Environment.NewLine +
            "System.InvalidOperationException: Plugin manifest is invalid");

        var loadFailed = records.Should()
            .ContainSingle(static record => record.Name == "nina.plugin.load_failed")
            .Which;
        loadFailed.Body.Should().Be(filteredError.Body);
        records.Should().ContainSingle(static record => record.Name == "nina.application.stop")
            .Which.Body.Should().Be("Application shutting down");
    }

    private static NinaLogTelemetryCollector CreateCollector(
        string logPath,
        RecordingTelemetrySink sink,
        bool filteredLogsEnabled = false,
        bool rawForwardingEnabled = false,
        int readBufferSize = 4096,
        NinaLogTailerStartPosition startPosition = NinaLogTailerStartPosition.Beginning,
        TimeSpan? pollInterval = null,
        TimeSpan? pendingEventFlushDelay = null) =>
        CreateCollector(
            new CoreTelemetryOptions
            {
                NinaLogPath = logPath,
                FilteredLogsEnabled = filteredLogsEnabled,
                RawForwardingEnabled = rawForwardingEnabled,
            },
            sink,
            readBufferSize,
            startPosition,
            pollInterval,
            pendingEventFlushDelay);

    private static NinaLogTelemetryCollector CreateCollector(
        CoreTelemetryOptions options,
        RecordingTelemetrySink sink,
        int readBufferSize = 4096,
        NinaLogTailerStartPosition startPosition = NinaLogTailerStartPosition.Beginning,
        TimeSpan? pollInterval = null,
        TimeSpan? pendingEventFlushDelay = null) =>
        new(
            options,
            sink,
            TimeProvider.System,
            startPosition,
            pollInterval ?? TimeSpan.FromMilliseconds(10),
            readBufferSize,
            pendingEventFlushDelay ?? TimeSpan.FromMilliseconds(10));

    private static async Task WaitForTailerToHoldEventAsync(NinaLogTelemetryCollector collector)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (collector.HasPendingTailEventForTests)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        collector.HasPendingTailEventForTests.Should().BeTrue("the pump should have read and held the single log event");
    }

    private static async Task<IReadOnlyList<TelemetryRecord>> WaitForRecordsAsync(
        RecordingTelemetrySink sink,
        int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var records = sink.Records;
            if (records.Count >= expectedCount)
            {
                return records;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return sink.Records;
    }

    private static void AssertLogAttributes(
        TelemetryRecord record,
        string level,
        string source,
        string member,
        int lineNumber,
        string kind,
        DateTimeOffset timestamp)
    {
        record.Timestamp.Should().Be(timestamp);
        record.Attributes.Should().Contain("nina.log.level", level);
        record.Attributes.Should().Contain("nina.log.source", source);
        record.Attributes.Should().Contain("nina.log.member", member);
        record.Attributes.Should().Contain("nina.log.line", lineNumber);
        record.Attributes.Should().Contain("nina.log.kind", kind);
        record.Attributes.Should().Contain(
            "nina.log.timestamp",
            timestamp.ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset ParsedTimestamp(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var localWallClock = new DateTime(year, month, day, hour, minute, second);
        return new DateTimeOffset(localWallClock, TimeZoneInfo.Local.GetUtcOffset(localWallClock));
    }

    private sealed class RecordingTelemetrySink : ITelemetrySink
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

    private sealed class TempNinaLog : IDisposable
    {
        private TempNinaLog(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }
        public string Path { get; }

        public static TempNinaLog Create(IReadOnlyList<string> lines)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "nina.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));
            if (lines.Count > 0)
            {
                File.AppendAllText(path, Environment.NewLine);
            }

            return new TempNinaLog(directory, path);
        }

        public void Append(string line) =>
            File.AppendAllText(Path, line + Environment.NewLine);

        public void Replace(IReadOnlyList<string> lines)
        {
            File.WriteAllText(Path, string.Join(Environment.NewLine, lines));
            if (lines.Count > 0)
            {
                File.AppendAllText(Path, Environment.NewLine);
            }
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch
            {
                // Temp cleanup must not fail the test.
            }
        }
    }

    private sealed class TempMissingNinaLog : IDisposable
    {
        private TempMissingNinaLog(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }
        public string Path { get; }

        public static TempMissingNinaLog Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            return new TempMissingNinaLog(directory, System.IO.Path.Combine(directory, "nina.log"));
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup must not fail the test.
            }
        }
    }
}
