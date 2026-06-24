using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class SwitchTelemetryCollector : ISwitchConsumer, IDisposable
{
    private const string SourceName = "nina.switch";
    private const string UnknownSwitchName = "Unknown";

    private readonly object syncRoot = new();
    private readonly ISwitchMediator mediator;
    private readonly Dictionary<short, PublishedSwitchMetric> publishedMetrics = [];
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private string? lastConnectedSwitchName;
    private string? lastDisconnectedSwitchName;
    private bool startAttempted;
    private bool startupFailed;
    private bool registered;
    private bool connectedSubscriptionAttempted;
    private bool disconnectedSubscriptionAttempted;
    private bool subscribedConnected;
    private bool subscribedDisconnected;
    private bool lifecycleEventsEnabled;
    private bool disconnectedEventLogged;

    public SwitchTelemetryCollector(
        ISwitchMediator mediator,
        ITelemetrySink sink,
        TimeProvider timeProvider)
    {
        this.mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed || startAttempted)
            {
                return;
            }

            startAttempted = true;
            try
            {
                mediator.RegisterConsumer(this);
                registered = true;

                connectedSubscriptionAttempted = true;
                mediator.Connected += OnConnected;
                subscribedConnected = true;

                disconnectedSubscriptionAttempted = true;
                mediator.Disconnected += OnDisconnected;
                subscribedDisconnected = true;
                lifecycleEventsEnabled = true;
            }
            catch (Exception ex)
            {
                startupFailed = true;
                lifecycleEventsEnabled = false;
                CleanupFailedStart();
                PublishRegistrationFailure(ex);
            }
        }
    }

    public void UpdateDeviceInfo(SwitchInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        try
        {
            lock (syncRoot)
            {
                if (disposed || startupFailed)
                {
                    return;
                }

                UpdateDeviceInfoCore(deviceInfo);
            }
        }
        catch
        {
            // NINA equipment callbacks must never fail because telemetry handling failed.
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifecycleEventsEnabled = false;

            if (connectedSubscriptionAttempted || subscribedConnected)
            {
                try
                {
                    mediator.Connected -= OnConnected;
                    subscribedConnected = false;
                    connectedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (disconnectedSubscriptionAttempted || subscribedDisconnected)
            {
                try
                {
                    mediator.Disconnected -= OnDisconnected;
                    subscribedDisconnected = false;
                    disconnectedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (!registered)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
            }
            catch
            {
                // Telemetry teardown must never interfere with NINA shutdown.
            }
        }
    }

    private void UpdateDeviceInfoCore(SwitchInfo deviceInfo)
    {
        var switchName = NormalizeName(deviceInfo.Name);
        var timestamp = timeProvider.GetUtcNow();

        if (!deviceInfo.Connected)
        {
            if (lastConnectedSwitchName is not null)
            {
                lastDisconnectedSwitchName = lastConnectedSwitchName;
                PublishClearMetrics(timestamp);
                ResetPublishedState();
            }

            return;
        }

        if (lastConnectedSwitchName is not null &&
            !string.Equals(lastConnectedSwitchName, switchName, StringComparison.Ordinal))
        {
            PublishClearMetrics(timestamp);
            ResetPublishedFlags();
        }

        disconnectedEventLogged = false;
        lastDisconnectedSwitchName = null;
        lastConnectedSwitchName = switchName;
        var readOnlySwitches = deviceInfo.ReadonlySwitches;
        if (readOnlySwitches is null || readOnlySwitches.Count == 0)
        {
            PublishClearMetrics(timestamp);
            ResetPublishedFlags();
            return;
        }

        PublishCurrentMetrics(timestamp, switchName, readOnlySwitches);
    }

    private Task OnConnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var timestamp = timeProvider.GetUtcNow();
                var deviceInfo = TryGetInfo();
                var switchName = deviceInfo is { Connected: true }
                    ? NormalizeName(deviceInfo.Name)
                    : ResolveConnectedSwitchName();

                disconnectedEventLogged = false;
                lastDisconnectedSwitchName = null;
                if (deviceInfo is { Connected: true })
                {
                    if (lastConnectedSwitchName is not null &&
                        !string.Equals(lastConnectedSwitchName, switchName, StringComparison.Ordinal))
                    {
                        PublishClearMetrics(timestamp);
                        ResetPublishedFlags();
                    }

                    lastConnectedSwitchName = switchName;
                }

                PublishNamedLog(
                    timestamp,
                    "switch_connected",
                    "Switch connected",
                    CreateSwitchAttributes(switchName));
            }
        }
        catch
        {
            // NINA switch events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || disconnectedEventLogged || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var timestamp = timeProvider.GetUtcNow();
                var switchName = ResolveDisconnectedSwitchName();

                PublishNamedLog(
                    timestamp,
                    "switch_disconnected",
                    "Switch disconnected",
                    CreateSwitchAttributes(switchName));
                PublishClearMetrics(timestamp);
                lastDisconnectedSwitchName = switchName;
                ResetPublishedState();
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA switch events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private void PublishCurrentMetrics(
        DateTimeOffset timestamp,
        string switchName,
        IReadOnlyCollection<ISwitch> readOnlySwitches)
    {
        var currentSwitchIds = new HashSet<short>();

        foreach (var readOnlySwitch in readOnlySwitches)
        {
            if (readOnlySwitch is null)
            {
                continue;
            }

            currentSwitchIds.Add(readOnlySwitch.Id);
            if (!double.IsFinite(readOnlySwitch.Value))
            {
                ClearPublishedMetric(timestamp, readOnlySwitch.Id);
                continue;
            }

            var currentMetric = CreatePublishedMetric(switchName, readOnlySwitch);
            if (publishedMetrics.TryGetValue(readOnlySwitch.Id, out var previousMetric) &&
                !previousMetric.HasSameIdentity(currentMetric))
            {
                PublishClearMetric(timestamp, previousMetric);
            }

            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                currentMetric.Name,
                readOnlySwitch.Value,
                TelemetryPriority.Normal,
                currentMetric.Attributes));
            publishedMetrics[readOnlySwitch.Id] = currentMetric;
        }

        foreach (var disappearedSwitchId in publishedMetrics.Keys.Except(currentSwitchIds).ToArray())
        {
            ClearPublishedMetric(timestamp, disappearedSwitchId);
        }
    }

    private void PublishClearMetrics(DateTimeOffset timestamp)
    {
        foreach (var metric in publishedMetrics.Values)
        {
            PublishClearMetric(timestamp, metric);
        }
    }

    private void ClearPublishedMetric(DateTimeOffset timestamp, short switchId)
    {
        if (!publishedMetrics.Remove(switchId, out var metric))
        {
            return;
        }

        PublishClearMetric(timestamp, metric);
    }

    private void PublishClearMetric(DateTimeOffset timestamp, PublishedSwitchMetric metric)
    {
        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            metric.Name,
            double.NaN,
            TelemetryPriority.Normal,
            metric.Attributes));
    }

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "switch_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void CleanupFailedStart()
    {
        if (disconnectedSubscriptionAttempted)
        {
            try
            {
                mediator.Disconnected -= OnDisconnected;
                subscribedDisconnected = false;
                disconnectedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (connectedSubscriptionAttempted)
        {
            try
            {
                mediator.Connected -= OnConnected;
                subscribedConnected = false;
                connectedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (registered)
        {
            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        PublishClearMetrics(timeProvider.GetUtcNow());
        ResetPublishedState();
        lastDisconnectedSwitchName = null;
        disconnectedEventLogged = false;
    }

    private void PublishNamedLog(
        DateTimeOffset timestamp,
        string name,
        string body,
        IReadOnlyDictionary<string, object?> attributes) =>
        TryPublishSafely(new TelemetryRecord(
            TelemetrySignal.Log,
            timestamp,
            SourceName,
            name,
            TelemetryPriority.Normal,
            attributes,
            Body: body,
            Severity: TelemetrySeverity.Information));

    private void ResetPublishedState()
    {
        lastConnectedSwitchName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags() =>
        publishedMetrics.Clear();

    private void TryPublishSafely(TelemetryRecord record)
    {
        try
        {
            sink.TryPublish(record);
        }
        catch
        {
            // NINA equipment callbacks must never fail because telemetry is unavailable.
        }
    }

    private string ResolveConnectedSwitchName() =>
        NormalizeName(lastConnectedSwitchName ?? lastDisconnectedSwitchName);

    private string ResolveDisconnectedSwitchName() =>
        lastConnectedSwitchName ??
        lastDisconnectedSwitchName ??
        NormalizeName(TryGetInfo()?.Name);

    private SwitchInfo? TryGetInfo()
    {
        try
        {
            return mediator.GetInfo();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> CreateSwitchAttributes(string switchName) =>
        new()
        {
            ["switch_name"] = switchName,
        };

    private static PublishedSwitchMetric CreatePublishedMetric(string switchName, ISwitch readOnlySwitch) =>
        new(
            NinaMetricCatalog.SwitchReadOnlyGaugeName(readOnlySwitch.Id),
            new Dictionary<string, object?>
            {
                ["switch_name"] = switchName,
                ["switch_id"] = readOnlySwitch.Id,
                ["name"] = NormalizeName(readOnlySwitch.Name),
                ["switch_channel_name"] = NormalizeName(readOnlySwitch.Name),
            });

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? UnknownSwitchName
            : name;

    private sealed class PublishedSwitchMetric(
        string name,
        IReadOnlyDictionary<string, object?> attributes)
    {
        public string Name { get; } = name;

        public IReadOnlyDictionary<string, object?> Attributes { get; } = attributes;

        public bool HasSameIdentity(PublishedSwitchMetric other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            Equals(Attributes["switch_name"], other.Attributes["switch_name"]) &&
            Equals(Attributes["switch_id"], other.Attributes["switch_id"]) &&
            Equals(Attributes["switch_channel_name"], other.Attributes["switch_channel_name"]);
    }
}
