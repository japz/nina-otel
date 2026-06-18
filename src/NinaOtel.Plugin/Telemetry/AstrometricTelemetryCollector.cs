using NINA.Astrometry;
using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public readonly record struct AstrometricAltitudes(
    double SunAltitude,
    double MoonAltitude);

public interface IAstrometricAltitudeCalculator
{
    AstrometricAltitudes Calculate(DateTime utcNow, ObserverInfo observerInfo);
}

public sealed class AstrometricTelemetryCollector : IDisposable
{
    private const string SourceName = "nina.astrometry";
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly object syncRoot = new();
    private readonly IProfileService profileService;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly IAstrometricAltitudeCalculator altitudeCalculator;
    private readonly TimeSpan interval;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? workerTask;
    private bool disposed;
    private bool started;
    private string? lastFailureName;

    public AstrometricTelemetryCollector(
        IProfileService profileService,
        ITelemetrySink sink,
        TimeProvider timeProvider,
        IAstrometricAltitudeCalculator? altitudeCalculator = null,
        TimeSpan? interval = null)
    {
        this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.altitudeCalculator = altitudeCalculator ?? new AstroUtilAltitudeCalculator();
        this.interval = interval ?? DefaultInterval;
        if (this.interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        }
    }

    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed || started)
            {
                return;
            }

            started = true;
            cancellationTokenSource = new CancellationTokenSource();
            workerTask = Task.Run(
                () => RunAsync(cancellationTokenSource.Token),
                CancellationToken.None);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? sourceToCancel;
        Task? taskToObserve;

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sourceToCancel = cancellationTokenSource;
            taskToObserve = workerTask;
        }

        if (sourceToCancel is null)
        {
            return;
        }

        try
        {
            sourceToCancel.Cancel();
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }

        _ = DisposeCancellationSourceWhenWorkerStopsAsync(taskToObserve, sourceToCancel);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (!cancellationToken.IsCancellationRequested)
            {
                PublishSampleSafely(cancellationToken);
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            PublishFailureOnce(
                "astrometry_collector.worker_failed",
                ex);
        }
    }

    private void PublishSampleSafely(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var timestamp = timeProvider.GetUtcNow();
        var observerInfo = TryReadObserverInfo();
        if (observerInfo is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        AstrometricAltitudes altitudes;
        try
        {
            altitudes = altitudeCalculator.Calculate(timestamp.UtcDateTime, observerInfo);
        }
        catch (Exception ex)
        {
            PublishFailureOnce(
                "astrometry_collector.calculation_failed",
                ex);
            return;
        }

        ClearFailureSuppression();
        PublishMetricIfFinite(
            timestamp,
            "astro_sun_altitude",
            altitudes.SunAltitude,
            cancellationToken);
        PublishMetricIfFinite(
            timestamp,
            "astro_moon_altitude",
            altitudes.MoonAltitude,
            cancellationToken);
    }

    private ObserverInfo? TryReadObserverInfo()
    {
        try
        {
            var settings = profileService.ActiveProfile?.AstrometrySettings;
            if (settings is null)
            {
                return null;
            }

            return new ObserverInfo
            {
                Latitude = settings.Latitude,
                Longitude = settings.Longitude,
                Elevation = settings.Elevation,
            };
        }
        catch (Exception ex)
        {
            PublishFailureOnce(
                "astrometry_collector.profile_read_failed",
                ex);
            return null;
        }
    }

    private void PublishMetricIfFinite(
        DateTimeOffset timestamp,
        string metricName,
        double value,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TryPublishSafely(TelemetryRecord.Metric(
            timestamp,
            SourceName,
            metricName,
            value,
            TelemetryPriority.Normal));
    }

    private void PublishFailureOnce(string name, Exception ex)
    {
        lock (syncRoot)
        {
            if (disposed || string.Equals(lastFailureName, name, StringComparison.Ordinal))
            {
                return;
            }

            lastFailureName = name;
        }

        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            name,
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void ClearFailureSuppression()
    {
        lock (syncRoot)
        {
            lastFailureName = null;
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
            // Telemetry collection must never fail NINA startup, callbacks, or shutdown.
        }
    }

    private static async Task DisposeCancellationSourceWhenWorkerStopsAsync(
        Task? task,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch
        {
            // The worker already suppresses operational failures; disposal is best-effort.
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private sealed class AstroUtilAltitudeCalculator : IAstrometricAltitudeCalculator
    {
        public AstrometricAltitudes Calculate(DateTime utcNow, ObserverInfo observerInfo) =>
            new(
                AstroUtil.GetSunAltitude(utcNow, observerInfo),
                AstroUtil.GetMoonAltitude(utcNow, observerInfo));
    }
}
