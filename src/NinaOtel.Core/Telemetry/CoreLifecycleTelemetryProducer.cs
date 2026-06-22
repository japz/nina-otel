using System.Reflection;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Telemetry;

public sealed class CoreLifecycleTelemetryProducer
{
    private const string SourceName = "ninaotel.core";
    private const string SessionSpanName = "nina.session";
    private const string SessionSpanId = "nina.session";

    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly string? serviceVersion;
    private NinaOtelOptions options;
    private bool sessionStarted;

    public CoreLifecycleTelemetryProducer(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        NinaOtelOptions options)
        : this(sink, timeProvider, options, ResolveServiceVersion())
    {
    }

    public CoreLifecycleTelemetryProducer(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        NinaOtelOptions options,
        string? serviceVersion)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.serviceVersion = string.IsNullOrWhiteSpace(serviceVersion)
            ? null
            : serviceVersion.Trim();
    }

    public void PluginInitialized()
    {
        PublishLifecycle("initialized", TelemetryPriority.Important);
        if (!sessionStarted)
        {
            sessionStarted = true;
            PublishSessionSpan(SpanEventKind.Start);
        }
    }

    public void PluginStopping() => PublishLifecycle("stopping", TelemetryPriority.Important);

    public void PluginStopped()
    {
        PublishLifecycle("stopped", TelemetryPriority.Important);
        if (sessionStarted)
        {
            sessionStarted = false;
            PublishSessionSpan(SpanEventKind.Stop);
        }
    }

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
            SourceName,
            "ninaotel.plugin.lifecycle",
            priority,
            CreateSafeAttributes(status)));
    }

    private void PublishSessionSpan(SpanEventKind kind)
    {
        sink.TryPublish(TelemetryRecord.Span(
            timeProvider.GetUtcNow(),
            SourceName,
            SessionSpanName,
            kind,
            SessionSpanId,
            TelemetryPriority.Important,
            CreateSessionAttributes()));
    }

    private Dictionary<string, object?> CreateSafeAttributes(string status) =>
        new()
        {
            ["status"] = status,
            ["otlp.protocol"] = options.Otlp.Protocol.ToString(),
            ["otlp.endpoint.configured"] = options.Otlp.Endpoint.IsAbsoluteUri,
            ["buffer.disk_on_failure.enabled"] = options.Buffer.DiskOnFailureEnabled,
        };

    private Dictionary<string, object?> CreateSessionAttributes()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["service.name"] = "NinaOtel",
            ["ninaotel.component"] = "core",
        };

        if (!string.IsNullOrWhiteSpace(serviceVersion))
        {
            attributes["service.version"] = serviceVersion;
        }

        return attributes;
    }

    private static string? ResolveServiceVersion()
    {
        var assembly = typeof(CoreLifecycleTelemetryProducer).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString();
    }
}
