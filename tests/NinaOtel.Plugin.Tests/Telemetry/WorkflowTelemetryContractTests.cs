using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.NightSummary;
using NinaOtel.Addons.PHD2;
using NinaOtel.Addons.TargetScheduler;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class WorkflowTelemetryContractTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(8);

    public static IEnumerable<object[]> EquipmentWorkflowNamesCoveredByBehaviorTests()
    {
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/ImageTelemetryCollectorTests.cs", "nina.exposure"];
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/ImageTelemetryCollectorTests.cs", "nina.image_save"];
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/FilterWheelTelemetryCollectorTests.cs", "nina.filter_change"];
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/GuiderTelemetryCollectorTests.cs", "nina.dither_settle"];
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/MountTelemetryCollectorTests.cs", "nina.slew"];
        yield return ["tests/NinaOtel.Plugin.Tests/Telemetry/FocuserTelemetryCollectorTests.cs", "nina.autofocus"];
    }

    [Fact]
    public void CoreLifecycleProducer_PublishesStableSessionRecordName()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault());

        producer.PluginInitialized();
        producer.PluginStopped();

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "nina.session" &&
            record.SpanKind == SpanEventKind.Start);
        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "nina.session" &&
            record.SpanKind == SpanEventKind.Stop);
    }

    [Fact]
    public async Task NinaLogCollector_PublishesStableLifecycleBreadcrumbRecordNames()
    {
        var expectedNames = new[]
        {
            "nina.application.start",
            "nina.application.stop",
            "nina.plugin.loaded",
            "nina.plugin.load_failed",
            "nina.equipment.connected",
            "nina.equipment.disconnected",
            "nina.sequence.start",
            "nina.sequence.stop",
            "nina.autofocus.start",
            "nina.autofocus.stop",
            "nina.meridian_flip.start",
            "nina.meridian_flip.stop",
            "nina.safety.unsafe",
            "nina.safety.safe",
        };
        using var logFile = TempLogFile.Create(
        [
            "2026-06-18T22:00:00.0000|INFO|NINA.Core.App|Start|10|Application started",
            "2026-06-18T22:00:01.0000|INFO|NINA.Core.App|Stop|11|Application shutting down",
            "2026-06-18T22:00:02.0000|INFO|NINA.Core.PluginLoader|Load|12|Successfully loaded plugin NinaOtel",
            "2026-06-18T22:00:03.0000|INFO|NINA.Core.PluginLoader|Load|13|Failed to load plugin NinaOtel",
            "2026-06-18T22:00:04.0000|INFO|NINA.Equipment.Camera|Connect|14|Camera connected",
            "2026-06-18T22:00:05.0000|INFO|NINA.Equipment.Mount|Disconnect|15|Mount disconnected",
            "2026-06-18T22:00:06.0000|INFO|NINA.Sequencer.Sequence|Start|16|Sequence started",
            "2026-06-18T22:00:07.0000|INFO|NINA.Sequencer.Sequence|Finish|17|Sequence finished",
            "2026-06-18T22:00:08.0000|INFO|NINA.Sequencer.AutoFocus|Start|18|Autofocus started",
            "2026-06-18T22:00:09.0000|INFO|NINA.Sequencer.AutoFocus|Finish|19|Autofocus finished",
            "2026-06-18T22:00:10.0000|INFO|NINA.Sequencer.MeridianFlip|Start|20|Meridian flip started",
            "2026-06-18T22:00:11.0000|INFO|NINA.Sequencer.MeridianFlip|Finish|21|Meridian flip finished",
            "2026-06-18T22:00:12.0000|INFO|NINA.Equipment.SafetyMonitor|Update|22|Safety monitor changed to Unsafe",
            "2026-06-18T22:00:13.0000|INFO|NINA.Equipment.SafetyMonitor|Update|23|Safety monitor changed to Safe",
        ]);
        var sink = new RecordingSink();
        using var collector = new NinaLogTelemetryCollector(
            new CoreTelemetryOptions
            {
                NinaLogPath = logFile.Path,
                FilteredLogsEnabled = true,
            },
            sink,
            TimeProvider.System,
            NinaLogTailerStartPosition.Beginning,
            PollInterval,
            readBufferSize: 4096);

        collector.Start();

        await WaitForNamesAsync(sink, expectedNames);
        sink.Records
            .Where(static record => record.Source == "nina.log")
            .Select(static record => record.Name)
            .Should()
            .Contain(expectedNames);
    }

    [Fact]
    public async Task Addons_PublishStableWorkflowRecordNames()
    {
        using var phd2Log = TempLogFile.Create([]);
        using var targetSchedulerLog = TempLogFile.Create([]);
        using var nightSummaryLog = TempLogFile.Create([]);
        var sink = new RecordingSink();
        var expectedNames = new[]
        {
            "phd2.dither",
            "phd2.settle",
            "target_scheduler.planning",
            "night_summary.session",
            "night_summary.report",
        };

        await using var addons = new AddonScope(
        [
            (new Phd2TelemetryAddon(), CreateContext(sink, debugLogPath: phd2Log.Path)),
            (new TargetSchedulerTelemetryAddon(), CreateContext(sink, logPath: targetSchedulerLog.Path)),
            (new NightSummaryTelemetryAddon(), CreateContext(sink, logPath: nightSummaryLog.Path)),
        ]);
        await addons.StartAsync();

        phd2Log.Append("2026-06-18 22:00:10.000 Dither: started");
        phd2Log.Append("2026-06-18 22:00:15.000 Settling started");
        targetSchedulerLog.Append("2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started");
        nightSummaryLog.Append("2026-06-18T22:00:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Session started. SessionId=abc-123");
        nightSummaryLog.Append("2026-06-18T22:01:00.0000|INFO|NightSummary.cs|Log|10|NightSummary: Generating report for session abc-123 ...");

        await WaitForNamesAsync(sink, expectedNames);
        sink.Records.Select(static record => record.Name).Should().Contain(expectedNames);
    }

    [Theory]
    [MemberData(nameof(EquipmentWorkflowNamesCoveredByBehaviorTests))]
    public void EquipmentWorkflowRecordNames_AreCoveredByBehaviorTests(
        string relativeTestPath,
        string recordName)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativeTestPath));

        source.Should().Contain(
            $"record.Name == \"{recordName}\"",
            $"the stable {recordName} contract should be asserted by the owning collector behavior test");
    }

    private static AddonContext CreateContext(
        ITelemetrySink sink,
        string? debugLogPath = null,
        string? logPath = null)
    {
        var settings = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(debugLogPath))
        {
            settings["DebugLogPath"] = debugLogPath;
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            settings["LogPath"] = logPath;
        }

        return new AddonContext(
            sink,
            TimeProvider.System,
            CancellationToken.None,
            new AddonConfiguration(settings: settings));
    }

    private static async Task WaitForNamesAsync(
        RecordingSink sink,
        IReadOnlyCollection<string> expectedNames)
    {
        var stopAt = DateTimeOffset.UtcNow + WaitTimeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            var names = sink.Records.Select(static record => record.Name).ToHashSet(StringComparer.Ordinal);
            if (expectedNames.All(names.Contains))
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        var observedNames = string.Join(", ", sink.Records.Select(static record => record.Name).Distinct().Order());
        throw new TimeoutException($"Expected telemetry names were not published before timeout. Observed: {observedNames}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NinaOtel.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run from inside the repository");
        return directory!.FullName;
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

    private sealed class TempLogFile : IDisposable
    {
        private TempLogFile(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }
        public string Path { get; }

        public static TempLogFile Create(IReadOnlyList<string> lines)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "telemetry.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));
            if (lines.Count > 0)
            {
                File.AppendAllText(path, Environment.NewLine);
            }

            return new TempLogFile(directory, path);
        }

        public void Append(string line) =>
            File.AppendAllText(Path, line + Environment.NewLine);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class AddonScope : IAsyncDisposable
    {
        private readonly IReadOnlyList<(ITelemetryAddon Addon, IAddonContext Context)> addons;

        public AddonScope(IReadOnlyList<(ITelemetryAddon Addon, IAddonContext Context)> addons)
        {
            this.addons = addons;
        }

        public async Task StartAsync()
        {
            foreach (var (addon, context) in addons)
            {
                await addon.StartAsync(context, CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (addon, _) in addons)
            {
                try
                {
                    await addon.StopAsync(CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }
}
