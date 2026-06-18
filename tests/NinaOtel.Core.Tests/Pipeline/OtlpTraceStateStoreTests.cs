using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpTraceStateStoreTests
{
    [Fact]
    public void Apply_WhenStopArrivesAfterStart_CompletesSpanWithOriginalTimestamps()
    {
        var store = new OtlpTraceStateStore();
        var started = new DateTimeOffset(2026, 6, 18, 21, 30, 0, TimeSpan.Zero);
        var stopped = started.AddSeconds(12);

        store.Apply(
        [
            TelemetryRecord.Span(
                started,
                "nina.sequence",
                "nina.sequence.target",
                SpanEventKind.Start,
                "target-1",
                TelemetryPriority.Important,
                new Dictionary<string, object?>
                {
                    ["target.name"] = "M42",
                }) with
                {
                    TraceId = "00112233445566778899aabbccddeeff",
                },
        ]).Should().BeEmpty();

        var completed = store.Apply(
        [
            TelemetryRecord.Span(
                stopped,
                "nina.sequence",
                "nina.sequence.target",
                SpanEventKind.Stop,
                "target-1",
                TelemetryPriority.Important,
                new Dictionary<string, object?>
                {
                    ["filter.name"] = "L",
                }),
        ]);

        completed.Should().ContainSingle();
        completed[0].Name.Should().Be("nina.sequence.target");
        completed[0].Source.Should().Be("nina.sequence");
        completed[0].StartTimestamp.Should().Be(started);
        completed[0].EndTimestamp.Should().Be(stopped);
        completed[0].TraceIdSeed.Should().Be("00112233445566778899aabbccddeeff");
        completed[0].Attributes.Should().Contain("target.name", "M42");
        completed[0].Attributes.Should().Contain("filter.name", "L");
    }

    [Fact]
    public void Apply_WhenEventArrivesBeforeStop_AddsSpanEvent()
    {
        var store = new OtlpTraceStateStore();
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var eventTime = DateTimeOffset.UnixEpoch.AddSeconds(15);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.capture.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
            TelemetryRecord.Span(
                eventTime,
                "nina.capture",
                "download",
                SpanEventKind.Event,
                "exp-1",
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["file.name"] = "M42_L_001.fit",
                }),
            TelemetryRecord.Span(stopped, "nina.capture", "nina.capture.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal),
        ]);

        completed.Should().ContainSingle();
        completed[0].Events.Should().ContainSingle();
        completed[0].Events[0].Name.Should().Be("download");
        completed[0].Events[0].Timestamp.Should().Be(eventTime);
        completed[0].Events[0].Attributes.Should().Contain("file.name", "M42_L_001.fit");
    }

    [Fact]
    public void Apply_WhenStopTimestampPrecedesStart_PreservesOriginalStopTimestamp()
    {
        var store = new OtlpTraceStateStore();
        var started = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(10);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.capture.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
            TelemetryRecord.Span(stopped, "nina.capture", "nina.capture.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal),
        ]);

        completed.Should().ContainSingle();
        completed[0].StartTimestamp.Should().Be(started);
        completed[0].EndTimestamp.Should().Be(stopped);
    }

    [Fact]
    public void Apply_WhenSameSpanIdExistsInDifferentTraceIds_CompletesMatchingTrace()
    {
        var store = new OtlpTraceStateStore();
        var firstTraceId = "00112233445566778899aabbccddeeff";
        var secondTraceId = "ffeeddccbbaa99887766554433221100";
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = firstTraceId,
            },
            TelemetryRecord.Span(started.AddSeconds(1), "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = secondTraceId,
            },
        ]).Should().BeEmpty();

        var completed = store.Apply(
        [
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = firstTraceId,
            },
            TelemetryRecord.Span(stopped.AddSeconds(1), "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = secondTraceId,
            },
        ]);

        completed.Should().HaveCount(2);
        completed.Select(static span => span.TraceIdSeed)
            .Should()
            .Equal(firstTraceId, secondTraceId);
    }

    [Fact]
    public void Apply_WhenExplicitTraceIdDoesNotMatchActiveSpan_DoesNotMutateOtherTrace()
    {
        var store = new OtlpTraceStateStore();
        var firstTraceId = "00112233445566778899aabbccddeeff";
        var secondTraceId = "ffeeddccbbaa99887766554433221100";
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var eventTime = DateTimeOffset.UnixEpoch.AddSeconds(15);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        store.Apply(
        [
            TelemetryRecord.Span(
                started,
                "nina.capture",
                "nina.exposure",
                SpanEventKind.Start,
                "exp-1",
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["trace"] = "first",
                }) with
                {
                    TraceId = firstTraceId,
                },
            TelemetryRecord.Span(
                eventTime,
                "nina.capture",
                "download",
                SpanEventKind.Event,
                "exp-1",
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["event"] = "wrong-trace",
                }) with
                {
                    TraceId = secondTraceId,
                },
        ]).Should().BeEmpty();

        store.Apply(
        [
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = secondTraceId,
            },
        ]).Should().BeEmpty();

        var completed = store.Apply(
        [
            TelemetryRecord.Span(stopped.AddSeconds(1), "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = firstTraceId,
            },
        ]);

        completed.Should().ContainSingle();
        completed[0].TraceIdSeed.Should().Be(firstTraceId);
        completed[0].Attributes.Should().Contain("trace", "first");
        completed[0].Events.Should().BeEmpty();
    }

    [Fact]
    public void Apply_WhenStopWithExplicitTraceIdMatchesSingleNonExplicitActiveSpan_CompletesThatSpan()
    {
        var store = new OtlpTraceStateStore();
        var traceId = "00112233445566778899aabbccddeeff";
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
        ]).Should().BeEmpty();

        var completed = store.Apply(
        [
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = traceId,
            },
        ]);

        completed.Should().ContainSingle();
        completed[0].TraceIdSeed.Should().Be(traceId);
        completed[0].StartTimestamp.Should().Be(started);
        completed[0].EndTimestamp.Should().Be(stopped);
    }

    [Fact]
    public void Apply_WhenExplicitTraceIdMissHasMultipleActiveSpanMatches_DoesNotUseNonExplicitFallback()
    {
        var store = new OtlpTraceStateStore();
        var firstTraceId = "00112233445566778899aabbccddeeff";
        var secondTraceId = "ffeeddccbbaa99887766554433221100";
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = firstTraceId,
            },
            TelemetryRecord.Span(started.AddSeconds(1), "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
        ]).Should().BeEmpty();

        store.Apply(
        [
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = secondTraceId,
            },
        ]).Should().BeEmpty();

        var completed = store.Apply(
        [
            TelemetryRecord.Span(stopped.AddSeconds(1), "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal) with
            {
                TraceId = firstTraceId,
            },
            TelemetryRecord.Span(stopped.AddSeconds(2), "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal),
        ]);

        completed.Should().HaveCount(2);
        completed.Select(static span => span.TraceIdSeed)
            .Should()
            .Contain(firstTraceId);
    }

    [Fact]
    public void Apply_WhenPendingCompletedSpanLimitIsExceeded_EvictsOldestPendingSpans()
    {
        var store = new OtlpTraceStateStore(maxActiveSpans: 8, maxCompletedSpans: 2);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(10);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(timestamp, "nina.capture", "nina.exposure.first", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal),
            TelemetryRecord.Span(timestamp.AddSeconds(1), "nina.capture", "nina.exposure.second", SpanEventKind.Stop, "exp-2", TelemetryPriority.Normal),
            TelemetryRecord.Span(timestamp.AddSeconds(2), "nina.capture", "nina.exposure.third", SpanEventKind.Stop, "exp-3", TelemetryPriority.Normal),
        ]);

        completed.Should().HaveCount(2);
        completed.Select(static span => span.Name)
            .Should()
            .Equal("nina.exposure.second", "nina.exposure.third");
    }

    [Fact]
    public void Apply_WhenActiveSpanLimitIsExceeded_EvictsOldestActiveSpans()
    {
        var store = new OtlpTraceStateStore(maxActiveSpans: 1, maxCompletedSpans: 8);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(10);

        store.Apply(
        [
            TelemetryRecord.Span(timestamp, "nina.capture", "nina.exposure.first", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
            TelemetryRecord.Span(timestamp.AddSeconds(1), "nina.capture", "nina.exposure.second", SpanEventKind.Start, "exp-2", TelemetryPriority.Normal),
        ]).Should().BeEmpty();

        store.ActiveSpanCount.Should().Be(1);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(timestamp.AddSeconds(2), "nina.capture", "nina.exposure.second", SpanEventKind.Stop, "exp-2", TelemetryPriority.Normal),
        ]);

        completed.Should().ContainSingle();
        completed[0].Name.Should().Be("nina.exposure.second");
    }

    [Fact]
    public void Apply_WhenTraceAndParentIdsAreWhitespace_IgnoresThem()
    {
        var store = new OtlpTraceStateStore();
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal, parentSpanId: " ") with
            {
                TraceId = " ",
            },
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal, parentSpanId: " ") with
            {
                TraceId = " ",
            },
        ]);

        completed.Should().ContainSingle();
        completed[0].TraceIdSeed.Should().NotBeNullOrWhiteSpace();
        completed[0].TraceIdSeed.Should().NotBe(" ");
        completed[0].ParentSpanId.Should().BeNull();
    }

    [Fact]
    public void MarkExportSucceeded_RemovesOnlyExportedCompletedSpans()
    {
        var store = new OtlpTraceStateStore();
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var stopped = DateTimeOffset.UnixEpoch.AddSeconds(20);

        var completed = store.Apply(
        [
            TelemetryRecord.Span(started, "nina.capture", "nina.exposure", SpanEventKind.Start, "exp-1", TelemetryPriority.Normal),
            TelemetryRecord.Span(stopped, "nina.capture", "nina.exposure", SpanEventKind.Stop, "exp-1", TelemetryPriority.Normal),
        ]);

        completed.Should().ContainSingle();

        store.Apply([]).Should().ContainSingle();
        store.MarkExportSucceeded(completed.Count);
        store.Apply([]).Should().BeEmpty();
    }
}
