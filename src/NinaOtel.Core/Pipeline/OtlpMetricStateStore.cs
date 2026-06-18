using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpMetricStateStore
{
    private static readonly IReadOnlyList<Measurement<double>> EmptyMeasurements =
        Array.AsReadOnly(Array.Empty<Measurement<double>>());

    private readonly object syncRoot = new();
    private readonly Meter meter;
    private readonly Dictionary<string, MetricInstrumentState> instruments = new(StringComparer.Ordinal);

    public OtlpMetricStateStore(Meter meter)
    {
        this.meter = meter ?? throw new ArgumentNullException(nameof(meter));
    }

    public IReadOnlyCollection<string> InstrumentNames
    {
        get
        {
            lock (syncRoot)
            {
                return Array.AsReadOnly(instruments.Keys.Order(StringComparer.Ordinal).ToArray());
            }
        }
    }

    public int Apply(IReadOnlyList<TelemetryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var accepted = 0;
        foreach (var record in records)
        {
            if (Apply(record))
            {
                accepted++;
            }
        }

        return accepted;
    }

    public IReadOnlyList<Measurement<double>> CollectMeasurements(string instrumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentName);

        lock (syncRoot)
        {
            return instruments.TryGetValue(instrumentName, out var instrument)
                ? instrument.Collect()
                : EmptyMeasurements;
        }
    }

    private bool Apply(TelemetryRecord record)
    {
        if (record.Signal != TelemetrySignal.Metric ||
            !record.NumericValue.HasValue ||
            !NinaMetricCatalog.TryGetExportKind(record.Name, out var exportKind))
        {
            return false;
        }

        lock (syncRoot)
        {
            var tags = CreateTags(record, exportKind);
            if (exportKind == NinaMetricExportKind.LiveObservableGauge &&
                double.IsNaN(record.NumericValue.Value))
            {
                return instruments.TryGetValue(record.Name, out var existingInstrument) &&
                    existingInstrument.Remove(tags);
            }

            if (double.IsNaN(record.NumericValue.Value))
            {
                return false;
            }

            var instrument = GetOrCreateInstrument(record.Name, exportKind);
            instrument.Update(record, tags);
            return true;
        }
    }

    private MetricInstrumentState GetOrCreateInstrument(string instrumentName, NinaMetricExportKind exportKind)
    {
        if (instruments.TryGetValue(instrumentName, out var instrument))
        {
            return instrument;
        }

        instrument = new MetricInstrumentState(
            meter.CreateObservableGauge(
                instrumentName,
                () => CollectMeasurements(instrumentName),
                unit: null,
                description: null),
            exportKind);
        instruments.Add(instrumentName, instrument);
        return instrument;
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> CreateTags(
        TelemetryRecord record,
        NinaMetricExportKind exportKind)
    {
        var allowedAttributes = NinaMetricCatalog.GetMetricAttributeNames(record.Name, exportKind);
        if (allowedAttributes is null)
        {
            return Array.AsReadOnly(Array.Empty<KeyValuePair<string, object?>>());
        }

        var tags = new List<KeyValuePair<string, object?>>(record.Attributes.Count + 1);
        foreach (var attribute in record.Attributes.OrderBy(static attribute => attribute.Key, StringComparer.Ordinal))
        {
            if (allowedAttributes.Contains(attribute.Key))
            {
                tags.Add(new KeyValuePair<string, object?>(attribute.Key, attribute.Value));
            }
        }

        tags.Add(new KeyValuePair<string, object?>("ninaotel.source", record.Source));

        return tags.AsReadOnly();
    }

    private static string CreatePointKey(IReadOnlyList<KeyValuePair<string, object?>> tags) =>
        string.Join(
            "\u001f",
            tags.Select(static tag => $"{tag.Key}\u001e{tag.Value?.GetType().FullName}\u001e{tag.Value}"));

    private sealed class MetricInstrumentState(ObservableGauge<double> gauge, NinaMetricExportKind exportKind)
    {
        private readonly ObservableGauge<double> gauge = gauge;
        private readonly NinaMetricExportKind exportKind = exportKind;
        private readonly Dictionary<string, MetricPoint> livePoints = new(StringComparer.Ordinal);
        private readonly Queue<MetricPoint> oneShotPoints = new();

        public IReadOnlyList<Measurement<double>> Collect()
        {
            var points = exportKind == NinaMetricExportKind.DeferredPointInTime
                ? DrainOneShotPoints()
                : livePoints.Values.ToArray();

            var measurements = points
                .Select(static point => new Measurement<double>(point.Value, point.Tags))
                .ToArray();

            return Array.AsReadOnly(measurements);
        }

        public void Update(
            TelemetryRecord record,
            IReadOnlyList<KeyValuePair<string, object?>> tags)
        {
            if (exportKind == NinaMetricExportKind.DeferredPointInTime)
            {
                oneShotPoints.Enqueue(new MetricPoint(record.NumericValue!.Value, tags));
            }
            else
            {
                livePoints[CreatePointKey(tags)] = new MetricPoint(record.NumericValue!.Value, tags);
            }

            GC.KeepAlive(gauge);
        }

        public bool Remove(IReadOnlyList<KeyValuePair<string, object?>> tags) =>
            livePoints.Remove(CreatePointKey(tags));

        private MetricPoint[] DrainOneShotPoints()
        {
            if (oneShotPoints.Count == 0)
            {
                return [];
            }

            var points = oneShotPoints.ToArray();
            oneShotPoints.Clear();
            return points;
        }
    }

    private sealed record MetricPoint(
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
