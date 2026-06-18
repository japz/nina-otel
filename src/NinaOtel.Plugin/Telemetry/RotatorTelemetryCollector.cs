using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class RotatorTelemetryCollector : IRotatorConsumer, IDisposable
{
    private const string SourceName = "nina.rotator";
    private const string UnknownRotatorName = "Unknown";

    private readonly object syncRoot = new();
    private readonly IRotatorMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool hasPublishedMechanicalAngle;
    private bool hasPublishedSkyAngle;
    private string? lastConnectedRotatorName;
    private bool started;

    public RotatorTelemetryCollector(
        IRotatorMediator mediator,
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
            if (disposed || started)
            {
                return;
            }

            try
            {
                mediator.RegisterConsumer(this);
                started = true;
            }
            catch (Exception ex)
            {
                PublishRegistrationFailure(ex);
            }
        }
    }

    public void UpdateDeviceInfo(RotatorInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            var rotatorName = NormalizeRotatorName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedRotatorName is not null)
                {
                    PublishClearMetrics(
                        lastConnectedRotatorName,
                        hasPublishedMechanicalAngle,
                        hasPublishedSkyAngle);
                    ResetPublishedState();
                }

                return;
            }

            if (lastConnectedRotatorName is not null &&
                !string.Equals(lastConnectedRotatorName, rotatorName, StringComparison.Ordinal))
            {
                PublishClearMetrics(
                    lastConnectedRotatorName,
                    hasPublishedMechanicalAngle,
                    hasPublishedSkyAngle);
                ResetPublishedFlags();
            }

            lastConnectedRotatorName = rotatorName;
            PublishCurrentMetrics(deviceInfo, rotatorName);
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
            if (!started)
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

    private void PublishCurrentMetrics(RotatorInfo deviceInfo, string rotatorName)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateRotatorAttributes(rotatorName);

        PublishOrClearUnavailableMetric(
            timestamp,
            "rotator_mechanical_angle",
            deviceInfo.MechanicalPosition,
            attributes,
            ref hasPublishedMechanicalAngle);

        PublishOrClearUnavailableMetric(
            timestamp,
            "rotator_angle",
            deviceInfo.Position,
            attributes,
            ref hasPublishedSkyAngle);
    }

    private void PublishOrClearUnavailableMetric(
        DateTimeOffset timestamp,
        string metricName,
        float value,
        IReadOnlyDictionary<string, object?> attributes,
        ref bool hasPublished)
    {
        if (!float.IsNaN(value))
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
        string rotatorName,
        bool clearMechanicalAngle,
        bool clearSkyAngle)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateRotatorAttributes(rotatorName);

        if (clearMechanicalAngle)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "rotator_mechanical_angle",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearSkyAngle)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "rotator_angle",
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
            "rotator_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void ResetPublishedState()
    {
        lastConnectedRotatorName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags()
    {
        hasPublishedMechanicalAngle = false;
        hasPublishedSkyAngle = false;
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

    private static Dictionary<string, object?> CreateRotatorAttributes(string rotatorName) =>
        new()
        {
            ["rotator_name"] = rotatorName,
        };

    private static string NormalizeRotatorName(string? rotatorName) =>
        string.IsNullOrWhiteSpace(rotatorName)
            ? UnknownRotatorName
            : rotatorName;
}
