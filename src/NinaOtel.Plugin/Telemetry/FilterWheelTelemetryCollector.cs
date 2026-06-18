using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

public sealed class FilterWheelTelemetryCollector : IFilterWheelConsumer, IDisposable
{
    private const string SourceName = "nina.filter_wheel";
    private const string UnknownFilterWheelName = "Unknown";

    private readonly object syncRoot = new();
    private readonly IFilterWheelMediator mediator;
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private bool disposed;
    private string? lastFilterName;
    private string? lastFilterWheelName;
    private bool startAttempted;
    private bool registered;

    public FilterWheelTelemetryCollector(
        IFilterWheelMediator mediator,
        ITelemetrySink sink,
        TimeProvider timeProvider)
    {
        this.mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed || startAttempted)
            {
                return;
            }

            startAttempted = true;
            try
            {
                mediator.RegisterConsumer(this);
                registered = true;
            }
            catch (Exception ex)
            {
                PublishRegistrationFailure(ex);
            }
        }
    }

    public void UpdateDeviceInfo(FilterWheelInfo deviceInfo)
    {
        if (deviceInfo is null)
        {
            return;
        }

        lock (syncRoot)
        {
            if (!TryGetAvailableFilterState(deviceInfo, out var filterWheelName, out var filterName, out var position))
            {
                ClearPreviousMetric();
                return;
            }

            if (HasPublishedMetric() &&
                (!string.Equals(lastFilterWheelName, filterWheelName, StringComparison.Ordinal) ||
                    !string.Equals(lastFilterName, filterName, StringComparison.Ordinal)))
            {
                ClearPreviousMetric();
            }

            PublishCurrentMetric(filterWheelName, filterName, position);
            lastFilterWheelName = filterWheelName;
            lastFilterName = filterName;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!registered)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
            }
            catch
            {
                // Telemetry teardown must never interfere with NINA shutdown.
            }
        }
    }

    private bool TryGetAvailableFilterState(
        FilterWheelInfo deviceInfo,
        out string filterWheelName,
        out string filterName,
        out short position)
    {
        filterWheelName = string.Empty;
        filterName = string.Empty;
        position = default;

        if (!deviceInfo.Connected || deviceInfo.IsMoving || deviceInfo.SelectedFilter is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceInfo.SelectedFilter.Name))
        {
            return false;
        }

        filterWheelName = NormalizeFilterWheelName(deviceInfo.Name);
        filterName = deviceInfo.SelectedFilter.Name;
        position = deviceInfo.SelectedFilter.Position;
        return true;
    }

    private void PublishCurrentMetric(string filterWheelName, string filterName, short position)
    {
        TryPublishSafely(TelemetryRecord.Metric(
            timeProvider.GetUtcNow(),
            SourceName,
            "fwheel_filter",
            position,
            TelemetryPriority.Normal,
            CreateFilterWheelAttributes(filterWheelName, filterName)));
    }

    private void ClearPreviousMetric()
    {
        if (!HasPublishedMetric())
        {
            return;
        }

        TryPublishSafely(TelemetryRecord.Metric(
            timeProvider.GetUtcNow(),
            SourceName,
            "fwheel_filter",
            double.NaN,
            TelemetryPriority.Normal,
            CreateFilterWheelAttributes(lastFilterWheelName!, lastFilterName!)));

        lastFilterWheelName = null;
        lastFilterName = null;
    }

    private bool HasPublishedMetric() =>
        lastFilterWheelName is not null && lastFilterName is not null;

    private void PublishRegistrationFailure(Exception ex)
    {
        TryPublishSafely(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            SourceName,
            "filter_wheel_collector.registration_failed",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
                ["error_message"] = ex.Message,
            }));
    }

    private void TryPublishSafely(TelemetryRecord record)
    {
        try
        {
            sink.TryPublish(record);
        }
        catch
        {
            // NINA equipment callbacks must never fail because telemetry is unavailable.
        }
    }

    private static Dictionary<string, object?> CreateFilterWheelAttributes(
        string filterWheelName,
        string filterName) =>
        new()
        {
            ["filter_wheel_name"] = filterWheelName,
            ["filter_name"] = filterName,
        };

    private static string NormalizeFilterWheelName(string? filterWheelName) =>
        string.IsNullOrWhiteSpace(filterWheelName)
            ? UnknownFilterWheelName
            : filterWheelName;
}
