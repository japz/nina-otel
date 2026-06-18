using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class WeatherTelemetryCollector : IWeatherDataConsumer, IDisposable
{
    private const string SourceName = "nina.weather";
    private const string UnknownWeatherDeviceName = "Unknown";

    private readonly object syncRoot = new();
    private readonly IWeatherDataMediator mediator;
    private readonly WeatherMetricState[] metrics =
    [
        new("wx_cloud_cover", static deviceInfo => deviceInfo.CloudCover),
        new("wx_dewpoint", static deviceInfo => deviceInfo.DewPoint),
        new("wx_humidity", static deviceInfo => deviceInfo.Humidity),
        new("wx_pressure", static deviceInfo => deviceInfo.Pressure),
        new("wx_rain_rate", static deviceInfo => deviceInfo.RainRate),
        new("wx_sky_brightness", static deviceInfo => deviceInfo.SkyBrightness),
        new("wx_sky_quality", static deviceInfo => deviceInfo.SkyQuality),
        new("wx_sky_temperature", static deviceInfo => deviceInfo.SkyTemperature),
        new("wx_star_fwhm", static deviceInfo => deviceInfo.StarFWHM),
        new("wx_temperature", static deviceInfo => deviceInfo.Temperature),
        new("wx_wind_direction", static deviceInfo => deviceInfo.WindDirection),
        new("wx_wind_gust", static deviceInfo => deviceInfo.WindGust),
        new("wx_wind_speed", static deviceInfo => deviceInfo.WindSpeed),
    ];
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private string? lastConnectedWeatherDeviceName;
    private bool startAttempted;
    private bool registered;

    public WeatherTelemetryCollector(
        IWeatherDataMediator mediator,
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

    public void UpdateDeviceInfo(WeatherDataInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            var weatherDeviceName = NormalizeWeatherDeviceName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedWeatherDeviceName is not null)
                {
                    PublishClearMetrics(timeProvider.GetUtcNow(), lastConnectedWeatherDeviceName);
                    ResetPublishedState();
                }

                return;
            }

            var timestamp = timeProvider.GetUtcNow();
            if (lastConnectedWeatherDeviceName is not null &&
                !string.Equals(lastConnectedWeatherDeviceName, weatherDeviceName, StringComparison.Ordinal))
            {
                PublishClearMetrics(timestamp, lastConnectedWeatherDeviceName);
                ResetPublishedFlags();
            }

            lastConnectedWeatherDeviceName = weatherDeviceName;
            PublishCurrentMetrics(timestamp, deviceInfo, weatherDeviceName);
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

    private void PublishCurrentMetrics(
        DateTimeOffset timestamp,
        WeatherDataInfo deviceInfo,
        string weatherDeviceName)
    {
        var attributes = CreateWeatherAttributes(weatherDeviceName);
        foreach (var metric in metrics)
        {
            PublishOrClearUnavailableMetric(
                timestamp,
                metric,
                metric.ReadValue(deviceInfo),
                attributes);
        }
    }

    private void PublishOrClearUnavailableMetric(
        DateTimeOffset timestamp,
        WeatherMetricState metric,
        double value,
        IReadOnlyDictionary<string, object?> attributes)
    {
        if (double.IsFinite(value))
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                metric.Name,
                value,
                TelemetryPriority.Normal,
                attributes));
            metric.HasPublished = true;
        }
        else if (metric.HasPublished)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                metric.Name,
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            metric.HasPublished = false;
        }
    }

    private void PublishClearMetrics(DateTimeOffset timestamp, string weatherDeviceName)
    {
        var attributes = CreateWeatherAttributes(weatherDeviceName);

        foreach (var metric in metrics)
        {
            if (!metric.HasPublished)
            {
                continue;
            }

            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                metric.Name,
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
            "weather_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void ResetPublishedState()
    {
        lastConnectedWeatherDeviceName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags()
    {
        foreach (var metric in metrics)
        {
            metric.HasPublished = false;
        }
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

    private static Dictionary<string, object?> CreateWeatherAttributes(string weatherDeviceName) =>
        new()
        {
            ["wx_device_name"] = weatherDeviceName,
        };

    private static string NormalizeWeatherDeviceName(string? weatherDeviceName) =>
        string.IsNullOrWhiteSpace(weatherDeviceName)
            ? UnknownWeatherDeviceName
            : weatherDeviceName;

    private sealed class WeatherMetricState(
        string name,
        Func<WeatherDataInfo, double> readValue)
    {
        public string Name { get; } = name;

        public Func<WeatherDataInfo, double> ReadValue { get; } = readValue;

        public bool HasPublished { get; set; }
    }
}
