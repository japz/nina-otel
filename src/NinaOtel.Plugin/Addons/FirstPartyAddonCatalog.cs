using NinaOtel.Abstractions.Addons;
using NinaOtel.Addons.NightSummary;
using NinaOtel.Addons.OnStepX;
using NinaOtel.Addons.PHD2;
using NinaOtel.Addons.TargetScheduler;

namespace NinaOtel.Plugin.Addons;

public static class FirstPartyAddonCatalog
{
    private static readonly IReadOnlyList<FirstPartyAddonDescriptor> Catalog =
    [
        new("phd2", "PHD2", "PHD2", static () => new Phd2TelemetryAddon()),
        new("target-scheduler", "Target Scheduler", "Target Scheduler", static () => new TargetSchedulerTelemetryAddon()),
        new("night-summary", "Night Summary", "Night Summary", static () => new NightSummaryTelemetryAddon()),
        new("onstepx", "OnStepX", "OnStepX", static () => new OnStepXTelemetryAddon()),
    ];

    public static IReadOnlyList<FirstPartyAddonDescriptor> Descriptors => Catalog;

    public static IReadOnlyList<ITelemetryAddon> CreateAll()
        => Catalog.Select(static descriptor => descriptor.Create()).ToArray();
}

public sealed record FirstPartyAddonDescriptor(
    string Id,
    string DisplayName,
    string Source,
    Func<ITelemetryAddon> Factory)
{
    public ITelemetryAddon Create() => Factory();
}
