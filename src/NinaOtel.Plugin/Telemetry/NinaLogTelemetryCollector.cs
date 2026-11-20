using System.Globalization;
using System.Text;
using NinaOtel.Abstractions.Logs;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Logs;
using NinaOtel.Core.Options;

namespace NinaOtel.Plugin.Telemetry;

public sealed class NinaLogTelemetryCollector : IDisposable
{
    private const string SourceName = "nina.log";
    private const string FilteredLogName = "nina.log";
    private const string RawLogName = "nina.log.raw";
    private static readonly TimeSpan DefaultPendingEventFlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly object syncRoot = new();
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly NinaLogTailerStartPosition startPosition;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan pendingEventFlushDelay;
    private readonly int readBufferSize;
    private readonly LogSourceCatalog catalog;
    private CoreTelemetryOptions options;
    private NinaLogTailer? tailer;
    private LogSourceWatcher? watcher;
    private CancellationTokenSource? cancellation;
    private Task? pumpTask;
    private DateTimeOffset? pendingEventUpdatedAt;
    private bool waitingStatusPublished;
    private bool started;
    private bool disposed;

    public NinaLogTelemetryCollector(
        CoreTelemetryOptions options,
        ITelemetrySink sink,
        TimeProvider timeProvider)
        : this(
            options,
            sink,
            timeProvider,
            NinaLogTailerStartPosition.End,
            TimeSpan.FromSeconds(1),
            readBufferSize: 4096,
            pendingEventFlushDelay: DefaultPendingEventFlushDelay,
            catalog: null)
    {
    }

