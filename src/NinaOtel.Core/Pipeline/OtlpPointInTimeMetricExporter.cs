using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpPointInTimeMetricExporter : IDisposable
{
    private readonly OtlpDirectTransport transport;

    public OtlpPointInTimeMetricExporter(OtlpOptions options, Func<HttpClient>? httpClientFactory = null)
    {
        transport = new OtlpDirectTransport(options, "metrics", httpClientFactory);
    }

    internal OtlpPointInTimeMetricExporter(OtlpOptions options, HttpMessageHandler handler)
        : this(options, () => new HttpClient(handler))
    {
    }

    public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
        transport.SendAsync(OtlpPointInTimeMetricSerializer.Serialize(records), cancellationToken);

    public void Dispose() => transport.Dispose();
}
