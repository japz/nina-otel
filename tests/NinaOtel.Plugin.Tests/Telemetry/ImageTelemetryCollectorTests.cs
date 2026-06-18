using System.Collections.Immutable;
using System.ComponentModel;
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

        sink.Records.Should().HaveCount(22);
        var metrics = sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).ToArray();
        metrics.Should().HaveCount(19);
        metrics.Should().OnlyContain(record =>
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
        metrics.Select(static record => record.Name).Should().BeEquivalentTo(
            RequiredImageMetricNames.Concat(["image_fwhm", "image_eccentricity"]));
        metrics.Should().ContainSingle(record => record.Name == "image_mean" && record.NumericValue == 101.2);
        metrics.Should().ContainSingle(record => record.Name == "image_median" && record.NumericValue == 99.9);
        metrics.Should().ContainSingle(record => record.Name == "image_std_deviation" && record.NumericValue == 12.3);
        metrics.Should().ContainSingle(record => record.Name == "image_mad" && record.NumericValue == 3.4);
        metrics.Should().ContainSingle(record => record.Name == "image_min_adu" && record.NumericValue == 42);
        metrics.Should().ContainSingle(record => record.Name == "image_min_adu_count" && record.NumericValue == 2);
        metrics.Should().ContainSingle(record => record.Name == "image_max_adu" && record.NumericValue == 65535);
        metrics.Should().ContainSingle(record => record.Name == "image_max_adu_count" && record.NumericValue == 7);
        metrics.Should().ContainSingle(record => record.Name == "image_hfr" && record.NumericValue == 2.1);
        metrics.Should().ContainSingle(record => record.Name == "image_hfr_std_deviation" && record.NumericValue == 0.22);
        metrics.Should().ContainSingle(record => record.Name == "image_star_count" && record.NumericValue == 123);
        metrics.Should().ContainSingle(record => record.Name == "image_fwhm" && record.NumericValue == 3.3);
        metrics.Should().ContainSingle(record => record.Name == "image_eccentricity" && record.NumericValue == 0.42);
        metrics.Should().ContainSingle(record => record.Name == "image_rms_avg_ra_arcsec" && record.NumericValue == 0.61);
        metrics.Should().ContainSingle(record => record.Name == "image_rms_avg_dec_arcsec" && record.NumericValue == 0.72);
        metrics.Should().ContainSingle(record => record.Name == "image_rms_avg_arcsec" && record.NumericValue == 0.94);
        metrics.Should().ContainSingle(record => record.Name == "image_rms_peak_ra_arcsec" && record.NumericValue == 1.2);
        metrics.Should().ContainSingle(record => record.Name == "image_rms_peak_dec_arcsec" && record.NumericValue == 1.6);
        metrics.Should().ContainSingle(record =>
            record.Name == "image_rms_peak_arcsec" &&
            ImageTelemetryAssertions.IsApproximately(record.NumericValue!.Value, 2.0));
    }

    [Fact]
    public void ImageSaved_PublishesCompletedImageSaveSpanWithMetadataAndTimingContext()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var saveCompletedAt = new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero);
        using var collector = new ImageTelemetryCollector(
            mediator,
            sink,
            new FixedTimeProvider(saveCompletedAt));
        collector.Start();
        var exposureStart = new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc);
        var imageSavedEvent = CompleteImageSavedEvent(exposureStart);
        var rawImagePath = imageSavedEvent.PathToImage!.LocalPath;

        proxy.RaiseImageSaved(imageSavedEvent);

        var span = sink.Records.Should()
            .ContainSingle(static record => record.Signal == TelemetrySignal.Span && record.Name == "nina.image_save")
            .Which;
        span.Source.Should().Be("nina.image");
        span.Name.Should().Be("nina.image_save");
        span.Timestamp.Should().Be(saveCompletedAt);
        span.Priority.Should().Be(TelemetryPriority.Normal);
        span.SpanKind.Should().Be(SpanEventKind.Stop);
        span.SpanId.Should().NotBeNullOrWhiteSpace();
        span.SpanId.Should().NotContain(rawImagePath);
        span.SpanId.Should().NotContain(imageSavedEvent.PathToImage.ToString());
        span.Attributes.Should().Contain("image_file_name", "M42_L_001.fit");
        span.Attributes.Should().Contain("target_name", "M42");
        span.Attributes.Should().Contain("sequence_title", "Orion sequence");
        span.Attributes.Should().Contain("camera_name", "ASI2600MM");
        span.Attributes.Should().Contain("readout_mode", "High gain");
        span.Attributes.Should().Contain("exposure_start", "2026-06-18T20:15:30.0000000Z");

        var firstSpanId = span.SpanId;
        sink.Records.Clear();

        proxy.RaiseImageSaved(imageSavedEvent);

        sink.Records.Should()
            .ContainSingle(static record => record.Signal == TelemetrySignal.Span && record.Name == "nina.image_save")
            .Which.SpanId.Should().Be(firstSpanId);
    }

    [Fact]
    public void ImageSaved_WhenExposureDurationIsAvailable_PublishesExposureStartAndStopSpans()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(
            mediator,
            sink,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero)));
        collector.Start();
        var exposureStart = new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc);
        var imageSavedEvent = CompleteImageSavedEvent(exposureStart);
        imageSavedEvent.Duration = 120.5;
        var rawImagePath = imageSavedEvent.PathToImage!.LocalPath;

        proxy.RaiseImageSaved(imageSavedEvent);

        var exposureSpans = sink.Records
            .Where(static record => record.Signal == TelemetrySignal.Span && record.Name == "nina.exposure")
            .OrderBy(static record => record.SpanKind)
            .ToArray();
        exposureSpans.Should().HaveCount(2);
        exposureSpans.Should().OnlyContain(static record =>
            record.Source == "nina.image" &&
            record.Priority == TelemetryPriority.Normal);
        exposureSpans.Select(static record => record.SpanKind).Should().BeEquivalentTo(
            [SpanEventKind.Start, SpanEventKind.Stop]);

        var startSpan = exposureSpans.Should()
            .ContainSingle(static record => record.SpanKind == SpanEventKind.Start)
            .Which;
        var stopSpan = exposureSpans.Should()
            .ContainSingle(static record => record.SpanKind == SpanEventKind.Stop)
            .Which;
        startSpan.Timestamp.Should().Be(new DateTimeOffset(exposureStart));
        stopSpan.Timestamp.Should().Be(new DateTimeOffset(exposureStart).AddSeconds(120.5));
        stopSpan.SpanId.Should().Be(startSpan.SpanId);
        startSpan.SpanId.Should().NotBeNullOrWhiteSpace();
        startSpan.SpanId.Should().NotContain(rawImagePath);
        startSpan.SpanId.Should().NotContain(imageSavedEvent.PathToImage.ToString());
        startSpan.Attributes.Should().Contain("image_file_name", "M42_L_001.fit");
        startSpan.Attributes.Should().Contain("target_name", "M42");
        startSpan.Attributes.Should().Contain("sequence_title", "Orion sequence");
        startSpan.Attributes.Should().Contain("camera_name", "ASI2600MM");
        startSpan.Attributes.Should().Contain("readout_mode", "High gain");
        startSpan.Attributes.Should().Contain("exposure_start", "2026-06-18T20:15:30.0000000Z");
        startSpan.Attributes.Should().Contain("exposure_duration_seconds", 120.5);
        stopSpan.Attributes.Should().BeEquivalentTo(startSpan.Attributes);
    }

    [Theory]
    [MemberData(nameof(InvalidExposureDurations))]
    public void ImageSaved_WhenExposureDurationIsNotValid_DoesNotPublishExposureSpan(double durationSeconds)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        var imageSavedEvent = CompleteImageSavedEvent(new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc));
        imageSavedEvent.Duration = durationSeconds;

        proxy.RaiseImageSaved(imageSavedEvent);

        sink.Records.Should().NotContain(static record => record.Name == "nina.exposure");
        sink.Records.Should().ContainSingle(static record => record.Name == "nina.image_save");
    }

    [Fact]
    public void ImageSaved_WhenExposureStartIsMissing_DoesNotPublishExposureSpan()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        var imageSavedEvent = CompleteImageSavedEvent(default);
        imageSavedEvent.Duration = 120;

        proxy.RaiseImageSaved(imageSavedEvent);

        sink.Records.Should().NotContain(static record => record.Name == "nina.exposure");
        sink.Records.Should().ContainSingle(static record => record.Name == "nina.image_save");
    }

    [Fact]
    public void ImageSaved_WhenOptionalMetadataAndPathAreMissing_DoesNotReuseCollapsedSpanIdForDistinctSaveEvents()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new ImageTelemetryCollector(
            mediator,
            sink,
            new SequenceTimeProvider(
                new DateTimeOffset(2026, 6, 18, 22, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 18, 22, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 18, 22, 0, 2, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 18, 22, 0, 3, TimeSpan.Zero)));
        collector.Start();

        proxy.RaiseImageSaved(SparseImageSavedEvent());
        proxy.RaiseImageSaved(SparseImageSavedEvent());

        var spanIds = sink.Records
            .Where(static record => record.Signal == TelemetrySignal.Span)
            .Select(static record => record.SpanId)
            .ToArray();
        spanIds.Should().HaveCount(2);
        spanIds.Should().OnlyContain(static spanId => !string.IsNullOrWhiteSpace(spanId));
        spanIds.Should().NotContain("nina.image_save||||||");
        spanIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ImageSaved_WhenSpanPublishFails_StillAttemptsImageMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new SpanThrowingTelemetrySink();
        using var collector = new ImageTelemetryCollector(mediator, sink, new FixedTimeProvider());
        collector.Start();
        var exposureStart = new DateTime(2026, 6, 18, 20, 15, 30, DateTimeKind.Utc);

        var act = () => proxy.RaiseImageSaved(CompleteImageSavedEvent(exposureStart));

        act.Should().NotThrow();
        sink.SpanPublishAttempts.Should().Be(3);
        sink.Records.Should().HaveCount(19);
        sink.Records.Should().OnlyContain(static record => record.Signal == TelemetrySignal.Metric);
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            RequiredImageMetricNames.Concat(["image_fwhm", "image_eccentricity"]));
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

        var metrics = sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).ToArray();
        metrics.Select(static record => record.Name).Should().BeEquivalentTo(RequiredImageMetricNames);
        metrics.Should().OnlyContain(record => record.NumericValue == 0.0);
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
            Duration = 120,
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

    public static TheoryData<double> InvalidExposureDurations { get; } =
    [
        0,
        -1,
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
    ];

    private static ImageSavedEventArgs SparseImageSavedEvent() =>
        new()
        {
            MetaData = null!,
            PathToImage = null!,
            Statistics = ImageStatistics(),
            StarDetectionAnalysis = null!,
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

    private class ImageSaveMediatorProxy : DispatchProxy
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

    private class ImageStatisticsProxy : DispatchProxy
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
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

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

    private sealed class SpanThrowingTelemetrySink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public int SpanPublishAttempts { get; private set; }

        public bool TryPublish(TelemetryRecord record)
        {
            if (record.Signal == TelemetrySignal.Span)
            {
                SpanPublishAttempts++;
                throw new InvalidOperationException("Span sink unavailable.");
            }

            Records.Add(record);
            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset? timestamp = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            timestamp ?? new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] timestamps) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow()
        {
            if (index >= timestamps.Length)
            {
                return timestamps[^1];
            }

            return timestamps[index++];
        }
    }
}

internal static class ImageTelemetryAssertions
{
    public static bool IsApproximately(double actual, double expected) =>
        Math.Abs(actual - expected) < 0.000001;
}
