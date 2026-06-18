using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class MountTelemetryCollector : ITelescopeConsumer, IDisposable
{
    private const string SourceName = "nina.mount";
    private const string UnknownMountName = "Unknown";

    private readonly object syncRoot = new();
    private readonly ITelescopeMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool hasPublishedAltitude;
    private bool hasPublishedAzimuth;
    private string? lastConnectedMountName;
    private bool startAttempted;
    private bool registered;

    public MountTelemetryCollector(
        ITelescopeMediator mediator,
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

    public void UpdateDeviceInfo(TelescopeInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            var mountName = NormalizeMountName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedMountName is not null)
                {
                    PublishClearMetrics(
                        lastConnectedMountName,
                        hasPublishedAltitude,
                        hasPublishedAzimuth);
                    ResetPublishedState();
                }

                return;
            }

            if (lastConnectedMountName is not null &&
                !string.Equals(lastConnectedMountName, mountName, StringComparison.Ordinal))
            {
                PublishClearMetrics(
                    lastConnectedMountName,
                    hasPublishedAltitude,
                    hasPublishedAzimuth);
                ResetPublishedFlags();
            }

            lastConnectedMountName = mountName;
            PublishCurrentMetrics(deviceInfo, mountName);
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

    private void PublishCurrentMetrics(TelescopeInfo deviceInfo, string mountName)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateMountAttributes(mountName);

        PublishOrClearUnavailableMetric(
            timestamp,
            "mount_altitude",
            deviceInfo.Altitude,
            attributes,
            ref hasPublishedAltitude);

        PublishOrClearUnavailableMetric(
            timestamp,
            "mount_azimuth",
            deviceInfo.Azimuth,
            attributes,
            ref hasPublishedAzimuth);
    }

    private void PublishOrClearUnavailableMetric(
        DateTimeOffset timestamp,
        string metricName,
        double value,
        IReadOnlyDictionary<string, object?> attributes,
        ref bool hasPublished)
    {
        if (double.IsFinite(value))
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                metricName,
                value,
                TelemetryPriority.Normal,
                attributes));
            hasPublished = true;
        }
        else if (hasPublished)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                metricName,
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            hasPublished = false;
        }
    }

    private void PublishClearMetrics(
        string mountName,
        bool clearAltitude,
        bool clearAzimuth)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateMountAttributes(mountName);

        if (clearAltitude)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "mount_altitude",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearAzimuth)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "mount_azimuth",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }
    }

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "mount_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void ResetPublishedState()
    {
        lastConnectedMountName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags()
    {
        hasPublishedAltitude = false;
        hasPublishedAzimuth = false;
    }

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

    private static Dictionary<string, object?> CreateMountAttributes(string mountName) =>
        new()
        {
            ["mount_name"] = mountName,
        };

    private static string NormalizeMountName(string? mountName) =>
        string.IsNullOrWhiteSpace(mountName)
            ? UnknownMountName
            : mountName;
}
