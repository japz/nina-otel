using FluentAssertions;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpTelemetryExporterTests
{
    [Fact]
    public void CreateSignalEndpoint_WhenHttpEndpointHasDifferentSignalPath_ReplacesSignalPath()
    {
        var options = new OtlpOptions
        {
            Endpoint = new Uri("http://collector.local:4318/v1/logs"),
            Protocol = OtlpProtocol.HttpProtobuf,
        };

        var endpoint = OtlpTelemetryExporter.CreateSignalEndpoint(options, "v1/metrics");

        endpoint.Should().Be(new Uri("http://collector.local:4318/v1/metrics"));
    }

    [Fact]
    public void CreateSignalEndpoint_WhenHttpEndpointHasBasePath_AppendsSignalPath()
    {
        var options = new OtlpOptions
        {
            Endpoint = new Uri("http://collector.local:4318/otel"),
            Protocol = OtlpProtocol.HttpProtobuf,
        };

        var endpoint = OtlpTelemetryExporter.CreateSignalEndpoint(options, "v1/logs");

        endpoint.Should().Be(new Uri("http://collector.local:4318/otel/v1/logs"));
    }
}
