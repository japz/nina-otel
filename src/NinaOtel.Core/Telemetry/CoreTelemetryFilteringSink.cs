using System.Threading;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Telemetry;

public sealed class CoreTelemetryFilteringSink : ITelemetrySink
{
    private readonly ITelemetrySink inner;
    private CoreTelemetryOptions options;

    public CoreTelemetryFilteringSink(
        ITelemetrySink inner,
        CoreTelemetryOptions options)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void UpdateOptions(CoreTelemetryOptions updatedOptions)
    {
        Volatile.Write(
            ref options,
            updatedOptions ?? throw new ArgumentNullException(nameof(updatedOptions)));
    }

    public bool TryPublish(TelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var snapshot = Volatile.Read(ref options);
        if (ShouldSuppress(record, snapshot))
        {
            return true;
        }

        return inner.TryPublish(record);
    }

    private static bool ShouldSuppress(
        TelemetryRecord record,
        CoreTelemetryOptions options)
    {
        if (record.Signal == TelemetrySignal.Health)
        {
            return false;
        }

        if (!IsCoreSource(record.Source))
        {
            return false;
        }

        if (!options.WorkflowTracesEnabled && record.Signal == TelemetrySignal.Span)
        {
            return true;
        }

        if (record.Signal != TelemetrySignal.Metric)
        {
            return false;
        }

        if (!options.EquipmentEnabled && NinaMetricCatalog.IsCoreEquipmentMetric(record.Name))
        {
            return true;
        }

        return !options.ImageStatsEnabled && NinaMetricCatalog.IsImageMetric(record.Name);
    }

    private static bool IsCoreSource(string source) =>
        string.Equals(source, "ninaotel.core", StringComparison.Ordinal) ||
        source.StartsWith("nina.", StringComparison.Ordinal);
}
