using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using System.Globalization;
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
    private string? lastDisconnectedFocuserName;
    private bool startAttempted;
    private bool startupFailed;
    private bool registered;
    private bool connectedSubscriptionAttempted;
    private bool disconnectedSubscriptionAttempted;
    private bool subscribedConnected;
    private bool subscribedDisconnected;
    private bool lifecycleEventsEnabled;
    private bool disconnectedEventLogged;

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
            if (disposed || startAttempted)
            {
                return;
            }

            startAttempted = true;
            try
            {
                mediator.RegisterConsumer(this);
                registered = true;

                connectedSubscriptionAttempted = true;
                mediator.Connected += OnConnected;
                subscribedConnected = true;

                disconnectedSubscriptionAttempted = true;
                mediator.Disconnected += OnDisconnected;
                subscribedDisconnected = true;
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

    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
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

                var focuserName = NormalizeFocuserName(deviceInfo.Name);
                if (!deviceInfo.Connected)
                {
                    if (lastConnectedFocuserName is not null)
                    {
                        lastDisconnectedFocuserName = lastConnectedFocuserName;
                        PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
                        ResetPublishedState();
                    }

                    return;
                }

                if (lastConnectedFocuserName is not null &&
                    !string.Equals(lastConnectedFocuserName, focuserName, StringComparison.Ordinal))
                {
                    PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
                    hasPublishedTemperature = false;
                }

                disconnectedEventLogged = false;
                lastDisconnectedFocuserName = null;
                lastConnectedFocuserName = focuserName;
                PublishCurrentMetrics(deviceInfo, focuserName);
            }
        }
        catch
        {
            // NINA equipment callbacks must never fail because telemetry handling failed.
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
        if (info is null)
        {
            return;
        }

        try
        {
            PublishAutofocusCompleted(info);
        }
        catch
        {
            // Autofocus callbacks are NINA-owned; telemetry must not affect focusing.
        }
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
            lifecycleEventsEnabled = false;

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

            if (!registered)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
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
                var focuserName = deviceInfo is { Connected: true }
                    ? NormalizeFocuserName(deviceInfo.Name)
                    : NormalizeFocuserName(lastConnectedFocuserName);

                disconnectedEventLogged = false;
                lastDisconnectedFocuserName = null;
                if (deviceInfo is { Connected: true })
                {
                    if (lastConnectedFocuserName is not null &&
                        !string.Equals(lastConnectedFocuserName, focuserName, StringComparison.Ordinal))
                    {
                        PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
                        hasPublishedTemperature = false;
                    }

                    lastConnectedFocuserName = focuserName;
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "focuser_connected",
                    "Focuser connected",
                    TelemetryPriority.Normal,
                    CreateFocuserAttributes(focuserName));
            }
        }
        catch
        {
            // NINA focuser events must never fail because telemetry handling failed.
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

                var shouldClearMetrics = lastConnectedFocuserName is not null;
                var focuserName = ResolveDisconnectedFocuserName();

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "focuser_disconnected",
                    "Focuser disconnected",
                    TelemetryPriority.Important,
                    CreateFocuserAttributes(focuserName));
                if (shouldClearMetrics)
                {
                    PublishClearMetrics(focuserName, hasPublishedTemperature);
                }

                ResetPublishedState();
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA focuser events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
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

    private void PublishAutofocusCompleted(AutoFocusInfo info)
    {
        var timestamp = timeProvider.GetUtcNow();
        var attributes = CreateAutofocusAttributes(info);
        var spanId = CreateAutofocusSpanId(timestamp, attributes);

        TryPublishSafely(TelemetryRecord.Span(
            timestamp,
            SourceName,
            "nina.autofocus",
            SpanEventKind.Stop,
            spanId,
            TelemetryPriority.Normal,
            attributes));
    }

    private Dictionary<string, object?> CreateAutofocusAttributes(AutoFocusInfo info)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["autofocus_position"] = NormalizeAutofocusPosition(info.Position),
            ["autofocus_temperature"] = NormalizeAutofocusTemperature(info.Temperature),
            ["autofocus_filter"] = NormalizeAutofocusFilter(info.Filter),
        };

        lock (syncRoot)
        {
            if (lastConnectedFocuserName is not null)
            {
                attributes["focuser_name"] = lastConnectedFocuserName;
            }
        }

        return attributes;
    }

    private static int NormalizeAutofocusPosition(double position)
    {
        if (!double.IsFinite(position))
        {
            return 0;
        }

        if (position > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (position < int.MinValue)
        {
            return int.MinValue;
        }

        return Convert.ToInt32(position);
    }

    private static double NormalizeAutofocusTemperature(double temperature) =>
        double.IsNaN(temperature)
            ? 0d
            : temperature;

    private static string NormalizeAutofocusFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? "Unknown"
            : filter;

    private static string CreateAutofocusSpanId(
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?> attributes) =>
        string.Join(
            "|",
            [
                "nina.autofocus",
                $"completed={timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
                $"filter={AttributeValue(attributes, "autofocus_filter")}",
                $"position={AttributeValue(attributes, "autofocus_position")}",
                $"temperature={AttributeValue(attributes, "autofocus_temperature")}",
                $"focuser={AttributeValue(attributes, "focuser_name")}",
            ]);

    private static string AttributeValue(
        IReadOnlyDictionary<string, object?> attributes,
        string name) =>
        attributes.TryGetValue(name, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private void CleanupFailedStart()
    {
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

        if (registered)
        {
            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (lastConnectedFocuserName is not null)
        {
            PublishClearMetrics(lastConnectedFocuserName, hasPublishedTemperature);
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
        lastConnectedFocuserName = null;
        hasPublishedTemperature = false;
    }

    private string ResolveDisconnectedFocuserName() =>
        lastConnectedFocuserName ?? lastDisconnectedFocuserName ?? NormalizeFocuserName(TryGetInfo()?.Name);

    private FocuserInfo? TryGetInfo()
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
