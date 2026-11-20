using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpDirectTransport : IDisposable
{
    private readonly OtlpOptions options;
    private readonly string signalName;
    private readonly string grpcServicePath;
    private readonly object httpClientSyncRoot = new();
    private readonly Func<HttpClient> httpClientFactory;
    private HttpClient? httpClient;
    private ExceptionDispatchInfo? httpClientCreationFailure;

    public OtlpDirectTransport(OtlpOptions options, string signalName, Func<HttpClient>? httpClientFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.signalName = signalName;
        grpcServicePath = signalName switch
        {
            "metrics" => "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export",
            "logs" => "/opentelemetry.proto.collector.logs.v1.LogsService/Export",
            "traces" => "/opentelemetry.proto.collector.trace.v1.TraceService/Export",
            _ => throw new ArgumentOutOfRangeException(nameof(signalName)),
        };
        this.httpClientFactory = httpClientFactory ?? (() => OtlpHttpClientFactory.Create(options));
    }

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
        {
            return;
        }

        using var request = options.Protocol == OtlpProtocol.HttpProtobuf
            ? CreateHttpProtobufRequest(payload)
            : CreateGrpcRequest(payload);
        var client = GetHttpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var description = signalName == "traces" ? "trace" : signalName;
        if (!response.IsSuccessStatusCode)
        {
            throw new TelemetryExportException(
                $"OTLP {description} exporter returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                isPermanent: (int)response.StatusCode is 400 or 413 or 422);
        }

        if (options.Protocol == OtlpProtocol.Grpc)
        {
            await EnsureGrpcSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        }
    }

    public void Dispose() => httpClient?.Dispose();

    private HttpClient GetHttpClient()
    {
        if (httpClient is not null)
        {
            return httpClient;
        }

        httpClientCreationFailure?.Throw();

        lock (httpClientSyncRoot)
        {
            httpClientCreationFailure?.Throw();
            httpClient ??= CreateConfiguredHttpClient();
            return httpClient;
        }
    }

    private HttpClient CreateConfiguredHttpClient()
    {
        try
        {
            var client = httpClientFactory();
            client.Timeout = options.Timeout <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : options.Timeout;
            return client;
        }
        catch (Exception ex)
        {
            httpClientCreationFailure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    private HttpRequestMessage CreateHttpProtobufRequest(byte[] payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            OtlpTelemetryExporter.CreateSignalEndpoint(options, $"v1/{signalName}"))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        ApplyHeaders(request);
        return request;
    }

    private HttpRequestMessage CreateGrpcRequest(byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CreateGrpcEndpoint(options.Endpoint))
        {
            Content = new ByteArrayContent(CreateGrpcFrame(payload)),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        ApplyHeaders(request);
        return request;
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var header in options.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private Uri CreateGrpcEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path)
            ? grpcServicePath
            : path.EndsWith(grpcServicePath, StringComparison.Ordinal)
                ? path
                : $"{path}{grpcServicePath}";
        return builder.Uri;
    }

    private static byte[] CreateGrpcFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = 0;
        frame[1] = (byte)(payload.Length >> 24);
        frame[2] = (byte)(payload.Length >> 16);
        frame[3] = (byte)(payload.Length >> 8);
        frame[4] = (byte)payload.Length;
        Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        return frame;
    }

    private async Task EnsureGrpcSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!TryGetGrpcStatus(response.Headers, out var status))
        {
            // gRPC status is usually in HTTP/2 trailers, available after reading the body.
            await response.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            TryGetGrpcStatus(response.TrailingHeaders, out status);
        }

        if (status == "0")
        {
            return;
        }

        throw status is null
            ? new TelemetryExportException($"OTLP gRPC {signalName} exporter response is missing grpc-status.")
            : new TelemetryExportException($"OTLP gRPC {signalName} exporter returned grpc-status {status}.",
                isPermanent: status == "3");
    }

    private static bool TryGetGrpcStatus(HttpHeaders headers, out string? status)
    {
        if (headers.TryGetValues("grpc-status", out var values))
        {
            status = values.FirstOrDefault();
            return status is not null;
        }

        status = null;
        return false;
    }
}
