using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpTraceExporterTests
{
    [Fact]
    public async Task ExportAsync_WhenProtocolIsHttpProtobuf_PostsTracePayloadToSignalEndpoint()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpTraceExporter(
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

        await exporter.ExportAsync(CreateCompletedSpanRecords(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("http://collector.local:4318/otel/v1/traces"));
        request.ContentType.Should().Be("application/x-protobuf");
        request.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer token"));
        request.Payload.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenProtocolIsGrpc_PostsGrpcFramedTracePayload()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4317"),
                Protocol = OtlpProtocol.Grpc,
            },
            handler);

        await exporter.ExportAsync(CreateCompletedSpanRecords(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("http://collector.local:4317/opentelemetry.proto.collector.trace.v1.TraceService/Export"));
        request.ContentType.Should().Be("application/grpc");
        request.Headers.Should().Contain(new KeyValuePair<string, string>("TE", "trailers"));
        request.Payload.Should().HaveCountGreaterThan(5);
        request.Payload[0].Should().Be(0);
        DecodeGrpcPayloadLength(request.Payload).Should().Be(request.Payload.Length - 5);
    }

    [Fact]
    public async Task ExportAsync_WhenNoCompletedSpanExists_DoesNotSendRequest()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4318"),
                Protocol = OtlpProtocol.HttpProtobuf,
            },
            handler);

        await exporter.ExportAsync(
            [
                TelemetryRecord.Span(
                    DateTimeOffset.UnixEpoch,
                    "nina.sequence",
                    "nina.sequence.target",
                    SpanEventKind.Start,
                    "target-1",
                    TelemetryPriority.Normal),
            ],
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenGrpcStatusIsMissing_Throws()
    {
        using var handler = new RecordingHttpMessageHandler(addGrpcStatus: false);
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4317"),
                Protocol = OtlpProtocol.Grpc,
            },
            handler);

        var action = () => exporter.ExportAsync(CreateCompletedSpanRecords(), CancellationToken.None);

        await action.Should()
            .ThrowAsync<TelemetryExportException>()
            .WithMessage("*missing grpc-status*");
    }

    [Fact]
    public async Task ExportAsync_WhenSendFails_RetainsCompletedSpanForNextAttempt()
    {
        using var handler = new RecordingHttpMessageHandler(
            responseStatuses:
            [
                System.Net.HttpStatusCode.InternalServerError,
                System.Net.HttpStatusCode.OK,
            ]);
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4318"),
                Protocol = OtlpProtocol.HttpProtobuf,
            },
            handler);

        var firstAttempt = () => exporter.ExportAsync(CreateCompletedSpanRecords(), CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<TelemetryExportException>();
        await exporter.ExportAsync([], CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Payload.Should().NotBeEmpty();
        handler.Requests[1].Payload.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenSameFailedBatchIsReplayed_DoesNotDuplicatePendingCompletedSpan()
    {
        using var handler = new RecordingHttpMessageHandler(
            responseStatuses:
            [
                System.Net.HttpStatusCode.InternalServerError,
                System.Net.HttpStatusCode.OK,
            ]);
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions
            {
                Endpoint = new Uri("http://collector.local:4318"),
                Protocol = OtlpProtocol.HttpProtobuf,
            },
            handler);
        var records = CreateCompletedSpanRecords();

        var firstAttempt = () => exporter.ExportAsync(records, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<TelemetryExportException>();
        await exporter.ExportAsync(records, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Payload.Should().Equal(handler.Requests[0].Payload);
    }

    private static IReadOnlyList<TelemetryRecord> CreateCompletedSpanRecords()
    {
        var started = DateTimeOffset.UnixEpoch.AddSeconds(42);
        return
        [
            TelemetryRecord.Span(
                started,
                "nina.sequence",
                "nina.sequence.target",
                SpanEventKind.Start,
                "target-1",
                TelemetryPriority.Normal),
            TelemetryRecord.Span(
                started.AddSeconds(3),
                "nina.sequence",
                "nina.sequence.target",
                SpanEventKind.Stop,
                "target-1",
                TelemetryPriority.Normal),
        ];
    }

    private static int DecodeGrpcPayloadLength(byte[] payload) =>
        (payload[1] << 24) |
        (payload[2] << 16) |
        (payload[3] << 8) |
        payload[4];

    private sealed class RecordingHttpMessageHandler(
        bool addGrpcStatus = true,
        IReadOnlyList<System.Net.HttpStatusCode>? responseStatuses = null) : HttpMessageHandler
    {
        private int requestCount;

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

            var statusCode = responseStatuses is not null && requestCount < responseStatuses.Count
                ? responseStatuses[requestCount]
                : System.Net.HttpStatusCode.OK;
            requestCount++;

            var response = new HttpResponseMessage(statusCode);
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
