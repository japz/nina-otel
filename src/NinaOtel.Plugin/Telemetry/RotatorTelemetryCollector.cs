using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using System.Globalization;

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
    private string? lastDisconnectedRotatorName;
    private bool startAttempted;
    private bool startupFailed;
    private bool registrationAttempted;
    private bool registered;
    private bool shouldUnsubscribeConnected;
    private bool shouldUnsubscribeDisconnected;
    private bool shouldUnsubscribeMoved;
    private bool lifecycleEventsEnabled;
    private bool movedEventsEnabled;
    private bool disconnectedEventLogged;
    private long rotatorMoveSequence;

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

                shouldUnsubscribeMoved = true;
                mediator.Moved += OnMoved;

                shouldUnsubscribeConnected = true;
                mediator.Connected += OnConnected;

                shouldUnsubscribeDisconnected = true;
                mediator.Disconnected += OnDisconnected;

                lifecycleEventsEnabled = true;
                movedEventsEnabled = true;
            }
            catch (Exception ex)
            {
                startupFailed = true;
                lifecycleEventsEnabled = false;
                movedEventsEnabled = false;
                CleanupFailedStart();
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
            if (disposed || startupFailed)
            {
                return;
            }

            var rotatorName = NormalizeRotatorName(deviceInfo.Name);
            if (!deviceInfo.Connected)
            {
                if (lastConnectedRotatorName is not null)
                {
                    lastDisconnectedRotatorName = lastConnectedRotatorName;
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

            disconnectedEventLogged = false;
            lastDisconnectedRotatorName = null;
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
            lifecycleEventsEnabled = false;
            movedEventsEnabled = false;
            TryUnsubscribeDisconnected();
            TryUnsubscribeConnected();
            TryUnsubscribeMoved();
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
                if (disposed || startupFailed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var deviceInfo = TryGetInfo();
                var rotatorName = deviceInfo is { Connected: true }
                    ? NormalizeRotatorName(deviceInfo.Name)
                    : ResolveConnectedRotatorName();

                if (lastConnectedRotatorName is not null &&
                    !string.Equals(lastConnectedRotatorName, rotatorName, StringComparison.Ordinal))
                {
                    PublishClearMetrics(
                        lastConnectedRotatorName,
                        hasPublishedMechanicalAngle,
                        hasPublishedSkyAngle);
                    ResetPublishedFlags();
                }

                disconnectedEventLogged = false;
                lastDisconnectedRotatorName = null;
                lastConnectedRotatorName = rotatorName;

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "rotator_connected",
                    "Rotator connected",
                    CreateRotatorAttributes(rotatorName));
            }
        }
        catch
        {
            // NINA rotator lifecycle events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || startupFailed || disconnectedEventLogged || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var shouldClearMetrics = lastConnectedRotatorName is not null;
                var rotatorName = ResolveDisconnectedRotatorName();

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "rotator_disconnected",
                    "Rotator disconnected",
                    CreateRotatorAttributes(rotatorName));

                if (shouldClearMetrics)
                {
                    PublishClearMetrics(
                        rotatorName,
                        hasPublishedMechanicalAngle,
                        hasPublishedSkyAngle);
                }

                ResetPublishedState();
                lastDisconnectedRotatorName = rotatorName;
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA rotator lifecycle events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private Task OnMoved(object sender, RotatorEventArgs args)
    {
        try
        {
            if (args is null)
            {
                return Task.CompletedTask;
            }

            var records = CreateRotatorMovedRecords(args);
            if (records is not null)
            {
                TryPublishSafely(records.Value.Span);
                TryPublishSafely(records.Value.Log);
            }
        }
        catch
        {
            // NINA rotator events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
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

    private (TelemetryRecord Span, TelemetryRecord Log)? CreateRotatorMovedRecords(RotatorEventArgs args)
    {
        lock (syncRoot)
        {
            if (disposed || !movedEventsEnabled)
            {
                return null;
            }

            var timestamp = timeProvider.GetUtcNow();
            var sequence = ++rotatorMoveSequence;
            var attributes = CreateRotatorMovedAttributes(
                ResolveCurrentRotatorName(),
                args.From,
                args.To);
            var text = CreateRotatorMovedText(args.To);

            var span = TelemetryRecord.Span(
                timestamp,
                SourceName,
                "nina.rotator_moved",
                SpanEventKind.Stop,
                CreateRotatorMovedSpanId(timestamp, sequence, attributes),
                TelemetryPriority.Normal,
                attributes);
            var log = new TelemetryRecord(
                TelemetrySignal.Log,
                timestamp,
                SourceName,
                "rotator_moved",
                TelemetryPriority.Normal,
                CreateRotatorMovedLogAttributes(attributes, text),
                Body: text,
                Severity: TelemetrySeverity.Information);

            return (span, log);
        }
    }

    private void CleanupFailedStart()
    {
        TryUnsubscribeDisconnected();
        TryUnsubscribeConnected();
        TryUnsubscribeMoved();

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

        if (lastConnectedRotatorName is not null)
        {
            PublishClearMetrics(
                lastConnectedRotatorName,
                hasPublishedMechanicalAngle,
                hasPublishedSkyAngle);
        }

        ResetPublishedState();
        lastDisconnectedRotatorName = null;
        disconnectedEventLogged = false;
    }

    private void TryUnsubscribeDisconnected()
    {
        if (!shouldUnsubscribeDisconnected)
        {
            return;
        }

        try
        {
            mediator.Disconnected -= OnDisconnected;
            shouldUnsubscribeDisconnected = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private void TryUnsubscribeConnected()
    {
        if (!shouldUnsubscribeConnected)
        {
            return;
        }

        try
        {
            mediator.Connected -= OnConnected;
            shouldUnsubscribeConnected = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private void TryUnsubscribeMoved()
    {
        if (!shouldUnsubscribeMoved)
        {
            return;
        }

        try
        {
            mediator.Moved -= OnMoved;
            shouldUnsubscribeMoved = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
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

    private string ResolveConnectedRotatorName() =>
        lastConnectedRotatorName ??
        lastDisconnectedRotatorName ??
        UnknownRotatorName;

    private string ResolveDisconnectedRotatorName() =>
        lastConnectedRotatorName ??
        lastDisconnectedRotatorName ??
        NormalizeRotatorName(TryGetInfo()?.Name);

    private RotatorInfo? TryGetInfo()
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

    private static Dictionary<string, object?> CreateRotatorAttributes(string rotatorName) =>
        new()
        {
            ["rotator_name"] = rotatorName,
        };

    private string ResolveCurrentRotatorName() =>
        lastConnectedRotatorName is null
            ? UnknownRotatorName
            : lastConnectedRotatorName;

    private static Dictionary<string, object?> CreateRotatorMovedAttributes(
        string rotatorName,
        float from,
        float to) =>
        new()
        {
            ["rotator_name"] = rotatorName,
            ["rotator_moved_from"] = from,
            ["rotator_moved_to"] = to,
        };

    private static Dictionary<string, object?> CreateRotatorMovedLogAttributes(
        IReadOnlyDictionary<string, object?> movementAttributes,
        string text) =>
        new()
        {
            ["rotator_name"] = movementAttributes["rotator_name"],
            ["title"] = "Rotator moved",
            ["text"] = text,
            ["rotator_moved_from"] = movementAttributes["rotator_moved_from"],
            ["rotator_moved_to"] = movementAttributes["rotator_moved_to"],
        };

    private static string CreateRotatorMovedText(float to) =>
        string.Format(CultureInfo.InvariantCulture, "Rotator moved to {0:F2}\u00B0", to);

    private static string CreateRotatorMovedSpanId(
        DateTimeOffset timestamp,
        long sequence,
        IReadOnlyDictionary<string, object?> attributes) =>
        string.Join(
            "|",
            "rotator_moved",
            timestamp.ToUniversalTime().ToString("O"),
            sequence,
            attributes["rotator_name"],
            Convert.ToString(attributes["rotator_moved_from"], CultureInfo.InvariantCulture),
            Convert.ToString(attributes["rotator_moved_to"], CultureInfo.InvariantCulture));

    private static string NormalizeRotatorName(string? rotatorName) =>
        string.IsNullOrWhiteSpace(rotatorName)
            ? UnknownRotatorName
            : rotatorName;
}
