namespace NinaOtel.Core.Pipeline;

[Flags]
internal enum SpoolSignal
{
    None = 0,
    Metrics = 1,
    Logs = 2,
    Traces = 4,
    All = Metrics | Logs | Traces,
}

internal interface ISpoolReplayExporter : IDisposable
{
    Task ExportAsync(SpoolSignal signal, DiskTelemetrySpool.Batch batch, CancellationToken cancellationToken);

    void Acknowledge(DiskTelemetrySpool.Batch batch, SpoolSignal signals) { }
}
