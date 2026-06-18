using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

internal readonly record struct QhyCameraSensorTelemetry(
    double AirPressure,
    double Humidity);

public sealed class CameraTelemetryCollector : ICameraConsumer, IDisposable
{
    private const string SourceName = "nina.camera";
    private const string UnknownCameraName = "Unknown";

    private readonly object syncRoot = new();
    private readonly ICameraMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly Func<QhyCameraSensorTelemetry?> qhySensorTelemetryProvider;
    private bool disposed;
    private bool hasPublishedBattery;
    private bool hasPublishedCoolerPower;
    private bool hasPublishedQhyAirPressure;
    private bool hasPublishedQhyHumidity;
    private bool hasPublishedTemperature;
    private string? lastConnectedCameraName;
    private bool started;

    public CameraTelemetryCollector(
        ICameraMediator mediator,
        ITelemetrySink sink,
        TimeProvider timeProvider)
        : this(mediator, sink, timeProvider, null)
    {
    }

    internal CameraTelemetryCollector(
        ICameraMediator mediator,
        ITelemetrySink sink,
        TimeProvider timeProvider,
        Func<QhyCameraSensorTelemetry?>? qhySensorTelemetryProvider = null)
    {
        this.mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.qhySensorTelemetryProvider = qhySensorTelemetryProvider ?? ReadQhySensorTelemetry;
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

    public void UpdateDeviceInfo(CameraInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            var cameraName = NormalizeCameraName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedCameraName is not null)
                {
                    PublishClearMetrics(
                        lastConnectedCameraName,
                        hasPublishedTemperature,
                        hasPublishedCoolerPower,
                        hasPublishedBattery,
                        hasPublishedQhyAirPressure,
                        hasPublishedQhyHumidity);
                    ResetPublishedState();
                }

                return;
            }

            if (lastConnectedCameraName is not null &&
                !string.Equals(lastConnectedCameraName, cameraName, StringComparison.Ordinal))
            {
                PublishClearMetrics(
                    lastConnectedCameraName,
                    hasPublishedTemperature,
                    hasPublishedCoolerPower,
                    hasPublishedBattery,
                    hasPublishedQhyAirPressure,
                    hasPublishedQhyHumidity);
                ResetPublishedFlags();
            }

            lastConnectedCameraName = cameraName;
            PublishCurrentMetrics(deviceInfo, cameraName);
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

    private void PublishCurrentMetrics(CameraInfo deviceInfo, string cameraName)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateCameraAttributes(cameraName);

        PublishOrClearUnavailableMetric(
            timestamp,
            "camera_sensor_temperature",
            deviceInfo.Temperature,
            attributes,
            ref hasPublishedTemperature);

        PublishOrClearUnavailableMetric(
            timestamp,
            "camera_cooler_power",
            deviceInfo.CoolerPower,
            attributes,
            ref hasPublishedCoolerPower);

        if (deviceInfo.HasBattery && deviceInfo.Battery >= 0)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "camera_battery_level",
                deviceInfo.Battery,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedBattery = true;
        }
        else if (hasPublishedBattery)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "camera_battery_level",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedBattery = false;
        }

        PublishQhySensorMetrics(timestamp, attributes);
    }

    private void PublishOrClearUnavailableMetric(
        DateTimeOffset timestamp,
        string metricName,
        double value,
        IReadOnlyDictionary<string, object?> attributes,
        ref bool hasPublished)
    {
        if (!double.IsNaN(value))
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
        string cameraName,
        bool clearTemperature,
        bool clearCoolerPower,
        bool clearBattery,
        bool clearQhyAirPressure,
        bool clearQhyHumidity)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateCameraAttributes(cameraName);

        if (clearTemperature)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "camera_sensor_temperature",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearCoolerPower)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "camera_cooler_power",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearBattery)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "camera_battery_level",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearQhyAirPressure)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "qhy_sensor_air_pressure",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }

        if (clearQhyHumidity)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "qhy_sensor_humidity",
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
            "camera_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void ResetPublishedState()
    {
        lastConnectedCameraName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags()
    {
        hasPublishedTemperature = false;
        hasPublishedCoolerPower = false;
        hasPublishedBattery = false;
        hasPublishedQhyAirPressure = false;
        hasPublishedQhyHumidity = false;
    }

    private void PublishQhySensorMetrics(
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?> attributes)
    {
        QhyCameraSensorTelemetry? qhySensorTelemetry;

        try
        {
            qhySensorTelemetry = qhySensorTelemetryProvider();
        }
        catch
        {
            qhySensorTelemetry = null;
        }

        if (qhySensorTelemetry is not { } telemetry)
        {
            ClearQhySensorMetricsIfNeeded(timestamp, attributes);
            return;
        }

        PublishOrClearUnavailableMetric(
            timestamp,
            "qhy_sensor_air_pressure",
            telemetry.AirPressure,
            attributes,
            ref hasPublishedQhyAirPressure);
        PublishOrClearUnavailableMetric(
            timestamp,
            "qhy_sensor_humidity",
            telemetry.Humidity,
            attributes,
            ref hasPublishedQhyHumidity);
    }

    private void ClearQhySensorMetricsIfNeeded(
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?> attributes)
    {
        if (hasPublishedQhyAirPressure)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "qhy_sensor_air_pressure",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedQhyAirPressure = false;
        }

        if (hasPublishedQhyHumidity)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "qhy_sensor_humidity",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedQhyHumidity = false;
        }
    }

    private QhyCameraSensorTelemetry? ReadQhySensorTelemetry()
    {
        object camera;

        try
        {
            camera = mediator.GetDevice();
        }
        catch
        {
            return null;
        }

        camera = camera is PersistSettingsCameraDecorator decorator
            ? decorator.Camera
            : camera;

        return camera is QHYCamera qhyCamera
            ? new QhyCameraSensorTelemetry(
                qhyCamera.QhyHasSensorAirPressure
                    ? qhyCamera.QhySensorAirPressure
                    : double.NaN,
                qhyCamera.QhyHasSensorHumidity
                    ? qhyCamera.QhySensorHumidity
                    : double.NaN)
            : null;
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

    private static Dictionary<string, object?> CreateCameraAttributes(string cameraName) =>
        new()
        {
            ["camera_name"] = cameraName,
        };

    private static string NormalizeCameraName(string? cameraName) =>
        string.IsNullOrWhiteSpace(cameraName)
            ? UnknownCameraName
            : cameraName;
}
