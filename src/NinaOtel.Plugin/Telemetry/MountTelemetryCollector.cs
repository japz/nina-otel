using NINA.Astrometry;
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
    private string? lastDisconnectedMountName;
    private bool startAttempted;
    private bool startupFailed;
    private bool registrationAttempted;
    private bool registered;
    private bool connectedSubscriptionAttempted;
    private bool disconnectedSubscriptionAttempted;
    private bool parkedSubscriptionAttempted;
    private bool unparkedSubscriptionAttempted;
    private bool homedSubscriptionAttempted;
    private bool slewedSubscriptionAttempted;
    private bool subscribedConnected;
    private bool subscribedDisconnected;
    private bool subscribedParked;
    private bool subscribedUnparked;
    private bool subscribedHomed;
    private bool subscribedSlewed;
    private bool lifecycleEventsEnabled;
    private bool disconnectedEventLogged;

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
                registrationAttempted = true;
                mediator.RegisterConsumer(this);
                registered = true;

                connectedSubscriptionAttempted = true;
                mediator.Connected += OnConnected;
                subscribedConnected = true;

                disconnectedSubscriptionAttempted = true;
                mediator.Disconnected += OnDisconnected;
                subscribedDisconnected = true;

                parkedSubscriptionAttempted = true;
                mediator.Parked += OnParked;
                subscribedParked = true;

                unparkedSubscriptionAttempted = true;
                mediator.Unparked += OnUnparked;
                subscribedUnparked = true;

                homedSubscriptionAttempted = true;
                mediator.Homed += OnHomed;
                subscribedHomed = true;

                slewedSubscriptionAttempted = true;
                mediator.Slewed += OnSlewed;
                subscribedSlewed = true;

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

    public void UpdateDeviceInfo(TelescopeInfo deviceInfo)
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

                var mountName = NormalizeMountName(deviceInfo.Name);
                if (!deviceInfo.Connected)
                {
                    if (lastConnectedMountName is not null)
                    {
                        lastDisconnectedMountName = lastConnectedMountName;
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

                disconnectedEventLogged = false;
                lastDisconnectedMountName = null;
                lastConnectedMountName = mountName;
                PublishCurrentMetrics(deviceInfo, mountName);
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

            if (slewedSubscriptionAttempted || subscribedSlewed)
            {
                try
                {
                    mediator.Slewed -= OnSlewed;
                    subscribedSlewed = false;
                    slewedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (homedSubscriptionAttempted || subscribedHomed)
            {
                try
                {
                    mediator.Homed -= OnHomed;
                    subscribedHomed = false;
                    homedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (unparkedSubscriptionAttempted || subscribedUnparked)
            {
                try
                {
                    mediator.Unparked -= OnUnparked;
                    subscribedUnparked = false;
                    unparkedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (parkedSubscriptionAttempted || subscribedParked)
            {
                try
                {
                    mediator.Parked -= OnParked;
                    subscribedParked = false;
                    parkedSubscriptionAttempted = false;
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

            if (!registered && !registrationAttempted)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
                registrationAttempted = false;
            }
            catch
            {
                // Telemetry teardown must never interfere with NINA shutdown.
            }
        }
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

                var deviceInfo = TryGetInfo();
                var mountName = deviceInfo is { Connected: true }
                    ? NormalizeMountName(deviceInfo.Name)
                    : NormalizeMountName(lastConnectedMountName);

                disconnectedEventLogged = false;
                lastDisconnectedMountName = null;
                if (deviceInfo is { Connected: true })
                {
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
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "mount_connected",
                    "Mount connected",
                    TelemetryPriority.Normal,
                    CreateMountAttributes(mountName));
            }
        }
        catch
        {
            // NINA mount events must never fail because telemetry handling failed.
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

                var shouldClearMetrics = lastConnectedMountName is not null;
                var mountName = ResolveDisconnectedMountName();

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "mount_disconnected",
                    "Mount disconnected",
                    TelemetryPriority.Important,
                    CreateMountAttributes(mountName));
                if (shouldClearMetrics)
                {
                    PublishClearMetrics(mountName, hasPublishedAltitude, hasPublishedAzimuth);
                }

                ResetPublishedState();
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA mount events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task OnParked(object sender, EventArgs e) =>
        PublishMountLifecycleLog("mount_parked", "Mount has parked", TelemetryPriority.Normal);

    private Task OnUnparked(object sender, EventArgs e) =>
        PublishMountLifecycleLog("mount_unparked", "Mount has unparked", TelemetryPriority.Normal);

    private Task OnHomed(object sender, EventArgs e) =>
        PublishMountLifecycleLog("mount_homed", "Mount has homed", TelemetryPriority.Normal);

    private Task OnSlewed(object sender, MountSlewedEventArgs? e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "mount_slewed",
                    CreateSlewedBody(e),
                    TelemetryPriority.Normal,
                    CreateSlewedAttributes(ResolveCurrentMountName(), e));
            }
        }
        catch
        {
            // NINA mount events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task PublishMountLifecycleLog(
        string name,
        string body,
        TelemetryPriority priority)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    name,
                    body,
                    priority,
                    CreateMountAttributes(ResolveCurrentMountName()));
            }
        }
        catch
        {
            // NINA mount events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
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

    private void CleanupFailedStart()
    {
        if (slewedSubscriptionAttempted)
        {
            try
            {
                mediator.Slewed -= OnSlewed;
                subscribedSlewed = false;
                slewedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (homedSubscriptionAttempted)
        {
            try
            {
                mediator.Homed -= OnHomed;
                subscribedHomed = false;
                homedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (unparkedSubscriptionAttempted)
        {
            try
            {
                mediator.Unparked -= OnUnparked;
                subscribedUnparked = false;
                unparkedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (parkedSubscriptionAttempted)
        {
            try
            {
                mediator.Parked -= OnParked;
                subscribedParked = false;
                parkedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

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

        if (registered || registrationAttempted)
        {
            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
                registrationAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (lastConnectedMountName is not null)
        {
            PublishClearMetrics(lastConnectedMountName, hasPublishedAltitude, hasPublishedAzimuth);
        }

        ResetPublishedState();
        disconnectedEventLogged = false;
    }

    private void PublishNamedLog(
        DateTimeOffset timestamp,
        string name,
        string body,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?> attributes) =>
        TryPublishSafely(new TelemetryRecord(
            TelemetrySignal.Log,
            timestamp,
            SourceName,
            name,
            priority,
            attributes,
            Body: body,
            Severity: TelemetrySeverity.Information));

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

    private string ResolveCurrentMountName()
    {
        var deviceInfo = TryGetInfo();
        return deviceInfo is { Connected: true }
            ? NormalizeMountName(deviceInfo.Name)
            : NormalizeMountName(lastConnectedMountName ?? lastDisconnectedMountName);
    }

    private string ResolveDisconnectedMountName() =>
        lastConnectedMountName ??
        lastDisconnectedMountName ??
        NormalizeMountName(TryGetInfo()?.Name);

    private TelescopeInfo? TryGetInfo()
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

    private static string CreateSlewedBody(MountSlewedEventArgs? e)
    {
        try
        {
            var toCoords = e?.To.Transform(Epoch.J2000);
            return toCoords is null
                ? "Mount slewed"
                : $"Mount slewed to {toCoords.RAString}, {toCoords.DecString} ({toCoords.Epoch})";
        }
        catch
        {
            return "Mount slewed";
        }
    }

    private static Dictionary<string, object?> CreateSlewedAttributes(
        string mountName,
        MountSlewedEventArgs? e)
    {
        var attributes = CreateMountAttributes(mountName);
        try
        {
            var fromCoords = e?.From.Transform(Epoch.J2000);
            var toCoords = e?.To.Transform(Epoch.J2000);
            if (fromCoords is null || toCoords is null)
            {
                return attributes;
            }

            attributes["mount_slew_from_ra"] = fromCoords.RAString;
            attributes["mount_slew_from_dec"] = fromCoords.DecString;
            attributes["mount_slew_to_ra"] = toCoords.RAString;
            attributes["mount_slew_to_dec"] = toCoords.DecString;
        }
        catch
        {
            // Coordinate details are best-effort; the lifecycle log itself is still useful.
        }

        return attributes;
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
