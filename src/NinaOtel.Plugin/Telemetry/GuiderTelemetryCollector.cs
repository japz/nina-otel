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
    private GuiderInfoSnapshot? currentGuiderSnapshot;
    private bool startAttempted;
    private bool registered;
    private bool subscribedToGuideEvent;
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
                mediator.RegisterConsumer(this);
                registered = true;

                mediator.GuideEvent += OnGuideEvent;
                subscribedToGuideEvent = true;
            }
            catch (Exception ex)
            {
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

            if (subscribedToGuideEvent)
            {
                try
                {
                    mediator.GuideEvent -= OnGuideEvent;
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

    private void UpdateDeviceInfoCore(GuiderInfo deviceInfo)
    {
        var guiderName = NormalizeGuiderName(deviceInfo.Name);

        if (!deviceInfo.Connected)
        {
            var clearGuiderName = pendingClearGuiderName ?? lastConnectedGuiderName;
            if (clearGuiderName is not null)
            {
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
