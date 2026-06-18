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
    private bool startAttempted;
    private bool registered;

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
            }
            catch (Exception ex)
            {
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
            if (!registered)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
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

    private static PublishedSwitchMetric CreatePublishedMetric(string switchName, ISwitch readOnlySwitch) =>
        new(
            NinaMetricCatalog.SwitchReadOnlyGaugeName(readOnlySwitch.Id),
            new Dictionary<string, object?>
            {
                ["switch_name"] = switchName,
                ["switch_id"] = readOnlySwitch.Id,
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
