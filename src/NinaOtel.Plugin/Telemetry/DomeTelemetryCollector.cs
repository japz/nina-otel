using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class DomeTelemetryCollector : IDisposable
{
    private const string SourceName = "nina.dome";

    private readonly object syncRoot = new();
    private readonly IDomeMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool startAttempted;
    private bool eventsEnabled;
    private bool shouldUnsubscribeConnected;
    private bool shouldUnsubscribeDisconnected;
    private bool shouldUnsubscribeOpened;
    private bool shouldUnsubscribeClosed;
    private bool shouldUnsubscribeHomed;
    private bool shouldUnsubscribeParked;
    private bool shouldUnsubscribeSlewed;

    public DomeTelemetryCollector(
        IDomeMediator mediator,
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

                shouldUnsubscribeHomed = true;
                mediator.Homed += OnHomed;

                shouldUnsubscribeParked = true;
                mediator.Parked += OnParked;

                shouldUnsubscribeSlewed = true;
                mediator.Slewed += OnSlewed;

                eventsEnabled = true;
            }
            catch (Exception ex)
            {
                eventsEnabled = false;
                CleanupSubscriptions();
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
        PublishEventSafely("dome_connected", "Dome connected");
        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        PublishEventSafely("dome_disconnected", "Dome disconnected");
        return Task.CompletedTask;
    }

    private Task OnOpened(object sender, EventArgs e)
    {
        PublishEventSafely("dome_shutter_open", "Dome shutter opened");
        return Task.CompletedTask;
    }

    private Task OnClosed(object sender, EventArgs e)
    {
        PublishEventSafely("dome_shutter_close", "Dome shutter closed");
        return Task.CompletedTask;
    }

    private Task OnHomed(object sender, EventArgs e)
    {
        PublishEventSafely("dome_shutter_homed", "Dome homed");
        return Task.CompletedTask;
    }

    private Task OnParked(object sender, EventArgs e)
    {
        PublishEventSafely("dome_shutter_parked", "Dome parked");
        return Task.CompletedTask;
    }

    private Task OnSlewed(object sender, DomeEventArgs e)
    {
        try
        {
            if (e is null)
            {
                return Task.CompletedTask;
            }

            PublishEvent(
                "dome_slewed",
                $"Dome slewed azimuth to {e.To:F2}\u00b0",
                new Dictionary<string, object?>
                {
                    ["title"] = "Dome slewed azimuth",
                    ["dome_slewed_from"] = e.From,
                    ["dome_slewed_to"] = e.To,
                });
        }
        catch
        {
            // NINA dome events must never fail because telemetry is unavailable.
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
            // NINA dome events must never fail because telemetry is unavailable.
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

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "dome_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void CleanupSubscriptions()
    {
        if (shouldUnsubscribeSlewed)
        {
            try
            {
                mediator.Slewed -= OnSlewed;
                shouldUnsubscribeSlewed = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeParked)
        {
            try
            {
                mediator.Parked -= OnParked;
                shouldUnsubscribeParked = false;
            }
            catch
            {
                // Startup/teardown cleanup must never interfere with NINA.
            }
        }

        if (shouldUnsubscribeHomed)
        {
            try
            {
                mediator.Homed -= OnHomed;
                shouldUnsubscribeHomed = false;
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
