using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using OxyPlot;

namespace NinaOtel.Plugin.Telemetry;

public sealed class FocuserTelemetryCollector : IFocuserConsumer, IDisposable
{
    private const string SourceName = "nina.focuser";
    private const string UnknownFocuserName = "Unknown";

    private readonly object syncRoot = new();
    private readonly IFocuserMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool hasPublishedTemperature;
    private string? lastConnectedFocuserName;
    private bool started;

    public FocuserTelemetryCollector(
        IFocuserMediator mediator,
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

    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            var focuserName = NormalizeFocuserName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedFocuserName is not null)
                {
                    PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
                    lastConnectedFocuserName = null;
                    hasPublishedTemperature = false;
                }

                return;
            }

            if (lastConnectedFocuserName is not null &&
                !string.Equals(lastConnectedFocuserName, focuserName, StringComparison.Ordinal))
            {
                PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
                hasPublishedTemperature = false;
            }

            lastConnectedFocuserName = focuserName;
            PublishCurrentMetrics(deviceInfo, focuserName);
        }
    }

    private void PublishCurrentMetrics(FocuserInfo deviceInfo, string focuserName)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateFocuserAttributes(focuserName);

        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            "focuser_position",
            deviceInfo.Position,
            TelemetryPriority.Normal,
            attributes));

        if (!double.IsNaN(deviceInfo.Temperature))
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "focuser_temperature",
                deviceInfo.Temperature,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedTemperature = true;
        }
        else if (hasPublishedTemperature)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "focuser_temperature",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
            hasPublishedTemperature = false;
        }
    }

    private void PublishClearMetrics(string focuserName, bool clearTemperature)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateFocuserAttributes(focuserName);

        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            "focuser_position",
            double.NaN,
            TelemetryPriority.Normal,
            attributes));

        if (clearTemperature)
        {
            TryPublishSafely(TelemetryRecord.Metric(
                timestamp,
                SourceName,
                "focuser_temperature",
                double.NaN,
                TelemetryPriority.Normal,
                attributes));
        }
    }

    private static Dictionary<string, object?> CreateFocuserAttributes(string focuserName) =>
        new()
        {
            ["focuser_name"] = focuserName,
        };

    private static string NormalizeFocuserName(string? focuserName) =>
        string.IsNullOrWhiteSpace(focuserName)
            ? UnknownFocuserName
            : focuserName;

    public void UpdateEndAutoFocusRun(AutoFocusInfo info)
    {
    }

    public void UpdateUserFocused(FocuserInfo info)
    {
    }

    public void AutoFocusRunStarting()
    {
    }

    public void NewAutoFocusPoint(DataPoint dataPoint)
    {
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

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "focuser_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
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
}
