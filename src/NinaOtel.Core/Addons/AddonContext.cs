using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Addons;

public sealed class AddonContext : IAddonContext
{
    public AddonContext(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        CancellationToken shutdownToken)
    {
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ShutdownToken = shutdownToken;
    }

    public ITelemetrySink Sink { get; }
    public TimeProvider TimeProvider { get; }
    public CancellationToken ShutdownToken { get; }

    public void ReportHealth(string addonId, string status, string message, TelemetryPriority priority)
    {
        Sink.TryPublish(TelemetryRecord.Health(
            TimeProvider.GetUtcNow(),
            $"addon.{addonId}",
            "ninaotel.addon.health",
            priority,
            new Dictionary<string, object?>
            {
                ["addon.id"] = addonId,
                ["status"] = status,
                ["message"] = message,
            }));
    }
}