    internal NinaLogTelemetryCollector(
        CoreTelemetryOptions options,
        ITelemetrySink sink,
        TimeProvider timeProvider,
        NinaLogTailerStartPosition startPosition,
        TimeSpan pollInterval,
        int readBufferSize,
        TimeSpan? pendingEventFlushDelay = null,
        LogSourceCatalog? catalog = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "Poll interval must be positive.");
        }

        this.pollInterval = pollInterval;
        this.startPosition = startPosition;
        this.readBufferSize = readBufferSize;
        this.pendingEventFlushDelay = pendingEventFlushDelay ?? DefaultPendingEventFlushDelay;
        this.catalog = catalog ?? LogSourceCatalog.Default;
        if (this.pendingEventFlushDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pendingEventFlushDelay),
                pendingEventFlushDelay,
                "Pending event flush delay cannot be negative.");
        }
    }

    internal bool HasPendingTailEventForTests
    {
        get
        {
            lock (syncRoot)
            {
                return tailer?.HasPendingEvent == true;
            }
        }
    }

    public void Start()
    {
        string? waitingMessage = null;

        lock (syncRoot)
        {
            if (started || disposed)
            {
                return;
            }

            started = true;
            cancellation = new CancellationTokenSource();
            if (IsCollectionEnabled(options))
            {
                ReplaceTailerLocked();
                waitingMessage = UpdateWaitingStatusLocked();
            }

            pumpTask = Task.Run(() => PumpAsync(cancellation.Token));
        }

        if (waitingMessage is not null)
        {
            PublishWaitingStatus(waitingMessage);
        }
    }

    public void UpdateOptions(CoreTelemetryOptions updatedOptions)
    {
        ArgumentNullException.ThrowIfNull(updatedOptions);
        CoreTelemetryOptions? previousOptions = null;
        IReadOnlyList<NinaLogEvent> pendingEvents = [];
        string? waitingMessage = null;

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            var pathChanged = !string.Equals(
                options.NinaLogPath,
                updatedOptions.NinaLogPath,
                StringComparison.Ordinal);
            var wasEnabled = IsCollectionEnabled(options);
            previousOptions = options;
            options = updatedOptions;

            if (!IsCollectionEnabled(options))
            {
                pendingEvents = tailer?.FlushPending() ?? [];
                DisposeTailerLocked();
            }
            else if (tailer is null || pathChanged || !wasEnabled)
            {
                pendingEvents = tailer?.FlushPending() ?? [];
                ReplaceTailerLocked();
            }

            if (IsCollectionEnabled(options))
            {
                waitingMessage = UpdateWaitingStatusLocked();
            }
        }

        if (previousOptions is not null)
        {
            PublishEvents(pendingEvents, previousOptions);
        }

        if (waitingMessage is not null)
        {
            PublishWaitingStatus(waitingMessage);
        }
    }

    public void Dispose()
    {
        Task? taskToObserve = null;
        CancellationTokenSource? cancellationToDispose = null;
        CoreTelemetryOptions optionsSnapshot;
        IReadOnlyList<NinaLogEvent> pendingEvents = [];

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            taskToObserve = pumpTask;
            cancellationToDispose = cancellation;
            optionsSnapshot = options;
        }

        try
        {
            cancellationToDispose?.Cancel();
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }

        try
        {
            taskToObserve?.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Background collector shutdown is best-effort.
        }

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                pendingEvents = tailer?.FlushPending() ?? [];
            }
            catch
            {
                pendingEvents = [];
            }
        }

        PublishEvents(pendingEvents, optionsSnapshot);

        lock (syncRoot)
        {
            disposed = true;
            DisposeTailerLocked();
            pumpTask = null;
            cancellation = null;
        }

        try
        {
            cancellationToDispose?.Dispose();
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (events, optionsSnapshot, waitingMessage) = ReadAvailableEvents();
                PublishEvents(events, optionsSnapshot);
                if (waitingMessage is not null)
                {
                    PublishWaitingStatus(waitingMessage);
                }
            }
            catch
            {
                // Log tailing must never affect NINA runtime behavior.
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private (IReadOnlyList<NinaLogEvent> Events, CoreTelemetryOptions OptionsSnapshot, string? WaitingMessage) ReadAvailableEvents()
    {
        lock (syncRoot)
        {
            var optionsSnapshot = options;
            if (disposed || !IsCollectionEnabled(optionsSnapshot))
            {
                return ([], optionsSnapshot, null);
            }

            tailer ??= CreateTailer();
            tailer.RefreshSource();
            var waitingMessage = UpdateWaitingStatusLocked();
            if (tailer.ResolvedPath is null)
            {
                return ([], optionsSnapshot, waitingMessage);
            }

            var readResult = tailer.ReadAvailable();
            if (readResult.HasNewLines)
            {
                if (tailer.HasPendingEvent)
                {
                    pendingEventUpdatedAt = timeProvider.GetUtcNow();
                }
                else
                {
                    pendingEventUpdatedAt = null;
                }

                return (readResult.Events, optionsSnapshot, UpdateWaitingStatusLocked());
            }

            if (tailer.HasPendingEvent)
            {
                var now = timeProvider.GetUtcNow();
                pendingEventUpdatedAt ??= now;
                if (now - pendingEventUpdatedAt.Value >= pendingEventFlushDelay)
                {
                    pendingEventUpdatedAt = null;
                    return (tailer.FlushPending(), optionsSnapshot, UpdateWaitingStatusLocked());
                }
            }

            return ([], optionsSnapshot, UpdateWaitingStatusLocked());
        }
    }

    /// <summary>
    /// Publishes one waiting health record while autodetect cannot find a file, and forgets the
    /// published state as soon as a file is back so the next gap is reported again.
    /// </summary>
    private string? UpdateWaitingStatusLocked()
    {
        if (tailer is null || !tailer.FollowsRotation)
        {
            return null;
        }

        if (tailer.ResolvedPath is not null)
        {
            waitingStatusPublished = false;
            return null;
        }

        if (waitingStatusPublished)
        {
            return null;
        }

        waitingStatusPublished = true;
        return tailer.NotFoundMessage;
    }

    private void PublishEvents(IEnumerable<NinaLogEvent> events, CoreTelemetryOptions optionsSnapshot)
    {
        foreach (var logEvent in events)
        {
            if (disposed)
            {
                return;
            }

            PublishRawIfEnabled(logEvent, optionsSnapshot);
            PublishFilteredIfEnabled(logEvent, optionsSnapshot);
        }
    }

    private void PublishRawIfEnabled(NinaLogEvent logEvent, CoreTelemetryOptions optionsSnapshot)
    {
        if (!optionsSnapshot.RawForwardingEnabled)
        {
            return;
        }

        TryPublishSafely(new TelemetryRecord(
            TelemetrySignal.Log,
            logEvent.Timestamp,
            SourceName,
            RawLogName,
            TelemetryPriority.Debug,
            CreateRawAttributes(logEvent),
            Body: logEvent.RawLine,
            Severity: logEvent.Severity ?? TelemetrySeverity.Information));
    }

    private void PublishFilteredIfEnabled(NinaLogEvent logEvent, CoreTelemetryOptions optionsSnapshot)
    {
        if (!optionsSnapshot.FilteredLogsEnabled)
        {
            return;
        }

        if (ShouldPublishFilteredLog(logEvent))
        {
            TryPublishSafely(new TelemetryRecord(
                TelemetrySignal.Log,
                logEvent.Timestamp,
                SourceName,
                FilteredLogName,
                TelemetryPriority.Important,
                CreateAttributes(logEvent),
                Body: logEvent.Message,
                Severity: logEvent.Severity ?? SeverityForKind(logEvent.Kind)));
        }

        if (!TryGetBreadcrumbName(logEvent.Kind, out var breadcrumbName))
        {
            return;
        }

        TryPublishSafely(new TelemetryRecord(
            TelemetrySignal.Log,
            logEvent.Timestamp,
            SourceName,
            breadcrumbName,
            TelemetryPriority.Normal,
            CreateAttributes(logEvent),
            Body: logEvent.Message,
            Severity: logEvent.Severity ?? TelemetrySeverity.Information));
    }

    private NinaLogTailer CreateTailer() =>
        new(watcher ??= CreateWatcher(), startPosition, readBufferSize);

    private LogSourceWatcher CreateWatcher() => new(catalog.Nina, options.NinaLogPath);

    private void ReplaceTailerLocked()
    {
        DisposeTailerLocked();
        watcher = CreateWatcher();
        tailer = new NinaLogTailer(watcher, startPosition, readBufferSize);
        tailer.Prime();
    }

    private void DisposeTailerLocked()
    {
        try
        {
            tailer?.Dispose();
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
        finally
        {
            tailer = null;
            watcher = null;
            pendingEventUpdatedAt = null;
            waitingStatusPublished = false;
        }
    }

    private static bool IsCollectionEnabled(CoreTelemetryOptions options) =>
        options.FilteredLogsEnabled || options.RawForwardingEnabled;

    private void PublishWaitingStatus(string searchMessage) =>
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "nina.log.status",
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["nina.log.path"] = string.Empty,
                ["status"] = "waiting",
                ["reason"] = "not_found",
                ["nina.log.search"] = searchMessage,
                ["message"] = searchMessage,
            }));

    private void TryPublishSafely(TelemetryRecord record)
    {
        try
        {
            if (!disposed)
            {
                sink.TryPublish(record);
            }
        }
        catch
        {
            // Telemetry sink failures must not affect NINA.
        }
    }

    private static IReadOnlyDictionary<string, object?> CreateAttributes(NinaLogEvent logEvent) =>
        new Dictionary<string, object?>
        {
            ["nina.log.level"] = logEvent.Level,
            ["nina.log.source"] = logEvent.Source,
            ["nina.log.member"] = logEvent.Member,
            ["nina.log.line"] = logEvent.LineNumber,
            ["nina.log.kind"] = ToSnakeCase(logEvent.Kind.ToString()),
            ["nina.log.timestamp"] = logEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        };

    private static IReadOnlyDictionary<string, object?> CreateRawAttributes(NinaLogEvent logEvent)
    {
        var attributes = new Dictionary<string, object?>(CreateAttributes(logEvent))
        {
            ["raw.line"] = logEvent.RawLine,
        };

        return attributes;
    }

    private static bool IsErrorLogKind(NinaLogEventKind kind) =>
        kind is NinaLogEventKind.Warning or NinaLogEventKind.Error or NinaLogEventKind.Fatal;

    private static bool ShouldPublishFilteredLog(NinaLogEvent logEvent) =>
        IsErrorLogKind(logEvent.Kind) ||
        logEvent.Severity is TelemetrySeverity.Warning or TelemetrySeverity.Error or TelemetrySeverity.Fatal;

    private static TelemetrySeverity SeverityForKind(NinaLogEventKind kind) =>
        kind switch
        {
            NinaLogEventKind.Fatal => TelemetrySeverity.Fatal,
            NinaLogEventKind.Error => TelemetrySeverity.Error,
            NinaLogEventKind.Warning => TelemetrySeverity.Warning,
            _ => TelemetrySeverity.Information,
        };

    private static bool TryGetBreadcrumbName(NinaLogEventKind kind, out string name)
    {
        name = kind switch
        {
            NinaLogEventKind.ApplicationStarted => "nina.application.start",
            NinaLogEventKind.ApplicationClosing => "nina.application.stop",
            NinaLogEventKind.PluginLoaded => "nina.plugin.loaded",
            NinaLogEventKind.PluginLoadFailed => "nina.plugin.load_failed",
            NinaLogEventKind.EquipmentConnected => "nina.equipment.connected",
            NinaLogEventKind.EquipmentDisconnected => "nina.equipment.disconnected",
            NinaLogEventKind.SequenceStarted => "nina.sequence.start",
            NinaLogEventKind.SequenceFinished => "nina.sequence.stop",
            NinaLogEventKind.AutofocusStarted => "nina.autofocus.start",
            NinaLogEventKind.AutofocusFinished => "nina.autofocus.stop",
            NinaLogEventKind.MeridianFlipStarted => "nina.meridian_flip.start",
            NinaLogEventKind.MeridianFlipFinished => "nina.meridian_flip.stop",
            NinaLogEventKind.SafetyUnsafe => "nina.safety.unsafe",
            NinaLogEventKind.SafetySafe => "nina.safety.safe",
            _ => string.Empty,
        };

        return name.Length > 0;
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
