using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Telemetry;

public sealed class CoreLifecycleTelemetryProducer
{
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private NinaOtelOptions options;

    public CoreLifecycleTelemetryProducer(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        NinaOtelOptions options)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void PluginInitialized() => PublishLifecycle("initialized", TelemetryPriority.Important);

    public void PluginStopping() => PublishLifecycle("stopping", TelemetryPriority.Important);

    public void PluginStopped() => PublishLifecycle("stopped", TelemetryPriority.Important);

    public void ProfileChanged(NinaOtelOptions updatedOptions)
    {
        ArgumentNullException.ThrowIfNull(updatedOptions);

        options = updatedOptions;
        PublishLifecycle("settings_loaded", TelemetryPriority.Normal);
    }

    private void PublishLifecycle(string status, TelemetryPriority priority)
    {
        sink.TryPublish(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            "ninaotel.core",
            "ninaotel.plugin.lifecycle",
            priority,
            CreateSafeAttributes(status)));
    }

    private Dictionary<string, object?> CreateSafeAttributes(string status) =>
        new()
        {
            ["status"] = status,
            ["otlp.protocol"] = options.Otlp.Protocol.ToString(),
            ["otlp.endpoint.configured"] = options.Otlp.Endpoint.IsAbsoluteUri,
            ["buffer.disk_on_failure.enabled"] = options.Buffer.DiskOnFailureEnabled,
        };
}
