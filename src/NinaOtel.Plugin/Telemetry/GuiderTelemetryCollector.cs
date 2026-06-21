using System.Globalization;
using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class GuiderTelemetryCollector : IGuiderConsumer, IDisposable
{
    private const string SourceName = "nina.guider";
    private const string UnknownGuiderName = "Unknown";

    private readonly object syncRoot = new();
    private readonly IGuiderMediator mediator;
    private readonly GuiderMetricState[] metrics =
    [
        new("guider_rms_ra_arcsec", static (snapshot, _) => snapshot.RmsRaArcseconds),
        new("guider_rms_dec_arcsec", static (snapshot, _) => snapshot.RmsDecArcseconds),
        new("guider_rms_arcsec", static (snapshot, _) => snapshot.RmsTotalArcseconds),
        new("guider_rms_ra_pixel", static (snapshot, _) => snapshot.RmsRaPixel),
        new("guider_rms_dec_pixel", static (snapshot, _) => snapshot.RmsDecPixel),
        new("guider_rms_pixel", static (snapshot, _) => snapshot.RmsTotalPixel),
        new("guider_rms_peak_ra_arcsec", static (snapshot, _) => snapshot.RmsPeakRaArcseconds),
        new("guider_rms_peak_dec_arcsec", static (snapshot, _) => snapshot.RmsPeakDecArcseconds),
        new("guider_rms_peak_arcsec", static (snapshot, _) => snapshot.RmsPeakArcseconds),
        new("guider_rms_peak_ra_pixel", static (snapshot, _) => snapshot.RmsPeakRaPixel),
        new("guider_rms_peak_dec_pixel", static (snapshot, _) => snapshot.RmsPeakDecPixel),
        new("guider_rms_peak_pixel", static (snapshot, _) => snapshot.RmsPeakPixel),
        new("guider_ra_distance", static (_, guideStep) => guideStep.RADistanceRaw),
        new("guider_ra_duration", static (_, guideStep) => guideStep.RADuration),
        new("guider_dec_distance", static (_, guideStep) => guideStep.DECDistanceRaw),
        new("guider_dec_duration", static (_, guideStep) => guideStep.DECDuration),
    ];
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private string? lastConnectedGuiderName;
    private string? lastDisconnectedGuiderName;
    private GuiderInfoSnapshot? currentGuiderSnapshot;
    private bool startAttempted;
    private bool startupFailed;
    private bool registrationAttempted;
    private bool registered;
    private bool connectedSubscriptionAttempted;
    private bool disconnectedSubscriptionAttempted;
    private bool guideEventSubscriptionAttempted;
    private bool guidingStartedSubscriptionAttempted;
    private bool guidingStoppedSubscriptionAttempted;
    private bool afterDitherSubscriptionAttempted;
    private bool subscribedConnected;
    private bool subscribedDisconnected;
    private bool subscribedToGuideEvent;
    private bool subscribedGuidingStarted;
    private bool subscribedGuidingStopped;
    private bool subscribedAfterDither;
    private bool lifecycleEventsEnabled;
    private bool disconnectedEventLogged;
    private string? pendingClearGuiderName;

    public GuiderTelemetryCollector(
        IGuiderMediator mediator,
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

                guideEventSubscriptionAttempted = true;
                mediator.GuideEvent += OnGuideEvent;
                subscribedToGuideEvent = true;

                guidingStartedSubscriptionAttempted = true;
                mediator.GuidingStarted += OnGuidingStarted;
                subscribedGuidingStarted = true;

                guidingStoppedSubscriptionAttempted = true;
                mediator.GuidingStopped += OnGuidingStopped;
                subscribedGuidingStopped = true;

                afterDitherSubscriptionAttempted = true;
                mediator.AfterDither += OnAfterDither;
                subscribedAfterDither = true;

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

    public void UpdateDeviceInfo(GuiderInfo deviceInfo)
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
            lifecycleEventsEnabled = false;

            if (afterDitherSubscriptionAttempted || subscribedAfterDither)
            {
                try
                {
                    mediator.AfterDither -= OnAfterDither;
                    subscribedAfterDither = false;
                    afterDitherSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (guidingStoppedSubscriptionAttempted || subscribedGuidingStopped)
            {
                try
                {
                    mediator.GuidingStopped -= OnGuidingStopped;
                    subscribedGuidingStopped = false;
                    guidingStoppedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (guidingStartedSubscriptionAttempted || subscribedGuidingStarted)
            {
                try
                {
                    mediator.GuidingStarted -= OnGuidingStarted;
                    subscribedGuidingStarted = false;
                    guidingStartedSubscriptionAttempted = false;
                }
                catch
                {
                    // Telemetry teardown must never interfere with NINA shutdown.
                }
            }

            if (guideEventSubscriptionAttempted || subscribedToGuideEvent)
            {
                try
                {
                    mediator.GuideEvent -= OnGuideEvent;
                    subscribedToGuideEvent = false;
                    guideEventSubscriptionAttempted = false;
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

    private void UpdateDeviceInfoCore(GuiderInfo deviceInfo)
    {
        var guiderName = NormalizeGuiderName(deviceInfo.Name);

        if (!deviceInfo.Connected)
        {
            var clearGuiderName = pendingClearGuiderName ?? lastConnectedGuiderName;
            if (clearGuiderName is not null)
            {
                lastDisconnectedGuiderName = clearGuiderName;
                PublishClearMetrics(timeProvider.GetUtcNow(), clearGuiderName);
                ResetPublishedState();
            }

            return;
        }

        if (lastConnectedGuiderName is not null &&
            !string.Equals(lastConnectedGuiderName, guiderName, StringComparison.Ordinal))
        {
            pendingClearGuiderName ??= lastConnectedGuiderName;
        }

        lastConnectedGuiderName = guiderName;
        lastDisconnectedGuiderName = null;
        disconnectedEventLogged = false;
        currentGuiderSnapshot = GuiderInfoSnapshot.From(deviceInfo, guiderName);
    }

    private void OnGuideEvent(object? sender, IGuideStep guideStep)
    {
        if (guideStep is null)
        {
            return;
        }

        try
        {
            lock (syncRoot)
            {
                if (disposed ||
                    startupFailed ||
                    currentGuiderSnapshot is null ||
                    lastConnectedGuiderName is null)
                {
                    return;
                }

                var timestamp = timeProvider.GetUtcNow();
                if (pendingClearGuiderName is not null)
                {
                    PublishClearMetrics(timestamp, pendingClearGuiderName);
                    ResetPublishedFlags();
                    pendingClearGuiderName = null;
                }

                PublishCurrentMetrics(timestamp, currentGuiderSnapshot, guideStep);
            }
        }
        catch
        {
            // NINA guider events must never fail because telemetry handling failed.
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
                var guiderName = deviceInfo is { Connected: true }
                    ? NormalizeGuiderName(deviceInfo.Name)
                    : NormalizeGuiderName(lastConnectedGuiderName);

                disconnectedEventLogged = false;
                lastDisconnectedGuiderName = null;
                if (deviceInfo is { Connected: true })
                {
                    if (lastConnectedGuiderName is not null &&
                        !string.Equals(lastConnectedGuiderName, guiderName, StringComparison.Ordinal))
                    {
                        pendingClearGuiderName ??= lastConnectedGuiderName;
                    }

                    lastConnectedGuiderName = guiderName;
                    currentGuiderSnapshot = GuiderInfoSnapshot.From(deviceInfo, guiderName);
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "guider_connected",
                    "Guider connected",
                    TelemetryPriority.Normal,
                    CreateGuiderAttributes(guiderName));
            }
        }
        catch
        {
            // NINA guider events must never fail because telemetry handling failed.
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

                var clearGuiderName = pendingClearGuiderName ?? lastConnectedGuiderName;
                var shouldClearMetrics = clearGuiderName is not null;
                var guiderName = ResolveDisconnectedGuiderName();

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "guider_disconnected",
                    "Guider disconnected",
                    TelemetryPriority.Important,
                    CreateGuiderAttributes(guiderName));
                if (shouldClearMetrics)
                {
                    PublishClearMetrics(timeProvider.GetUtcNow(), clearGuiderName!);
                }

                ResetPublishedState();
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA guider events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task OnAfterDither(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var timestamp = timeProvider.GetUtcNow();
                var attributes = CreateGuiderAttributes(ResolveCurrentGuiderName());
                PublishNamedLog(
                    timestamp,
                    "guider_dither",
                    "Dither",
                    TelemetryPriority.Normal,
                    attributes);
                TryPublishSafely(TelemetryRecord.Span(
                    timestamp,
                    SourceName,
                    "nina.dither_settle",
                    SpanEventKind.Stop,
                    CreateDitherSettleSpanId(timestamp, attributes),
                    TelemetryPriority.Normal,
                    attributes));
            }
        }
        catch
        {
            // NINA guider events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private Task OnGuidingStarted(object sender, EventArgs e) =>
        PublishGuiderLifecycleLog("guider_guiding_started", "Guiding started", TelemetryPriority.Normal);

    private Task OnGuidingStopped(object sender, EventArgs e) =>
        PublishGuiderLifecycleLog("guider_guiding_stopped", "Guiding stopped", TelemetryPriority.Normal);

    private Task PublishGuiderLifecycleLog(
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
                    CreateGuiderAttributes(ResolveCurrentGuiderName()));
            }
        }
        catch
        {
            // NINA guider events must never fail because telemetry handling failed.
        }

        return Task.CompletedTask;
    }

    private void PublishCurrentMetrics(
        DateTimeOffset timestamp,
        GuiderInfoSnapshot snapshot,
        IGuideStep guideStep)
    {
        var attributes = CreateGuiderAttributes(snapshot.GuiderName);
        foreach (var metric in metrics)
        {
            PublishOrClearUnavailableMetric(
                timestamp,
                metric,
                metric.ReadValue(snapshot, guideStep),
                attributes);
        }
    }

    private void PublishOrClearUnavailableMetric(
        DateTimeOffset timestamp,
        GuiderMetricState metric,
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

    private void PublishClearMetrics(DateTimeOffset timestamp, string guiderName)
    {
        var attributes = CreateGuiderAttributes(guiderName);

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
            "guider_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void CleanupFailedStart()
    {
        if (afterDitherSubscriptionAttempted)
        {
            try
            {
                mediator.AfterDither -= OnAfterDither;
                subscribedAfterDither = false;
                afterDitherSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (guidingStoppedSubscriptionAttempted)
        {
            try
            {
                mediator.GuidingStopped -= OnGuidingStopped;
                subscribedGuidingStopped = false;
                guidingStoppedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (guidingStartedSubscriptionAttempted)
        {
            try
            {
                mediator.GuidingStarted -= OnGuidingStarted;
                subscribedGuidingStarted = false;
                guidingStartedSubscriptionAttempted = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        if (guideEventSubscriptionAttempted)
        {
            try
            {
                mediator.GuideEvent -= OnGuideEvent;
                subscribedToGuideEvent = false;
                guideEventSubscriptionAttempted = false;
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

        var clearGuiderName = pendingClearGuiderName ?? lastConnectedGuiderName;
        if (clearGuiderName is not null)
        {
            PublishClearMetrics(timeProvider.GetUtcNow(), clearGuiderName);
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
        currentGuiderSnapshot = null;
        lastConnectedGuiderName = null;
        pendingClearGuiderName = null;
        ResetPublishedFlags();
    }

    private void ResetPublishedFlags()
    {
        foreach (var metric in metrics)
        {
            metric.HasPublished = false;
        }
    }

    private string ResolveCurrentGuiderName()
    {
        var deviceInfo = TryGetInfo();
        return deviceInfo is { Connected: true }
            ? NormalizeGuiderName(deviceInfo.Name)
            : NormalizeGuiderName(lastConnectedGuiderName ?? lastDisconnectedGuiderName);
    }

    private string ResolveDisconnectedGuiderName() =>
        lastConnectedGuiderName ??
        lastDisconnectedGuiderName ??
        NormalizeGuiderName(TryGetInfo()?.Name);

    private GuiderInfo? TryGetInfo()
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

    private static Dictionary<string, object?> CreateGuiderAttributes(string guiderName) =>
        new()
        {
            ["guider_name"] = guiderName,
        };

    private static string CreateDitherSettleSpanId(
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?> attributes) =>
        string.Join(
            "|",
            [
                "nina.dither_settle",
                $"timestamp={timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
                $"guider={AttributeValue(attributes, "guider_name")}",
            ]);

    private static string AttributeValue(
        IReadOnlyDictionary<string, object?> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static string NormalizeGuiderName(string? guiderName) =>
        string.IsNullOrWhiteSpace(guiderName)
            ? UnknownGuiderName
            : guiderName;

    private static double ReadPixel(RMSUnit? unit) =>
        unit?.Pixel ?? double.NaN;

    private static double ReadArcseconds(RMSUnit? unit) =>
        unit?.Arcseconds ?? double.NaN;

    private static double Hypotenuse(double first, double second) =>
        double.IsFinite(first) && double.IsFinite(second)
            ? Math.Sqrt((first * first) + (second * second))
            : double.NaN;

    private sealed class GuiderMetricState(
        string name,
        Func<GuiderInfoSnapshot, IGuideStep, double> readValue)
    {
        public string Name { get; } = name;

        public Func<GuiderInfoSnapshot, IGuideStep, double> ReadValue { get; } = readValue;

        public bool HasPublished { get; set; }
    }

    private sealed record GuiderInfoSnapshot(
        string GuiderName,
        double RmsRaArcseconds,
        double RmsDecArcseconds,
        double RmsTotalArcseconds,
        double RmsRaPixel,
        double RmsDecPixel,
        double RmsTotalPixel,
        double RmsPeakRaArcseconds,
        double RmsPeakDecArcseconds,
        double RmsPeakArcseconds,
        double RmsPeakRaPixel,
        double RmsPeakDecPixel,
        double RmsPeakPixel)
    {
        public static GuiderInfoSnapshot From(GuiderInfo info, string guiderName)
        {
            var rmsError = info.RMSError;
            var peakRaArcseconds = ReadArcseconds(rmsError?.PeakRA);
            var peakDecArcseconds = ReadArcseconds(rmsError?.PeakDec);
            var peakRaPixel = ReadPixel(rmsError?.PeakRA);
            var peakDecPixel = ReadPixel(rmsError?.PeakDec);

            return new GuiderInfoSnapshot(
                guiderName,
                ReadArcseconds(rmsError?.RA),
                ReadArcseconds(rmsError?.Dec),
                ReadArcseconds(rmsError?.Total),
                ReadPixel(rmsError?.RA),
                ReadPixel(rmsError?.Dec),
                ReadPixel(rmsError?.Total),
                peakRaArcseconds,
                peakDecArcseconds,
                Hypotenuse(peakRaArcseconds, peakDecArcseconds),
                peakRaPixel,
                peakDecPixel,
                Hypotenuse(peakRaPixel, peakDecPixel));
        }
    }
}
