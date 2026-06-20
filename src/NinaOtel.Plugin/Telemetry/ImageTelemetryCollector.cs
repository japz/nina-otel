using NinaOtel.Abstractions.Telemetry;
using NINA.Core.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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

        PublishExposureSpan(args, attributes);
        PublishImageSaveSpan(args, attributes);
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

        PublishImageLog(timestamp, args, attributes);
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
        AddIfPresent(attributes, "image_type", args.MetaData?.Image?.ImageType);
        AddIfPresent(attributes, "filter_name", args.Filter);

        if (TryNormalizeExposureDuration(args.Duration, out var durationSeconds))
        {
            attributes["exposure_duration_seconds"] = durationSeconds;
        }

        return attributes;
    }

    private void PublishImageSaveSpan(
        ImageSavedEventArgs args,
        IReadOnlyDictionary<string, object?> baseAttributes)
    {
        var saveCompletedAt = timeProvider.GetUtcNow();
        var attributes = new Dictionary<string, object?>(baseAttributes, StringComparer.Ordinal);
        AddIfPresent(attributes, "exposure_start", CreateExposureStartAttributeValue(args));

        PublishSpan(
            saveCompletedAt,
            "nina.image_save",
            SpanEventKind.Stop,
            CreateImageSaveSpanId(args, attributes, saveCompletedAt),
            attributes);
    }

    private void PublishExposureSpan(
        ImageSavedEventArgs args,
        IReadOnlyDictionary<string, object?> baseAttributes)
    {
        if (!TryCreateExposureWindow(args, out var startedAt, out var stoppedAt, out var durationSeconds))
        {
            return;
        }

        var attributes = new Dictionary<string, object?>(baseAttributes, StringComparer.Ordinal);
        attributes["exposure_start"] = startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        attributes["exposure_duration_seconds"] = durationSeconds;
        var spanId = CreateExposureSpanId(args, attributes);

        PublishSpan(
            startedAt,
            "nina.exposure",
            SpanEventKind.Start,
            spanId,
            attributes);
        PublishSpan(
            stoppedAt,
            "nina.exposure",
            SpanEventKind.Stop,
            spanId,
            attributes);
    }

    private static bool TryCreateExposureWindow(
        ImageSavedEventArgs args,
        out DateTimeOffset startedAt,
        out DateTimeOffset stoppedAt,
        out double durationSeconds)
    {
        startedAt = default;
        stoppedAt = default;
        durationSeconds = default;

        var exposureStart = args.MetaData?.Image?.ExposureStart;
        if (exposureStart is null || exposureStart.Value == default)
        {
            return false;
        }

        if (!TryNormalizeExposureDuration(args.Duration, out durationSeconds) || durationSeconds <= 0)
        {
            return false;
        }

        try
        {
            startedAt = new DateTimeOffset(exposureStart.Value.ToUniversalTime());
            stoppedAt = startedAt.AddSeconds(durationSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }

    private static bool TryNormalizeExposureDuration(double durationSeconds, out double normalized)
    {
        normalized = durationSeconds;
        return double.IsFinite(durationSeconds) && durationSeconds >= 0;
    }

    private static string CreateExposureStartAttributeValue(ImageSavedEventArgs args)
    {
        var exposureStart = args.MetaData?.Image?.ExposureStart;
        if (exposureStart is null || exposureStart.Value == default)
        {
            return string.Empty;
        }

        return exposureStart.Value
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
    }

    private static string CreateImageSaveSpanId(
        ImageSavedEventArgs args,
        IReadOnlyDictionary<string, object?> attributes,
        DateTimeOffset saveCompletedAt)
    {
        var imagePath = CreateImagePathValue(args);
        var exposureStart = AttributeValue(attributes, "exposure_start");
        var fallback = string.IsNullOrWhiteSpace(imagePath) && string.IsNullOrWhiteSpace(exposureStart)
            ? saveCompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;

        return string.Join(
            "|",
            [
                "nina.image_save",
                $"path_hash={CreateStableHash(imagePath)}",
                $"file={AttributeValue(attributes, "image_file_name")}",
                $"target={AttributeValue(attributes, "target_name")}",
                $"sequence={AttributeValue(attributes, "sequence_title")}",
                $"camera={AttributeValue(attributes, "camera_name")}",
                $"readout={AttributeValue(attributes, "readout_mode")}",
                $"exposure_start={exposureStart}",
                $"completed={fallback}",
            ]);
    }

    private static string CreateExposureSpanId(
        ImageSavedEventArgs args,
        IReadOnlyDictionary<string, object?> attributes)
    {
        var imagePath = CreateImagePathValue(args);

        return string.Join(
            "|",
            [
                "nina.exposure",
                $"path_hash={CreateStableHash(imagePath)}",
                $"file={AttributeValue(attributes, "image_file_name")}",
                $"target={AttributeValue(attributes, "target_name")}",
                $"sequence={AttributeValue(attributes, "sequence_title")}",
                $"camera={AttributeValue(attributes, "camera_name")}",
                $"readout={AttributeValue(attributes, "readout_mode")}",
                $"exposure_start={AttributeValue(attributes, "exposure_start")}",
                $"duration={AttributeValue(attributes, "exposure_duration_seconds")}",
            ]);
    }

    private static string CreateImagePathValue(ImageSavedEventArgs args)
    {
        if (args.PathToImage is not { } imagePath)
        {
            return string.Empty;
        }

        var path = imagePath.IsFile
            ? imagePath.LocalPath
            : imagePath.ToString();

        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path;
    }

    private static string CreateStableHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)
            .ToLowerInvariant()[..16];
    }

    private static string AttributeValue(
        IReadOnlyDictionary<string, object?> attributes,
        string name) =>
        attributes.TryGetValue(name, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

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

    private void PublishImageLog(
        DateTimeOffset timestamp,
        ImageSavedEventArgs args,
        IReadOnlyDictionary<string, object?> baseAttributes)
    {
        try
        {
            var body = CreateImageLogBody(args);
            var attributes = new Dictionary<string, object?>(baseAttributes, StringComparer.Ordinal)
            {
                ["name"] = "image",
                ["text"] = body,
                ["title"] = "Image taken",
            };

            sink.TryPublish(new TelemetryRecord(
                TelemetrySignal.Log,
                timestamp,
                Source,
                "image",
                TelemetryPriority.Normal,
                attributes,
                Body: body,
                Severity: TelemetrySeverity.Information));
        }
        catch
        {
        }
    }

    private static string CreateImageLogBody(ImageSavedEventArgs args)
    {
        var imageType = args.MetaData?.Image?.ImageType;
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "Image; Type: {0}",
            string.IsNullOrWhiteSpace(imageType) ? "Unknown" : imageType);

        var target = args.MetaData?.Target?.Name;
        if (!string.IsNullOrWhiteSpace(target))
        {
            text += string.Format(CultureInfo.InvariantCulture, ", Target: {0}", target);
        }

        if (!string.IsNullOrWhiteSpace(args.Filter))
        {
            text += string.Format(CultureInfo.InvariantCulture, ", Filter: {0}", args.Filter);
        }

        text += string.Format(
            CultureInfo.InvariantCulture,
            ", Exp: {0:F2}s",
            NormalizeExposureTime(args.MetaData?.Image?.ExposureTime));

        if (args.Statistics is not null)
        {
            text += string.Format(CultureInfo.InvariantCulture, ", Mean: {0:F2}", args.Statistics.Mean);
        }

        return text;
    }

    private static double NormalizeExposureTime(double? exposureTime) =>
        exposureTime.HasValue &&
        double.IsFinite(exposureTime.Value) &&
        exposureTime.Value >= 0
            ? exposureTime.Value
            : 0;

    private void PublishSpan(
        DateTimeOffset timestamp,
        string name,
        SpanEventKind kind,
        string spanId,
        IReadOnlyDictionary<string, object?> attributes)
    {
        try
        {
            sink.TryPublish(TelemetryRecord.Span(
                timestamp,
                Source,
                name,
                kind,
                spanId,
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
