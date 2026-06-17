using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

public sealed class CollectorHealthReportingExporter : ITelemetryExporter, IDisposable
{
    private readonly ITelemetryExporter innerExporter;
    private readonly Action<CollectorHealthSnapshot> reportHealth;
    private readonly Uri endpoint;
    private readonly OtlpProtocol protocol;
    private readonly TimeProvider timeProvider;

    public CollectorHealthReportingExporter(
        ITelemetryExporter innerExporter,
        Action<CollectorHealthSnapshot> reportHealth,
        Uri endpoint,
        OtlpProtocol protocol,
        TimeProvider timeProvider)
    {
        this.innerExporter = innerExporter ?? throw new ArgumentNullException(nameof(innerExporter));
        this.reportHealth = reportHealth ?? throw new ArgumentNullException(nameof(reportHealth));
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.protocol = protocol;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            await innerExporter.ExportAsync(records, cancellationToken).ConfigureAwait(false);
            ReportSafely(CollectorHealthSnapshot.Healthy(
                endpoint,
                protocol,
                records.Count,
                timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportSafely(CollectorHealthSnapshot.Unhealthy(
                endpoint,
                protocol,
                ex.GetType().Name,
                ex.Message,
                timeProvider.GetUtcNow(),
                records.Count));
            throw;
        }
    }

    public void Dispose()
    {
        if (innerExporter is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ReportSafely(CollectorHealthSnapshot snapshot)
    {
        try
        {
            reportHealth(snapshot);
        }
        catch
        {
            // Collector health is diagnostic state; it must never break export.
        }
    }
}
