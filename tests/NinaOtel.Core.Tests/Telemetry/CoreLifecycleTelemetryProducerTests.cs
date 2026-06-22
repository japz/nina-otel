using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Telemetry;

public sealed class CoreLifecycleTelemetryProducerTests
{
    [Fact]
    public void PluginInitialized_PublishesNonSecretLifecycleRecord()
    {
        var sink = new RecordingSink();
        var options = NinaOtelOptions.CreateDefault() with
        {
            Otlp = NinaOtelOptions.CreateDefault().Otlp with
            {
                Endpoint = new Uri("http://collector.local:4317/"),
                Protocol = OtlpProtocol.Grpc,
            },
            Buffer = NinaOtelOptions.CreateDefault().Buffer with
            {
                DiskOnFailureEnabled = true,
            },
        };
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            options);

        producer.PluginInitialized();

        var record = sink.Records.Should()
            .ContainSingle(static candidate => candidate.Signal == TelemetrySignal.Health)
            .Which;
        record.Signal.Should().Be(TelemetrySignal.Health);
        record.Source.Should().Be("ninaotel.core");
        record.Name.Should().Be("ninaotel.plugin.lifecycle");
        record.Priority.Should().Be(TelemetryPriority.Important);
        record.Attributes["status"].Should().Be("initialized");
        record.Attributes["otlp.protocol"].Should().Be("Grpc");
        record.Attributes["otlp.endpoint.configured"].Should().Be(true);
        record.Attributes.Should().NotContainKey("otlp.endpoint");
    }

    [Fact]
    public void PluginInitialized_PublishesSessionStartSpan()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault(),
            " 0.1.0-alpha.test ");

        producer.PluginInitialized();

        var record = sink.Records.Should()
            .ContainSingle(static candidate =>
                candidate.Signal == TelemetrySignal.Span &&
                candidate.Name == "nina.session" &&
                candidate.SpanKind == SpanEventKind.Start)
            .Which;
        record.Source.Should().Be("ninaotel.core");
        record.SpanId.Should().Be("nina.session");
        record.ParentSpanId.Should().BeNull();
        record.Priority.Should().Be(TelemetryPriority.Important);
        record.Attributes.Should().Contain("service.name", "NinaOtel");
        record.Attributes.Should().Contain("ninaotel.component", "core");
        record.Attributes.Should().Contain("service.version", "0.1.0-alpha.test");
    }

    [Fact]
    public void PluginStopped_AfterPluginInitialized_PublishesSessionStopSpan()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault(),
            "0.1.0-alpha.test");

        producer.PluginInitialized();
        producer.PluginStopped();

        var record = sink.Records.Should()
            .ContainSingle(static candidate =>
                candidate.Signal == TelemetrySignal.Span &&
                candidate.Name == "nina.session" &&
                candidate.SpanKind == SpanEventKind.Stop)
            .Which;
        record.Source.Should().Be("ninaotel.core");
        record.SpanId.Should().Be("nina.session");
        record.ParentSpanId.Should().BeNull();
        record.Priority.Should().Be(TelemetryPriority.Important);
        record.Attributes.Should().Contain("service.name", "NinaOtel");
        record.Attributes.Should().Contain("ninaotel.component", "core");
        record.Attributes.Should().Contain("service.version", "0.1.0-alpha.test");
    }

    [Fact]
    public void PluginStopped_BeforeSessionStarted_DoesNotPublishSessionStopSpan()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault(),
            "0.1.0-alpha.test");

        producer.PluginStopped();

        sink.Records.Should().ContainSingle(static record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["status"], "stopped"));
        sink.Records.Should().NotContain(static record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "nina.session");
    }

    [Fact]
    public void PluginStopped_WhenCalledTwiceAfterStart_PublishesOneSessionStopSpan()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault(),
            "0.1.0-alpha.test");

        producer.PluginInitialized();
        producer.PluginStopped();
        producer.PluginStopped();

        sink.Records.Should().ContainSingle(static record =>
            record.Signal == TelemetrySignal.Span &&
            record.Name == "nina.session" &&
            record.SpanKind == SpanEventKind.Stop);
    }

    [Fact]
    public void ProfileChanged_PublishesSettingsLoadedRecord()
    {
        var sink = new RecordingSink();
        var producer = new CoreLifecycleTelemetryProducer(
            sink,
            TimeProvider.System,
            NinaOtelOptions.CreateDefault());

        producer.ProfileChanged(NinaOtelOptions.CreateDefault() with
        {
            Otlp = NinaOtelOptions.CreateDefault().Otlp with
            {
                Protocol = OtlpProtocol.HttpProtobuf,
            },
        });

        sink.Records.Should().ContainSingle();
        var record = sink.Records[0];
        record.Attributes["status"].Should().Be("settings_loaded");
        record.Attributes["otlp.protocol"].Should().Be("HttpProtobuf");
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public bool TryPublish(TelemetryRecord record)
        {
            Records.Add(record);
            return true;
        }
    }
}
