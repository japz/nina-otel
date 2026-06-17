using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public sealed class ReloadableTelemetryExporter : ITelemetryExporter, IDisposable
{
    private readonly object syncRoot = new();
    private ExporterSlot currentExporter;
    private bool disposed;

    public ReloadableTelemetryExporter(ITelemetryExporter initialExporter)
    {
        currentExporter = new ExporterSlot(initialExporter ?? throw new ArgumentNullException(nameof(initialExporter)));
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ExporterSlot exporter;
        lock (syncRoot)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ReloadableTelemetryExporter));
            }

            exporter = currentExporter;
            exporter.Acquire();
        }

        try
        {
            await exporter.ExportAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var disposableToDispose = exporter.Release();
            disposableToDispose?.Dispose();
        }
    }

    public void Update(ITelemetryExporter replacementExporter)
    {
        ArgumentNullException.ThrowIfNull(replacementExporter);

        IDisposable? disposableToDispose;
        lock (syncRoot)
        {
            if (disposed)
            {
                disposableToDispose = replacementExporter as IDisposable;
            }
            else
            {
                var previousExporter = currentExporter;
                Volatile.Write(ref currentExporter, new ExporterSlot(replacementExporter));
                disposableToDispose = previousExporter.Retire();
            }
        }

        disposableToDispose?.Dispose();
    }

    public void Dispose()
    {
        IDisposable? disposableToDispose;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            disposableToDispose = currentExporter.Retire();
        }

        disposableToDispose?.Dispose();
    }

    private sealed class ExporterSlot
    {
        private readonly object syncRoot = new();
        private readonly ITelemetryExporter exporter;
        private int activeExports;
        private bool retired;
        private bool disposed;

        public ExporterSlot(ITelemetryExporter exporter)
        {
            this.exporter = exporter;
        }

        public void Acquire()
        {
            lock (syncRoot)
            {
                if (retired)
                {
                    throw new ObjectDisposedException(nameof(ReloadableTelemetryExporter));
                }

                activeExports++;
            }
        }

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            exporter.ExportAsync(records, cancellationToken);

        public IDisposable? Release()
        {
            lock (syncRoot)
            {
                activeExports--;
                if (retired && activeExports == 0 && !disposed)
                {
                    disposed = true;
                    return exporter as IDisposable;
                }
            }

            return null;
        }

        public IDisposable? Retire()
        {
            lock (syncRoot)
            {
                retired = true;
                if (activeExports == 0 && !disposed)
                {
                    disposed = true;
                    return exporter as IDisposable;
                }
            }

            return null;
        }
    }
}
