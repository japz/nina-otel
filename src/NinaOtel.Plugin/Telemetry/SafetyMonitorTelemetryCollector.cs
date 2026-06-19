using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class SafetyMonitorTelemetryCollector : ISafetyMonitorConsumer, IDisposable
{
    private const string SourceName = "nina.safety";
    private const string UnknownSafetyMonitorName = "Unknown";
    private const string SafetyGaugeName = "safety_issafe";

    private readonly object syncRoot = new();
    private readonly ISafetyMonitorMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool startAttempted;
    private bool registered;
    private bool subscribedConnected;
    private bool subscribedDisconnected;
    private bool subscribedIsSafeChanged;
    private bool hasPublishedGauge;
    private string? lastPublishedSafetyMonitorName;
    private bool? activePeriodIsSafe;
    private string? activePeriodSafetyMonitorName;
    private DateTimeOffset? activePeriodStart;
    private bool disconnectedEventLogged;

    public SafetyMonitorTelemetryCollector(
        ISafetyMonitorMediator mediator,
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

                mediator.Connected += OnConnected;
                subscribedConnected = true;

                mediator.Disconnected += OnDisconnected;
                subscribedDisconnected = true;

                mediator.IsSafeChanged += OnIsSafeChanged;
                subscribedIsSafeChanged = true;
            }
            catch (Exception ex)
            {
                PublishRegistrationFailure(ex);
            }
        }
    }

    public void UpdateDeviceInfo(SafetyMonitorInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        try
        {
            lock (syncRoot)
            {
                if (disposed)
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

            if (subscribedConnected)
            {
                try
                {
                    mediator.Connected -= OnConnected;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (subscribedDisconnected)
            {
                try
                {
                    mediator.Disconnected -= OnDisconnected;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (subscribedIsSafeChanged)
            {
                try
                {
                    mediator.IsSafeChanged -= OnIsSafeChanged;
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
            }
            catch
            {
                // Telemetry teardown must never interfere with NINA shutdown.
            }
        }
    }

    private void UpdateDeviceInfoCore(SafetyMonitorInfo deviceInfo)
    {
        var timestamp = timeProvider.GetUtcNow();

        if (!deviceInfo.Connected)
        {
            ClearGaugeIfPublished(timestamp);
            ResetGaugeState();
            CloseActivePeriod(timestamp);
            ResetPeriodState();
            return;
        }

        var safetyMonitorName = NormalizeSafetyMonitorName(deviceInfo.Name);
        var previousSafetyMonitorName = lastPublishedSafetyMonitorName;
        var monitorNameChanged = previousSafetyMonitorName is not null &&
            !string.Equals(previousSafetyMonitorName, safetyMonitorName, StringComparison.Ordinal);
        var safetyStateChanged = !monitorNameChanged &&
            activePeriodIsSafe is not null &&
            activePeriodIsSafe != deviceInfo.IsSafe;

        disconnectedEventLogged = false;

        if (monitorNameChanged && previousSafetyMonitorName is not null)
        {
            PublishClearGauge(timestamp, previousSafetyMonitorName);
            hasPublishedGauge = false;
        }

        if (safetyStateChanged)
        {
            PublishSafetyStateLog(timestamp, safetyMonitorName, deviceInfo.IsSafe);
        }

        if ((monitorNameChanged || safetyStateChanged) && activePeriodIsSafe is not null)
        {
            CloseActivePeriod(timestamp);
            ResetPeriodState();
        }

        PublishSafetyGauge(timestamp, safetyMonitorName, deviceInfo.IsSafe);
        if (activePeriodIsSafe is null)
        {
            BeginActivePeriod(timestamp, safetyMonitorName, deviceInfo.IsSafe);
        }
    }

    private Task OnConnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return Task.CompletedTask;
                }

                var timestamp = timeProvider.GetUtcNow();
                var deviceInfo = TryGetInfo();
                var safetyMonitorName = NormalizeSafetyMonitorName(
                    deviceInfo?.Name ?? lastPublishedSafetyMonitorName);

                disconnectedEventLogged = false;
                PublishNamedLog(
                    timestamp,
                    "safety_connected",
                    "Safety monitor connected.",
                    CreateSafetyAttributes(safetyMonitorName));
                if (deviceInfo is { Connected: true })
                {
                    PublishSafetyGauge(timestamp, safetyMonitorName, deviceInfo.IsSafe);
                    BeginActivePeriod(timestamp, safetyMonitorName, deviceInfo.IsSafe);
                }
            }
        }
        catch
        {
            // NINA safety monitor events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return Task.CompletedTask;
                }

                var timestamp = timeProvider.GetUtcNow();
                var safetyMonitorName = NormalizeSafetyMonitorName(
                    lastPublishedSafetyMonitorName ?? activePeriodSafetyMonitorName);

                if (disconnectedEventLogged)
                {
                    return Task.CompletedTask;
                }

                PublishNamedLog(
                    timestamp,
                    "safety_disconnected",
                    "Safety monitor disconnected.",
                    CreateSafetyAttributes(safetyMonitorName));
                ClearGaugeIfPublished(timestamp);
                CloseActivePeriod(timestamp);
                ResetGaugeState();
                ResetPeriodState();
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA safety monitor events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private void OnIsSafeChanged(object? sender, IsSafeEventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || e is null)
                {
                    return;
                }

                var timestamp = timeProvider.GetUtcNow();
                var safetyMonitorName = ResolveCurrentSafetyMonitorName();

                disconnectedEventLogged = false;
                PublishSafetyGauge(timestamp, safetyMonitorName, e.IsSafe);
                PublishSafetyStateLog(timestamp, safetyMonitorName, e.IsSafe);

                if (activePeriodIsSafe is null)
                {
                    BeginActivePeriod(timestamp, safetyMonitorName, e.IsSafe);
                }
                else if (activePeriodIsSafe != e.IsSafe)
                {
                    CloseActivePeriod(timestamp);
                    BeginActivePeriod(timestamp, safetyMonitorName, e.IsSafe);
                }
            }
        }
        catch
        {
            // NINA safety monitor events must never fail because telemetry handling failed.
        }
    }

    private void PublishSafetyGauge(DateTimeOffset timestamp, string safetyMonitorName, bool isSafe)
    {
        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            SafetyGaugeName,
            isSafe ? 1.0 : 0.0,
            TelemetryPriority.Normal,
            CreateSafetyAttributes(safetyMonitorName)));
        lastPublishedSafetyMonitorName = safetyMonitorName;
        hasPublishedGauge = true;
    }

    private void PublishSafetyStateLog(DateTimeOffset timestamp, string safetyMonitorName, bool isSafe) =>
        PublishNamedLog(
            timestamp,
            "safety_safe_state",
            $"Safety state changed to {(isSafe ? "SAFE" : "UNSAFE")}",
            CreateSafetyAttributes(
                safetyMonitorName,
                new KeyValuePair<string, object?>("title", "Safety state changed"),
                new KeyValuePair<string, object?>("safety_issafe", isSafe)));

    private void ClearGaugeIfPublished(DateTimeOffset timestamp)
    {
        if (!hasPublishedGauge || lastPublishedSafetyMonitorName is null)
        {
            return;
        }

        PublishClearGauge(timestamp, lastPublishedSafetyMonitorName);
        hasPublishedGauge = false;
    }

    private void PublishClearGauge(DateTimeOffset timestamp, string safetyMonitorName) =>
        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            SafetyGaugeName,
            double.NaN,
            TelemetryPriority.Normal,
            CreateSafetyAttributes(safetyMonitorName)));

    private void BeginActivePeriod(DateTimeOffset timestamp, string safetyMonitorName, bool isSafe)
    {
        activePeriodSafetyMonitorName = safetyMonitorName;
        activePeriodIsSafe = isSafe;
        activePeriodStart = timestamp;
    }

    private void CloseActivePeriod(DateTimeOffset timestamp)
    {
        if (activePeriodIsSafe is null ||
            activePeriodSafetyMonitorName is null ||
            activePeriodStart is null)
        {
            return;
        }

        var periodName = activePeriodIsSafe.Value
            ? "safety_safe_period"
            : "safety_unsafe_period";
        PublishNamedLog(
            activePeriodStart.Value,
            periodName,
            activePeriodIsSafe.Value
                ? "Safety monitor SAFE period ended."
                : "Safety monitor UNSAFE period ended.",
            CreateSafetyAttributes(
                activePeriodSafetyMonitorName,
                new KeyValuePair<string, object?>(
                    "timeEnd",
                    timestamp.ToUnixTimeMilliseconds())));
    }

    private string ResolveCurrentSafetyMonitorName()
    {
        if (lastPublishedSafetyMonitorName is not null)
        {
            return lastPublishedSafetyMonitorName;
        }

        if (activePeriodSafetyMonitorName is not null)
        {
            return activePeriodSafetyMonitorName;
        }

        return NormalizeSafetyMonitorName(TryGetInfo()?.Name);
    }

    private SafetyMonitorInfo? TryGetInfo()
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

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "safety_monitor_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
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

    private void ResetGaugeState()
    {
        hasPublishedGauge = false;
        lastPublishedSafetyMonitorName = null;
    }

    private void ResetPeriodState()
    {
        activePeriodIsSafe = null;
        activePeriodSafetyMonitorName = null;
        activePeriodStart = null;
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

    private static Dictionary<string, object?> CreateSafetyAttributes(
        string safetyMonitorName,
        params KeyValuePair<string, object?>[] additionalAttributes)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["safety_monitor_name"] = safetyMonitorName,
        };

        foreach (var attribute in additionalAttributes)
        {
            attributes[attribute.Key] = attribute.Value;
        }

        return attributes;
    }

    private static string NormalizeSafetyMonitorName(string? safetyMonitorName) =>
        string.IsNullOrWhiteSpace(safetyMonitorName)
            ? UnknownSafetyMonitorName
            : safetyMonitorName;
}
