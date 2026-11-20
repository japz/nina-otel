using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpTraceExporter : IDisposable
{
    private readonly OtlpTraceStateStore stateStore = new();
    private readonly OtlpDirectTransport transport;

    public OtlpTraceExporter(OtlpOptions options, Func<HttpClient>? httpClientFactory = null)
    {
        transport = new OtlpDirectTransport(options, "traces", httpClientFactory);
    }

    internal OtlpTraceExporter(OtlpOptions options, HttpMessageHandler handler)
        : this(options, () => new HttpClient(handler))
    {
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        var completedSpans = stateStore.Apply(records);
        await transport.SendAsync(OtlpTraceSerializer.Serialize(completedSpans), cancellationToken).ConfigureAwait(false);
        stateStore.MarkExportSucceeded(completedSpans.Count);
    }

    public void Dispose() => transport.Dispose();
}
