using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpTelemetryExporterTests
{
    [Fact]
    public void CreateExporterOptions_WhenProtocolIsHttpProtobuf_UsesHttpClientFactory()
    {
        var options = new OtlpOptions
        {
            Protocol = OtlpProtocol.HttpProtobuf,
        };

        var exporterOptions = OtlpTelemetryExporter.CreateExporterOptions(options, "v1/logs");

        exporterOptions.HttpClientFactory.Should().NotBeNull();
    }

    [Fact]
    public void CreateExporterOptions_WhenTlsPathsAreConfigured_UsesHttpClientFactory()
    {
        var options = new OtlpOptions
        {
            Protocol = OtlpProtocol.HttpProtobuf,
            Auth = new OtlpAuthOptions
            {
                CaCertificatePemPath = "ca.pem",
            },
        };

        var exporterOptions = OtlpTelemetryExporter.CreateExporterOptions(options, "v1/logs");

        exporterOptions.HttpClientFactory.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateExporterOptions_WhenTlsClientFactoryCannotCreateClient_ReturnsFailingClient()
    {
        var options = new OtlpOptions
        {
            Protocol = OtlpProtocol.HttpProtobuf,
            Auth = new OtlpAuthOptions
            {
                CaCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };
        var exporterOptions = OtlpTelemetryExporter.CreateExporterOptions(options, "v1/logs");

        using var client = exporterOptions.HttpClientFactory!();
        var send = () => client.GetAsync(new Uri("http://collector.local/"), CancellationToken.None);

        await send.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void CreateExporterOptions_WhenTlsPathsAreConfiguredWithGrpc_ThrowsNotSupportedException()
    {
        var options = new OtlpOptions
        {
            Protocol = OtlpProtocol.Grpc,
            Auth = new OtlpAuthOptions
            {
                CaCertificatePemPath = "ca.pem",
            },
        };

        Action create = () => OtlpTelemetryExporter.CreateExporterOptions(options, "v1/logs");

        create.Should()
            .Throw<NotSupportedException>()
            .WithMessage("*PEM TLS requires HTTP/protobuf*");
    }

    [Fact]
    public void Constructor_WhenTlsCertificatePathDoesNotExist_DoesNotThrow()
    {
        var options = new NinaOtelOptions
        {
            Otlp = new OtlpOptions
            {
                Protocol = OtlpProtocol.HttpProtobuf,
                Auth = new OtlpAuthOptions
                {
                    CaCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
                },
            },
        };

        using var exporter = new OtlpTelemetryExporter(options);
    }

    [Fact]
    public async Task ExportAsync_WhenLazyHttpClientFactoryThrows_CachesCreationFailure()
    {
        var attempts = 0;
        using var exporter = new OtlpTraceExporter(
            new OtlpOptions { Protocol = OtlpProtocol.HttpProtobuf },
            () =>
            {
                attempts++;
                throw new FileNotFoundException("missing cert", "ca.pem");
            });
        var records = CreateCompletedSpanRecords();

        var firstExport = () => exporter.ExportAsync(records, CancellationToken.None);
        var secondExport = () => exporter.ExportAsync(records, CancellationToken.None);

        await firstExport.Should().ThrowAsync<FileNotFoundException>();
        await secondExport.Should().ThrowAsync<FileNotFoundException>();

        attempts.Should().Be(1);
    }

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

    [Fact]
    public async Task ExportAsync_WhenTraceExportFails_ExportsSpanLogBreadcrumbBeforeThrowing()
    {
        await using var server = new LoopbackOtlpHttpServer(
            static path => path.EndsWith("/v1/traces", StringComparison.Ordinal)
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK);
        using var exporter = new OtlpTelemetryExporter(new NinaOtelOptions
        {
            Otlp = new OtlpOptions
            {
                Endpoint = server.Endpoint,
                Protocol = OtlpProtocol.HttpProtobuf,
                Timeout = TimeSpan.FromSeconds(5),
            },
        });
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var records = new[]
        {
            TelemetryRecord.Span(
                started,
                "nina.capture",
                "nina.exposure",
                SpanEventKind.Start,
                "exp-1",
                TelemetryPriority.Normal),
            TelemetryRecord.Span(
                started.AddSeconds(5),
                "nina.capture",
                "nina.exposure",
                SpanEventKind.Stop,
                "exp-1",
                TelemetryPriority.Normal),
        };

        var action = () => exporter.ExportAsync(records, CancellationToken.None);

        await action.Should()
            .ThrowAsync<TelemetryExportException>()
            .WithMessage("*trace exporter returned HTTP 500*");
        var paths = await server.WaitForRequestCountAsync(3, TimeSpan.FromSeconds(5));
        paths.Should().Contain("/v1/traces");
        paths.TakeWhile(static path => path != "/v1/traces")
            .Should()
            .OnlyContain(static path => path == "/v1/logs");
        Array.IndexOf(paths.ToArray(), "/v1/logs")
            .Should()
            .BeLessThan(Array.IndexOf(paths.ToArray(), "/v1/traces"));
    }

    [Fact]
    public async Task ExportAsync_WhenLiveAndDeferredMetricsAreMixed_PostsSeparatePointInTimeMetricPayload()
    {
        await using var server = new LoopbackOtlpHttpServer(static _ => HttpStatusCode.OK);
        using var exporter = new OtlpTelemetryExporter(new NinaOtelOptions
        {
            Otlp = new OtlpOptions
            {
                Endpoint = server.Endpoint,
                Protocol = OtlpProtocol.HttpProtobuf,
                Timeout = TimeSpan.FromSeconds(5),
            },
        });

        await exporter.ExportAsync(
            [
                TelemetryRecord.Metric(
                    DateTimeOffset.UnixEpoch.AddSeconds(20),
                    "nina.image",
                    "image_mean",
                    1842.5,
                    TelemetryPriority.Normal,
                    new Dictionary<string, object?>
                    {
                        ["image_file_name"] = "M42_L_001.fit",
                        ["camera_name"] = "ASI2600MM",
                    }),
                TelemetryRecord.Metric(
                    DateTimeOffset.UnixEpoch.AddSeconds(21),
                    "nina.camera",
                    "camera_sensor_temperature",
                    -7.25,
                    TelemetryPriority.Normal,
                    new Dictionary<string, object?>
                    {
                        ["camera_name"] = "ASI2600MM",
                    }),
            ],
            CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2, TimeSpan.FromSeconds(5));
        var metricRequests = requests
            .Where(static request => request.Path == "/v1/metrics")
            .ToArray();

        metricRequests.Should().HaveCountGreaterThanOrEqualTo(2);
        var pointInTimeRequest = metricRequests
            .Should()
            .ContainSingle(static request => ContainsMetricName(request.Body, "image_mean"))
            .Which;
        MetricNames(pointInTimeRequest.Body).Should()
            .Contain("image_mean")
            .And
            .NotContain("camera_sensor_temperature");
        metricRequests.Should()
            .Contain(static request => ContainsMetricName(request.Body, "camera_sensor_temperature"));
    }

    private static TelemetryRecord[] CreateCompletedSpanRecords()
    {
        var started = DateTimeOffset.UnixEpoch.AddSeconds(10);
        return
        [
            TelemetryRecord.Span(
                started,
                "nina.capture",
                "nina.exposure",
                SpanEventKind.Start,
                "exp-1",
                TelemetryPriority.Normal),
            TelemetryRecord.Span(
                started.AddSeconds(5),
                "nina.capture",
                "nina.exposure",
                SpanEventKind.Stop,
                "exp-1",
                TelemetryPriority.Normal),
        ];
    }

    private static bool ContainsMetricName(byte[] payload, string metricName) =>
        MetricNames(payload).Contains(metricName, StringComparer.Ordinal);

    private static IReadOnlyList<string> MetricNames(byte[] payload) =>
        ProtobufFieldScanner.FindStrings(payload, "1.2.2.1");

    private sealed class LoopbackOtlpHttpServer : IAsyncDisposable
    {
        private readonly Func<string, HttpStatusCode> statusForPath;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdownCts = new();
        private readonly object syncRoot = new();
        private readonly List<RecordedRequest> requests = [];
        private readonly Task acceptLoop;

        public LoopbackOtlpHttpServer(Func<string, HttpStatusCode> statusForPath)
        {
            this.statusForPath = statusForPath;
            listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}");
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri Endpoint { get; }

        public async ValueTask DisposeAsync()
        {
            await shutdownCts.CancelAsync();
            listener.Stop();

            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            shutdownCts.Dispose();
        }

        public async Task<IReadOnlyList<string>> WaitForRequestCountAsync(
            int expectedCount,
            TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            while (!timeoutCts.IsCancellationRequested)
            {
                lock (syncRoot)
                {
                    if (requests.Count >= expectedCount)
                    {
                        return requests.Select(static request => request.Path).ToArray();
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            lock (syncRoot)
            {
                return requests.Select(static request => request.Path).ToArray();
            }
        }

        public async Task<IReadOnlyList<RecordedRequest>> WaitForRequestsAsync(
            int expectedCount,
            TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            while (!timeoutCts.IsCancellationRequested)
            {
                lock (syncRoot)
                {
                    if (requests.Count >= expectedCount)
                    {
                        return requests.ToArray();
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            lock (syncRoot)
            {
                return requests.ToArray();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!shutdownCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (shutdownCts.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var ownedClient = client;
            var stream = client.GetStream();
            var headers = await ReadHeadersAsync(stream, shutdownCts.Token).ConfigureAwait(false);
            if (headers.Length == 0)
            {
                return;
            }

            var path = ParsePath(headers);
            var contentLength = ParseContentLength(headers);
            var body = contentLength > 0
                ? await ReadBodyAsync(stream, contentLength, shutdownCts.Token).ConfigureAwait(false)
                : [];

            lock (syncRoot)
            {
                requests.Add(new RecordedRequest(path, body));
            }

            await WriteResponseAsync(stream, statusForPath(path), shutdownCts.Token).ConfigureAwait(false);
        }

        private static async Task<string> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (bytes.Count < 32 * 1024)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytes.Add(buffer[0]);
                if (bytes.Count >= 4 &&
                    bytes[^4] == '\r' &&
                    bytes[^3] == '\n' &&
                    bytes[^2] == '\r' &&
                    bytes[^1] == '\n')
                {
                    break;
                }
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static string ParsePath(string headers)
        {
            var firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
            var firstLine = firstLineEnd < 0 ? headers : headers[..firstLineEnd];
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : string.Empty;
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["Content-Length:".Length..].Trim(), out var contentLength))
                {
                    return contentLength;
                }
            }

            return 0;
        }

        private static async Task<byte[]> ReadBodyAsync(
            NetworkStream stream,
            int length,
            CancellationToken cancellationToken)
        {
            var body = new byte[length];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await stream
                    .ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            return offset == body.Length ? body : body[..offset];
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            HttpStatusCode statusCode,
            CancellationToken cancellationToken)
        {
            var reasonPhrase = statusCode == HttpStatusCode.OK ? "OK" : "Internal Server Error";
            var response =
                $"HTTP/1.1 {(int)statusCode} {reasonPhrase}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            var payload = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(payload.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        public sealed record RecordedRequest(string Path, byte[] Body);
    }
}
