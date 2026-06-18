using NinaOtel.Abstractions.Telemetry;
using NINA.Core.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System.IO;

namespace NinaOtel.Plugin.Telemetry;

public sealed class ImageTelemetryCollector : IDisposable
{
    private const string Source = "nina.image";

    private readonly IImageSaveMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private bool started;

    public ImageTelemetryCollector(
        IImageSaveMediator mediator,
        ITelemetrySink sink,
        TimeProvider timeProvider)
    {
        this.mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Start()
    {
        if (started || disposed)
        {
            return;
        }

        mediator.ImageSaved += OnImageSaved;
        started = true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (started)
        {
            mediator.ImageSaved -= OnImageSaved;
            started = false;
        }

        disposed = true;
    }

    private void OnImageSaved(object? sender, ImageSavedEventArgs args)
    {
        try
        {
            PublishImageSaved(args);
        }
        catch
        {
            // Image save notifications run on NINA-owned paths. Telemetry must not affect imaging.
        }
    }

    private void PublishImageSaved(ImageSavedEventArgs args)
    {
        var timestamp = CreateTimestamp(args);
        var attributes = CreateAttributes(args);
        var statistics = args.Statistics;
        var starAnalysis = args.StarDetectionAnalysis;
        var recordedRms = args.MetaData?.Image?.RecordedRMS;

        PublishMetric(timestamp, "image_mean", NormalizeDouble(statistics?.Mean), attributes);
        PublishMetric(timestamp, "image_median", NormalizeDouble(statistics?.Median), attributes);
        PublishMetric(timestamp, "image_std_deviation", NormalizeDouble(statistics?.StDev), attributes);
        PublishMetric(timestamp, "image_mad", NormalizeDouble(statistics?.MedianAbsoluteDeviation), attributes);
        PublishMetric(timestamp, "image_min_adu", NormalizeLong(statistics?.Min), attributes);
        PublishMetric(timestamp, "image_min_adu_count", NormalizeLong(statistics?.MinOccurrences), attributes);
        PublishMetric(timestamp, "image_max_adu", NormalizeLong(statistics?.Max), attributes);
        PublishMetric(timestamp, "image_max_adu_count", NormalizeLong(statistics?.MaxOccurrences), attributes);
        PublishMetric(timestamp, "image_hfr", NormalizeDouble(starAnalysis?.HFR), attributes);
        PublishMetric(timestamp, "image_hfr_std_deviation", NormalizeDouble(starAnalysis?.HFRStDev), attributes);
        PublishMetric(timestamp, "image_star_count", NormalizeLong(starAnalysis?.DetectedStars), attributes);

        PublishOptionalHocusFocusMetric(timestamp, "image_fwhm", starAnalysis, "FWHM", attributes);
        PublishOptionalHocusFocusMetric(timestamp, "image_eccentricity", starAnalysis, "Eccentricity", attributes);

        var averageRms = CreateAverageRms(recordedRms);
        PublishMetric(timestamp, "image_rms_avg_ra_arcsec", averageRms.Ra, attributes);
        PublishMetric(timestamp, "image_rms_avg_dec_arcsec", averageRms.Dec, attributes);
        PublishMetric(timestamp, "image_rms_avg_arcsec", averageRms.Total, attributes);

        var peakRms = CreatePeakRms(recordedRms);
        PublishMetric(timestamp, "image_rms_peak_ra_arcsec", peakRms.Ra, attributes);
        PublishMetric(timestamp, "image_rms_peak_dec_arcsec", peakRms.Dec, attributes);
        PublishMetric(timestamp, "image_rms_peak_arcsec", peakRms.Total, attributes);
    }

    private DateTimeOffset CreateTimestamp(ImageSavedEventArgs args)
    {
        var exposureStart = args.MetaData?.Image?.ExposureStart;
        if (exposureStart is null || exposureStart.Value == default)
        {
            return timeProvider.GetUtcNow();
        }

        return new DateTimeOffset(exposureStart.Value.ToUniversalTime());
    }

    private static IReadOnlyDictionary<string, object?> CreateAttributes(ImageSavedEventArgs args)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (args.PathToImage is { } imagePath)
        {
            var localPath = imagePath.IsFile
                ? imagePath.LocalPath
                : imagePath.ToString();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                attributes["image_file_name"] = Path.GetFileName(localPath);
            }
        }

        AddIfPresent(attributes, "target_name", args.MetaData?.Target?.Name);
        AddIfPresent(attributes, "sequence_title", args.MetaData?.Sequence?.Title);
        AddIfPresent(attributes, "camera_name", args.MetaData?.Camera?.Name);
        AddIfPresent(attributes, "readout_mode", args.MetaData?.Camera?.ReadoutModeName);

        return attributes;
    }

    private static void AddIfPresent(
        Dictionary<string, object?> attributes,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[name] = value;
        }
    }

    private void PublishOptionalHocusFocusMetric(
        DateTimeOffset timestamp,
        string metricName,
        IStarDetectionAnalysis? starAnalysis,
        string propertyName,
        IReadOnlyDictionary<string, object?> attributes)
    {
        if (TryGetFiniteProperty(starAnalysis, propertyName, out var value))
        {
            PublishMetric(timestamp, metricName, value, attributes);
        }
    }

    private static bool TryGetFiniteProperty(
        IStarDetectionAnalysis? starAnalysis,
        string propertyName,
        out double value)
    {
        value = 0;
        if (starAnalysis is null)
        {
            return false;
        }

        var property = starAnalysis.GetType().GetProperty(propertyName);
        if (property is null || property.PropertyType != typeof(double))
        {
            return false;
        }

        value = (double)(property.GetValue(starAnalysis) ?? double.NaN);
        return !double.IsNaN(value);
    }

    private void PublishMetric(
        DateTimeOffset timestamp,
        string name,
        double value,
        IReadOnlyDictionary<string, object?> attributes)
    {
        try
        {
            sink.TryPublish(TelemetryRecord.Metric(
                timestamp,
                Source,
                name,
                value,
                TelemetryPriority.Normal,
                attributes));
        }
        catch
        {
        }
    }

    private static double NormalizeDouble(double? value) =>
        !value.HasValue || double.IsNaN(value.Value)
            ? 0
            : value.Value;

    private static double NormalizeLong(long? value) =>
        !value.HasValue || value.Value < 0
            ? 0
            : value.Value;

    private static RmsSnapshot CreateAverageRms(RMS? rms)
    {
        if (rms is null ||
            double.IsNaN(rms.RA) ||
            double.IsNaN(rms.Dec))
        {
            return RmsSnapshot.Zero;
        }

        return new RmsSnapshot(rms.RA, rms.Dec, NormalizeDouble(rms.Total));
    }

    private static RmsSnapshot CreatePeakRms(RMS? rms)
    {
        if (rms is null ||
            double.IsNaN(rms.PeakRA) ||
            double.IsNaN(rms.PeakDec))
        {
            return RmsSnapshot.Zero;
        }

        return new RmsSnapshot(
            rms.PeakRA,
            rms.PeakDec,
            Math.Sqrt(Math.Pow(rms.PeakRA, 2) + Math.Pow(rms.PeakDec, 2)));
    }

    private sealed record RmsSnapshot(double Ra, double Dec, double Total)
    {
        public static RmsSnapshot Zero { get; } = new(0, 0, 0);
    }
}
