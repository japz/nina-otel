using System.Collections.Immutable;
using System.Reflection;
using FluentAssertions;
using NINA.Core.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class ImageTelemetryCollectorTests
{
    private static readonly string[] RequiredImageMetricNames =
    [
        "image_mean",
        "image_median",
        "image_std_deviation",
        "image_mad",
        "image_min_adu",
        "image_min_adu_count",
        "image_max_adu",
        "image_max_adu_count",
        "image_hfr",
        "image_hfr_std_deviation",
        "image_star_count",
        "image_rms_avg_ra_arcsec",
        "image_rms_avg_dec_arcsec",
        "image_rms_avg_arcsec",
        "image_rms_peak_ra_arcsec",
        "image_rms_peak_dec_arcsec",
        "image_rms_peak_arcsec",
    ];

    [Theory]
    [InlineData("mediator")]
    [InlineData("sink")]
    [InlineData("timeProvider")]
    public void Constructor_WhenDependencyIsNull_ThrowsArgumentNullException(string nullDependency)
    {
        var proxy = CreateMediator(out var mediator);
        _ = proxy;
        var sink = new RecordingTelemetrySink();
        var timeProvider = TimeProvider.System;

        var act = () => new ImageTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_SubscribesImageSavedOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.AddImageSavedCalls.Should().Be(1);
        proxy.ImageSavedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void ImageSaved_WhenSnapshotIsComplete_PublishesImageStatisticsStarMetricsRmsMetricsAndAttributes()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(
            mediator,
            sink,
            new FixedTimeProvider());
        collector.Start();
        var exposureStart = new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc);

        proxy.RaiseImageSaved(CompleteImageSavedEvent(exposureStart));

        sink.Records.Should().HaveCount(19);
        sink.Records.Should().OnlyContain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.image" &&
            record.Timestamp == new DateTimeOffset(exposureStart) &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["image_file_name"], "M42_L_001.fit") &&
            Equals(record.Attributes["target_name"], "M42") &&
            Equals(record.Attributes["sequence_title"], "Orion sequence") &&
            Equals(record.Attributes["camera_name"], "ASI2600MM") &&
            Equals(record.Attributes["readout_mode"], "High gain") &&
            !record.Attributes.ContainsKey("profile_name") &&
            !record.Attributes.ContainsKey("host_name"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            RequiredImageMetricNames.Concat(["image_fwhm", "image_eccentricity"]));
        sink.Records.Should().ContainSingle(record => record.Name == "image_mean" && record.NumericValue == 101.2);
        sink.Records.Should().ContainSingle(record => record.Name == "image_median" && record.NumericValue == 99.9);
        sink.Records.Should().ContainSingle(record => record.Name == "image_std_deviation" && record.NumericValue == 12.3);
        sink.Records.Should().ContainSingle(record => record.Name == "image_mad" && record.NumericValue == 3.4);
        sink.Records.Should().ContainSingle(record => record.Name == "image_min_adu" && record.NumericValue == 42);
        sink.Records.Should().ContainSingle(record => record.Name == "image_min_adu_count" && record.NumericValue == 2);
        sink.Records.Should().ContainSingle(record => record.Name == "image_max_adu" && record.NumericValue == 65535);
        sink.Records.Should().ContainSingle(record => record.Name == "image_max_adu_count" && record.NumericValue == 7);
        sink.Records.Should().ContainSingle(record => record.Name == "image_hfr" && record.NumericValue == 2.1);
        sink.Records.Should().ContainSingle(record => record.Name == "image_hfr_std_deviation" && record.NumericValue == 0.22);
        sink.Records.Should().ContainSingle(record => record.Name == "image_star_count" && record.NumericValue == 123);
        sink.Records.Should().ContainSingle(record => record.Name == "image_fwhm" && record.NumericValue == 3.3);
        sink.Records.Should().ContainSingle(record => record.Name == "image_eccentricity" && record.NumericValue == 0.42);
        sink.Records.Should().ContainSingle(record => record.Name == "image_rms_avg_ra_arcsec" && record.NumericValue == 0.61);
        sink.Records.Should().ContainSingle(record => record.Name == "image_rms_avg_dec_arcsec" && record.NumericValue == 0.72);
        sink.Records.Should().ContainSingle(record => record.Name == "image_rms_avg_arcsec" && record.NumericValue == 0.94);
        sink.Records.Should().ContainSingle(record => record.Name == "image_rms_peak_ra_arcsec" && record.NumericValue == 1.2);
        sink.Records.Should().ContainSingle(record => record.Name == "image_rms_peak_dec_arcsec" && record.NumericValue == 1.6);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "image_rms_peak_arcsec" &&
            record.NumericValue!.Value.ShouldBeApproximately(2.0));
    }

    [Fact]
    public void ImageSaved_WhenStatsContainNanAndNegativeValues_NormalizesRequiredMetricsToZero()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        proxy.RaiseImageSaved(new ImageSavedEventArgs
        {
            MetaData = new ImageMetaData
            {
                Image = new ImageParameter
                {
                    ExposureStart = new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc),
                    RecordedRMS = new RMS
                    {
                        RA = double.NaN,
                        Dec = 0.72,
                        Total = 0.94,
                        PeakRA = 1.2,
                        PeakDec = double.NaN,
                    },
                },
            },
            Statistics = ImageStatistics(
                mean: double.NaN,
                median: double.NaN,
                stDev: double.NaN,
                medianAbsoluteDeviation: double.NaN,
                min: -42,
                minOccurrences: -2,
                max: -1,
                maxOccurrences: -7),
            StarDetectionAnalysis = new HocusFocusStarDetectionAnalysis
            {
                HFR = double.NaN,
                HFRStDev = double.NaN,
                DetectedStars = -123,
                FWHM = double.NaN,
                Eccentricity = double.NaN,
            },
        });

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(RequiredImageMetricNames);
        sink.Records.Should().OnlyContain(record => record.NumericValue == 0.0);
    }

    [Fact]
    public void ImageSaved_EmitsHocusFocusMetricsOnlyWhenPropertiesExistAndAreNotNan()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        proxy.RaiseImageSaved(CompleteImageSavedEvent(
            new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc),
            new StarDetectionAnalysis
            {
                HFR = 2.1,
                HFRStDev = 0.22,
                DetectedStars = 123,
            }));
        proxy.RaiseImageSaved(CompleteImageSavedEvent(
            new DateTime(2026, 6, 18, 20, 16, 30, DateTimeKind.Utc),
            new HocusFocusStarDetectionAnalysis
            {
                HFR = 2.4,
                HFRStDev = 0.24,
                DetectedStars = 124,
                FWHM = 3.6,
                Eccentricity = 0.51,
            }));

        sink.Records.Where(static record => record.Name == "image_fwhm").Should().ContainSingle()
            .Which.NumericValue.Should().Be(3.6);
        sink.Records.Where(static record => record.Name == "image_eccentricity").Should().ContainSingle()
            .Which.NumericValue.Should().Be(0.51);
    }

    [Fact]
    public void ImageSaved_WhenSinkFailsOrOptionalMetadataIsMissing_DoesNotThrowOutward()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new ImageTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var act = () => proxy.RaiseImageSaved(new ImageSavedEventArgs
        {
            MetaData = null!,
            Statistics = ImageStatistics(),
            StarDetectionAnalysis = null!,
            PathToImage = null!,
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_UnsubscribesImageSavedOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.RemoveImageSavedCalls.Should().Be(1);
        proxy.ImageSavedSubscriberCount.Should().Be(0);
    }

    private static ImageSavedEventArgs CompleteImageSavedEvent(DateTime exposureStart) =>
        CompleteImageSavedEvent(
            exposureStart,
            new HocusFocusStarDetectionAnalysis
            {
                HFR = 2.1,
                HFRStDev = 0.22,
                DetectedStars = 123,
                FWHM = 3.3,
                Eccentricity = 0.42,
            });

    private static ImageSavedEventArgs CompleteImageSavedEvent(
        DateTime exposureStart,
        IStarDetectionAnalysis starDetectionAnalysis) =>
        new()
        {
            MetaData = new ImageMetaData
            {
                Image = new ImageParameter
                {
                    ExposureStart = exposureStart,
                    RecordedRMS = new RMS
                    {
                        RA = 0.61,
                        Dec = 0.72,
                        Total = 0.94,
                        PeakRA = 1.2,
                        PeakDec = 1.6,
                    },
                },
                Target = new TargetParameter
                {
                    Name = "M42",
                },
                Sequence = new SequenceParameter
                {
                    Title = "Orion sequence",
                },
                Camera = new CameraParameter
                {
                    Name = "ASI2600MM",
                    ReadoutModeName = "High gain",
                },
            },
            PathToImage = new Uri("file:///Users/jasper/images/M42_L_001.fit"),
            Statistics = ImageStatistics(
                mean: 101.2,
                median: 99.9,
                stDev: 12.3,
                medianAbsoluteDeviation: 3.4,
                min: 42,
                minOccurrences: 2,
                max: 65535,
                maxOccurrences: 7),
            StarDetectionAnalysis = starDetectionAnalysis,
        };

    private static IImageStatistics ImageStatistics(
        double mean = 1,
        double median = 2,
        double stDev = 3,
        double medianAbsoluteDeviation = 4,
        int min = 5,
        long minOccurrences = 6,
        int max = 7,
        long maxOccurrences = 8)
    {
        var proxy = DispatchProxy.Create<IImageStatistics, ImageStatisticsProxy>();
        ((ImageStatisticsProxy)(object)proxy).Values = new Dictionary<string, object?>
        {
            [nameof(IImageStatistics.BitDepth)] = 16,
            [nameof(IImageStatistics.Mean)] = mean,
            [nameof(IImageStatistics.Median)] = median,
            [nameof(IImageStatistics.StDev)] = stDev,
            [nameof(IImageStatistics.MedianAbsoluteDeviation)] = medianAbsoluteDeviation,
            [nameof(IImageStatistics.Min)] = min,
            [nameof(IImageStatistics.MinOccurrences)] = minOccurrences,
            [nameof(IImageStatistics.Max)] = max,
            [nameof(IImageStatistics.MaxOccurrences)] = maxOccurrences,
            [nameof(IImageStatistics.Histogram)] = ImmutableList<OxyPlot.DataPoint>.Empty,
        };
        return proxy;
    }

    private static ImageSaveMediatorProxy CreateMediator(out IImageSaveMediator mediator)
    {
        mediator = DispatchProxy.Create<IImageSaveMediator, ImageSaveMediatorProxy>();
        return (ImageSaveMediatorProxy)(object)mediator;
    }

    private sealed class ImageSaveMediatorProxy : DispatchProxy
    {
        private EventHandler<ImageSavedEventArgs>? imageSaved;

        public int AddImageSavedCalls { get; private set; }

        public int RemoveImageSavedCalls { get; private set; }

        public int ImageSavedSubscriberCount =>
            imageSaved?.GetInvocationList().Length ?? 0;

        public void RaiseImageSaved(ImageSavedEventArgs args) =>
            imageSaved?.Invoke(this, args);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "add_ImageSaved":
                    AddImageSavedCalls++;
                    imageSaved += (EventHandler<ImageSavedEventArgs>)args![0]!;
                    return null;
                case "remove_ImageSaved":
                    RemoveImageSavedCalls++;
                    imageSaved -= (EventHandler<ImageSavedEventArgs>)args![0]!;
                    return null;
                default:
                    return targetMethod?.ReturnType == typeof(Task)
                        ? Task.CompletedTask
                        : null;
            }
        }
    }

    private sealed class ImageStatisticsProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.StartsWith("get_", StringComparison.Ordinal) == true)
            {
                return Values[targetMethod.Name[4..]];
            }

            return null;
        }
    }

    private class StarDetectionAnalysis : IStarDetectionAnalysis
    {
        public double HFR { get; set; }

        public double HFRStDev { get; set; }

        public int DetectedStars { get; set; }

        public List<DetectedStar> StarList { get; set; } = [];
    }

    private sealed class HocusFocusStarDetectionAnalysis : StarDetectionAnalysis
    {
        public double FWHM { get; set; }

        public double Eccentricity { get; set; }
    }

    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public bool TryPublish(TelemetryRecord record)
        {
            Records.Add(record);
            return true;
        }
    }

    private sealed class ThrowingTelemetrySink : ITelemetrySink
    {
        public bool TryPublish(TelemetryRecord record) =>
            throw new InvalidOperationException("Sink unavailable.");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
    }
}

internal static class ImageTelemetryTestExtensions
{
    public static bool ShouldBeApproximately(this double actual, double expected) =>
        Math.Abs(actual - expected) < 0.000001;
}
