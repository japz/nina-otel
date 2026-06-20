using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpTraceExporter : IDisposable
{
    private const string GrpcTraceServicePath = "/opentelemetry.proto.collector.trace.v1.TraceService/Export";

    private readonly OtlpOptions options;
    private readonly OtlpTraceStateStore stateStore = new();
    private readonly object httpClientSyncRoot = new();
    private readonly Func<HttpClient> httpClientFactory;
    private readonly bool ownsHttpClient;
    private HttpClient? httpClient;
    private ExceptionDispatchInfo? httpClientCreationFailure;

    public OtlpTraceExporter(OtlpOptions options)
        : this(options, () => OtlpHttpClientFactory.Create(options), ownsHttpClient: true)
    {
    }

    internal OtlpTraceExporter(OtlpOptions options, HttpMessageHandler handler)
        : this(options, () => new HttpClient(handler), ownsHttpClient: true)
    {
    }

    internal OtlpTraceExporter(OtlpOptions options, Func<HttpClient> httpClientFactory)
        : this(options, httpClientFactory, ownsHttpClient: true)
    {
    }

    private OtlpTraceExporter(
        OtlpOptions options,
        Func<HttpClient> httpClientFactory,
        bool ownsHttpClient)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.ownsHttpClient = ownsHttpClient;
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        var completedSpans = stateStore.Apply(records);
        var payload = OtlpTraceSerializer.Serialize(completedSpans);
        if (payload.Length == 0)
        {
            return;
        }

        using var request = options.Protocol == OtlpProtocol.HttpProtobuf
            ? CreateHttpProtobufRequest(payload)
            : CreateGrpcRequest(payload);
        using var response = await GetHttpClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new TelemetryExportException(
                $"OTLP trace exporter returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (options.Protocol == OtlpProtocol.Grpc)
        {
            await EnsureGrpcSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        stateStore.MarkExportSucceeded(completedSpans.Count);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient?.Dispose();
        }
    }

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
            OtlpTelemetryExporter.CreateSignalEndpoint(options, "v1/traces"))
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

    private static Uri CreateGrpcEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path)
            ? GrpcTraceServicePath
            : path.EndsWith(GrpcTraceServicePath, StringComparison.Ordinal)
                ? path
                : $"{path}{GrpcTraceServicePath}";
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

    private static async Task EnsureGrpcSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (TryGetGrpcStatus(response.Headers, out var status) && status == "0")
        {
            return;
        }

        await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (TryGetGrpcStatus(response.TrailingHeaders, out status) && status == "0")
        {
            return;
        }

        throw status is null
            ? new TelemetryExportException("OTLP gRPC trace exporter response is missing grpc-status.")
            : new TelemetryExportException($"OTLP gRPC trace exporter returned grpc-status {status}.");
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
