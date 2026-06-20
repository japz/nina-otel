using NINA.Core.Model.Equipment;
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
    private string? lastConnectedFilterWheelName;
    private string? lastDisconnectedFilterWheelName;
    private bool startAttempted;
    private bool startupFailed;
    private bool registered;
    private bool shouldUnsubscribeConnected;
    private bool shouldUnsubscribeDisconnected;
    private bool shouldUnsubscribeFilterChanged;
    private bool lifecycleEventsEnabled;
    private bool filterChangedEventsEnabled;
    private bool disconnectedEventLogged;
    private long filterChangeSequence;

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

                shouldUnsubscribeFilterChanged = true;
                mediator.FilterChanged += OnFilterChanged;

                shouldUnsubscribeConnected = true;
                mediator.Connected += OnConnected;

                shouldUnsubscribeDisconnected = true;
                mediator.Disconnected += OnDisconnected;

                lifecycleEventsEnabled = true;
                filterChangedEventsEnabled = true;
            }
            catch (Exception ex)
            {
                startupFailed = true;
                lifecycleEventsEnabled = false;
                filterChangedEventsEnabled = false;
                CleanupFailedStart();
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
            if (disposed || startupFailed)
            {
                return;
            }

            if (!TryGetAvailableFilterState(deviceInfo, out var filterWheelName, out var filterName, out var position))
            {
                if (lastConnectedFilterWheelName is not null)
                {
                    lastDisconnectedFilterWheelName = lastConnectedFilterWheelName;
                }

                ClearPreviousMetric();
                ResetPublishedState();
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
            lastConnectedFilterWheelName = filterWheelName;
            lastDisconnectedFilterWheelName = null;
            disconnectedEventLogged = false;
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
            lifecycleEventsEnabled = false;
            filterChangedEventsEnabled = false;
            CleanupSubscriptions();

            if (!registered)
            {
                return;
            }

            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
            }
            catch
            {
                // Telemetry teardown must never interfere with NINA shutdown.
            }
        }
    }

    private Task OnConnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || startupFailed || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var deviceInfo = TryGetInfo();
                var filterWheelName = deviceInfo is { Connected: true }
                    ? NormalizeFilterWheelName(deviceInfo.Name)
                    : ResolveCurrentFilterWheelName();

                disconnectedEventLogged = false;
                lastDisconnectedFilterWheelName = null;
                if (deviceInfo is { Connected: true })
                {
                    if (lastConnectedFilterWheelName is not null &&
                        !string.Equals(lastConnectedFilterWheelName, filterWheelName, StringComparison.Ordinal))
                    {
                        ClearPreviousMetric();
                    }

                    lastConnectedFilterWheelName = filterWheelName;
                }

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "fwheel_connected",
                    "Filter Wheel connected",
                    CreateLifecycleAttributes(filterWheelName));
            }
        }
        catch
        {
            // NINA filter wheel lifecycle events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnected(object sender, EventArgs e)
    {
        try
        {
            lock (syncRoot)
            {
                if (disposed || startupFailed || disconnectedEventLogged || !lifecycleEventsEnabled)
                {
                    return Task.CompletedTask;
                }

                var filterWheelName = ResolveDisconnectedFilterWheelName();

                PublishNamedLog(
                    timeProvider.GetUtcNow(),
                    "fwheel_disconnected",
                    "Filter Wheel disconnected",
                    CreateLifecycleAttributes(filterWheelName));

                ClearPreviousMetric();
                ResetPublishedState();
                lastDisconnectedFilterWheelName = filterWheelName;
                disconnectedEventLogged = true;
            }
        }
        catch
        {
            // NINA filter wheel lifecycle events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
    }

    private Task OnFilterChanged(object sender, FilterChangedEventArgs args)
    {
        try
        {
            if (args is null)
            {
                return Task.CompletedTask;
            }

            var record = CreateFilterChangeRecord(args);
            if (record is not null)
            {
                TryPublishSafely(record);
                TryPublishSafely(CreateFilterChangeLogRecord(record));
            }
        }
        catch
        {
            // NINA filter change events must never fail because telemetry is unavailable.
        }

        return Task.CompletedTask;
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

    private TelemetryRecord? CreateFilterChangeRecord(FilterChangedEventArgs args)
    {
        lock (syncRoot)
        {
            if (disposed || !filterChangedEventsEnabled)
            {
                return null;
            }

            var timestamp = timeProvider.GetUtcNow();
            var sequence = ++filterChangeSequence;
            var attributes = CreateFilterChangeAttributes(
                ResolveCurrentFilterWheelName(),
                args.From,
                args.To);

            return TelemetryRecord.Span(
                timestamp,
                SourceName,
                "nina.filter_change",
                SpanEventKind.Stop,
                CreateFilterChangeSpanId(timestamp, sequence, attributes),
                TelemetryPriority.Normal,
                attributes);
        }
    }

    private static TelemetryRecord CreateFilterChangeLogRecord(TelemetryRecord spanRecord)
    {
        var filterFrom = AttributeString(spanRecord.Attributes, "filter_from");
        var filterTo = AttributeString(spanRecord.Attributes, "filter_to");
        var text = $"Filter changed from {filterFrom} to {filterTo}";
        var attributes = new Dictionary<string, object?>(spanRecord.Attributes)
        {
            ["title"] = "Filter changed",
            ["text"] = text,
        };

        return new TelemetryRecord(
            TelemetrySignal.Log,
            spanRecord.Timestamp,
            SourceName,
            "filter_change",
            TelemetryPriority.Normal,
            attributes,
            Body: text,
            Severity: TelemetrySeverity.Information);
    }

    private void CleanupSubscriptions()
    {
        TryUnsubscribeDisconnected();
        TryUnsubscribeConnected();
        TryUnsubscribeFilterChanged();
    }

    private void CleanupFailedStart()
    {
        CleanupSubscriptions();

        if (registered)
        {
            try
            {
                mediator.RemoveConsumer(this);
                registered = false;
            }
            catch
            {
                // Startup cleanup must never interfere with NINA.
            }
        }

        ClearPreviousMetric();
        ResetPublishedState();
        lastDisconnectedFilterWheelName = null;
        disconnectedEventLogged = false;
    }

    private void TryUnsubscribeDisconnected()
    {
        if (!shouldUnsubscribeDisconnected)
        {
            return;
        }

        try
        {
            mediator.Disconnected -= OnDisconnected;
            shouldUnsubscribeDisconnected = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private void TryUnsubscribeConnected()
    {
        if (!shouldUnsubscribeConnected)
        {
            return;
        }

        try
        {
            mediator.Connected -= OnConnected;
            shouldUnsubscribeConnected = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private void TryUnsubscribeFilterChanged()
    {
        if (!shouldUnsubscribeFilterChanged)
        {
            return;
        }

        try
        {
            mediator.FilterChanged -= OnFilterChanged;
            shouldUnsubscribeFilterChanged = false;
        }
        catch
        {
            // Telemetry teardown must never interfere with NINA shutdown.
        }
    }

    private string ResolveCurrentFilterWheelName() =>
        lastConnectedFilterWheelName ??
        lastFilterWheelName ??
        ResolveMediatorFilterWheelName();

    private string ResolveDisconnectedFilterWheelName() =>
        lastConnectedFilterWheelName ??
        lastDisconnectedFilterWheelName ??
        lastFilterWheelName ??
        ResolveMediatorFilterWheelName();

    private string ResolveMediatorFilterWheelName()
    {
        return NormalizeFilterWheelName(TryGetInfo()?.Name);
    }

    private FilterWheelInfo? TryGetInfo()
    {
        try
        {
            return mediator.GetInfo();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> CreateLifecycleAttributes(string filterWheelName) =>
        new()
        {
            ["filter_wheel_name"] = filterWheelName,
        };

    private static Dictionary<string, object?> CreateFilterChangeAttributes(
        string filterWheelName,
        FilterInfo? from,
        FilterInfo? to) =>
        new()
        {
            ["filter_wheel_name"] = filterWheelName,
            ["filter_from"] = NormalizeFilterName(from?.Name),
            ["filter_to"] = NormalizeFilterName(to?.Name),
            ["filter_from_position"] = NormalizeFilterPosition(from),
            ["filter_to_position"] = NormalizeFilterPosition(to),
        };

    private static string CreateFilterChangeSpanId(
        DateTimeOffset timestamp,
        long sequence,
        IReadOnlyDictionary<string, object?> attributes) =>
        string.Join(
            "|",
            "filter_change",
            timestamp.ToUniversalTime().ToString("O"),
            sequence,
            attributes["filter_wheel_name"],
            attributes["filter_from"],
            attributes["filter_to"],
            attributes["filter_from_position"],
            attributes["filter_to_position"]);

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

    private void PublishNamedLog(
        DateTimeOffset timestamp,
        string name,
        string body,
        IReadOnlyDictionary<string, object?> attributes) =>
        TryPublishSafely(new TelemetryRecord(
            TelemetrySignal.Log,
            timestamp,
            SourceName,
            name,
            TelemetryPriority.Normal,
            attributes,
            Body: body,
            Severity: TelemetrySeverity.Information));

    private void ResetPublishedState()
    {
        lastFilterWheelName = null;
        lastFilterName = null;
        lastConnectedFilterWheelName = null;
    }

    private static Dictionary<string, object?> CreateFilterWheelAttributes(
        string filterWheelName,
        string filterName) =>
        new()
        {
            ["filter_wheel_name"] = filterWheelName,
            ["filter_name"] = filterName,
        };

    private static string NormalizeFilterName(string? filterName) =>
        string.IsNullOrWhiteSpace(filterName)
            ? "Unknown"
            : filterName;

    private static string AttributeString(
        IReadOnlyDictionary<string, object?> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value)
            ? NormalizeFilterName(value?.ToString())
            : NormalizeFilterName(null);

    private static int? NormalizeFilterPosition(FilterInfo? filter) =>
        filter is null ? null : filter.Position;

    private static string NormalizeFilterWheelName(string? filterWheelName) =>
        string.IsNullOrWhiteSpace(filterWheelName)
            ? UnknownFilterWheelName
            : filterWheelName;
}
