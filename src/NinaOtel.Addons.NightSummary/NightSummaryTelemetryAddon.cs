using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Addons.NightSummary;

public sealed class NightSummaryTelemetryAddon : ITelemetryAddon
{
    public AddonMetadata Metadata { get; } = new(
        "night-summary",
        "Night Summary",
        new Version(0, 1, 0),
        "Night Summary");

    public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

    public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.ReportHealth(
            Metadata.Id,
            "waiting",
            "Add-on shell loaded; source collection is not implemented yet.",
            TelemetryPriority.Routine);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
