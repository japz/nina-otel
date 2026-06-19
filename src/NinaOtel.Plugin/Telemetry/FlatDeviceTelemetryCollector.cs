using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class FlatDeviceTelemetryCollector : IDisposable
{
    private const string SourceName = "nina.flat_device";
    private const string UnknownLightState = "Unknown";

    private readonly object syncRoot = new();
    private readonly IFlatDeviceMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool startAttempted;
    private bool eventsEnabled;
    private bool shouldUnsubscribeConnected;
    private bool shouldUnsubscribeDisconnected;
    private bool shouldUnsubscribeOpened;
    private bool shouldUnsubscribeClosed;
    private bool shouldUnsubscribeBrightnessChanged;
    private bool shouldUnsubscribeLightToggled;

    public FlatDeviceTelemetryCollector(
        IFlatDeviceMediator mediator,
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
                shouldUnsubscribeConnected = true;
                mediator.Connected += OnConnected;

                shouldUnsubscribeDisconnected = true;
                mediator.Disconnected += OnDisconnected;

                shouldUnsubscribeOpened = true;
                mediator.Opened += OnOpened;

                shouldUnsubscribeClosed = true;
                mediator.Closed += OnClosed;

                shouldUnsubscribeBrightnessChanged = true;
                mediator.BrightnessChanged += OnBrightnessChanged;

                shouldUnsubscribeLightToggled = true;
                mediator.LightToggled += OnLightToggled;

                eventsEnabled = true;
            }
            catch (Exception ex)
            {
                eventsEnabled = false;
                CleanupFailedStart();
                PublishRegistrationFailure(ex);
            }
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
            eventsEnabled = false;
            CleanupSubscriptions();
        }
    }

    private Task OnConnected(object sender, EventArgs e)
    {
        PublishEventSafely("calibrator_connected", "Cover/Calibrator connected");
        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        PublishEventSafely("calibrator_disconnected", "Cover/Calibrator disconnected");
        return Task.CompletedTask;
    }

    private Task OnOpened(object sender, EventArgs e)
    {
        PublishEventSafely("calibrator_opened", "Cover opened");
        return Task.CompletedTask;
    }

    private Task OnClosed(object sender, EventArgs e)
    {
        PublishEventSafely("calibrator_closed", "Cover closed");
        return Task.CompletedTask;
    }

    private Task OnBrightnessChanged(object sender, FlatDeviceBrightnessChangedEventArgs e)
    {
        try
        {
            if (e is null)
            {
                return Task.CompletedTask;
            }

            PublishEvent(
                "calibrator_brightness",
                $"Calibrator brightness changed to {e.To}",
                new Dictionary<string, object?>
                {
                    ["title"] = "Calibrator brightness changed",
                    ["calibrator_brightness_from"] = e.From,
                    ["calibrator_brightness_to"] = e.To,
                });
        }
        catch
        {
            // NINA flat device events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private Task OnLightToggled(object sender, EventArgs e)
    {
        try
        {
            var state = ResolveLightState();
            PublishEvent(
                "calibrator_light_toggled",
                $"Calibrator light: {state}",
                new Dictionary<string, object?>
                {
                    ["title"] = "Calibrator light toggled",
                    ["calibrator_light_state"] = state,
                });
        }
        catch
        {
            // NINA flat device events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private void PublishEventSafely(string name, string body)
    {
        try
        {
            PublishEvent(name, body, new Dictionary<string, object?>());
        }
        catch
        {
            // NINA flat device events must never fail because telemetry is unavailable.
        }
    }

    private void PublishEvent(string name, string body, Dictionary<string, object?> attributes)
    {
        lock (syncRoot)
        {
            if (disposed || !eventsEnabled)
            {
                return;
            }

            TryPublishSafely(new TelemetryRecord(
                TelemetrySignal.Log,
                timeProvider.GetUtcNow(),
                SourceName,
                name,
                TelemetryPriority.Normal,
                attributes,
                Body: body,
                Severity: TelemetrySeverity.Information));
        }
    }

    private string ResolveLightState()
    {
        try
        {
            var state = mediator.GetInfo().LocalizedLightOnState;
            return string.IsNullOrWhiteSpace(state)
                ? UnknownLightState
                : state;
        }
        catch
        {
            return UnknownLightState;
        }
    }

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "flat_device_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void CleanupFailedStart() => CleanupSubscriptions();

    private void CleanupSubscriptions()
    {
        if (shouldUnsubscribeLightToggled)
        {
            try
            {
                mediator.LightToggled -= OnLightToggled;
                shouldUnsubscribeLightToggled = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeBrightnessChanged)
        {
            try
            {
                mediator.BrightnessChanged -= OnBrightnessChanged;
                shouldUnsubscribeBrightnessChanged = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeClosed)
        {
            try
            {
                mediator.Closed -= OnClosed;
                shouldUnsubscribeClosed = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeOpened)
        {
            try
            {
                mediator.Opened -= OnOpened;
                shouldUnsubscribeOpened = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeDisconnected)
        {
            try
            {
                mediator.Disconnected -= OnDisconnected;
                shouldUnsubscribeDisconnected = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeConnected)
        {
            try
            {
                mediator.Connected -= OnConnected;
                shouldUnsubscribeConnected = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
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
            // Telemetry publication must never affect NINA.
        }
    }
}
