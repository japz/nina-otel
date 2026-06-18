using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpPointInTimeMetricExporterTests
{
    [Fact]
    public async Task ExportAsync_WhenProtocolIsHttpProtobuf_PostsMetricsPayloadToSignalEndpoint()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpPointInTimeMetricExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4318/otel"),
                Protocol = OtlpProtocol.HttpProtobuf,
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer token",
                },
            },
            handler);
        var record = CreateImageMetric();

        await exporter.ExportAsync([record], CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("http://collector.local:4318/otel/v1/metrics"));
        request.ContentType.Should().Be("application/x-protobuf");
        request.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer token"));
        request.Payload.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenProtocolIsGrpc_PostsGrpcFramedMetricsPayload()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpPointInTimeMetricExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4317"),
                Protocol = OtlpProtocol.Grpc,
            },
            handler);
        var record = CreateImageMetric();

        await exporter.ExportAsync([record], CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("http://collector.local:4317/opentelemetry.proto.collector.metrics.v1.MetricsService/Export"));
        request.ContentType.Should().Be("application/grpc");
        request.Headers.Should().Contain(new KeyValuePair<string, string>("TE", "trailers"));
        request.Payload.Should().HaveCountGreaterThan(5);
        request.Payload[0].Should().Be(0);
        DecodeGrpcPayloadLength(request.Payload).Should().Be(request.Payload.Length - 5);
    }

    [Fact]
    public async Task ExportAsync_WhenNoDeferredMetrics_DoesNotSendRequest()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpPointInTimeMetricExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4318"),
                Protocol = OtlpProtocol.HttpProtobuf,
            },
            handler);

        await exporter.ExportAsync(
            [
                TelemetryRecord.Metric(
                    DateTimeOffset.UnixEpoch,
                    "nina.camera",
                    "camera_sensor_temperature",
                    -7.25,
                    TelemetryPriority.Normal),
            ],
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenGrpcStatusIsMissing_Throws()
    {
        using var handler = new RecordingHttpMessageHandler(addGrpcStatus: false);
        using var exporter = new OtlpPointInTimeMetricExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4317"),
                Protocol = OtlpProtocol.Grpc,
            },
            handler);

        var action = () => exporter.ExportAsync([CreateImageMetric()], CancellationToken.None);

        await action.Should()
            .ThrowAsync<TelemetryExportException>()
            .WithMessage("*missing grpc-status*");
    }

    private static TelemetryRecord CreateImageMetric() =>
        TelemetryRecord.Metric(
            DateTimeOffset.UnixEpoch.AddSeconds(42),
            "nina.image",
            "image_mean",
            1842.5,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["image_file_name"] = "M42_L_001.fit",
            });

    private static int DecodeGrpcPayloadLength(byte[] payload) =>
        (payload[1] << 24) |
        (payload[2] << 16) |
        (payload[3] << 8) |
        payload[4];

    private sealed class RecordingHttpMessageHandler(bool addGrpcStatus = true) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(
                    static header => header.Key,
                    static header => string.Join(",", header.Value)),
                payload));

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            if (addGrpcStatus)
            {
                response.Headers.TryAddWithoutValidation("grpc-status", "0");
            }

            return response;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Payload);
}
