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

    private sealed class LoopbackOtlpHttpServer : IAsyncDisposable
    {
        private readonly Func<string, HttpStatusCode> statusForPath;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdownCts = new();
        private readonly object syncRoot = new();
        private readonly List<string> requestPaths = [];
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
                    if (requestPaths.Count >= expectedCount)
                    {
                        return requestPaths.ToArray();
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
                return requestPaths.ToArray();
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
            lock (syncRoot)
            {
                requestPaths.Add(path);
            }

            var contentLength = ParseContentLength(headers);
            if (contentLength > 0)
            {
                await ReadBodyAsync(stream, contentLength, shutdownCts.Token).ConfigureAwait(false);
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

        private static async Task ReadBodyAsync(
            NetworkStream stream,
            int length,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[Math.Min(length, 8192)];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                remaining -= read;
            }
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
    }
}
