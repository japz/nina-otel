using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Addons.PHD2;

public sealed class Phd2TelemetryAddon : ITelemetryAddon
{
    public AddonMetadata Metadata { get; } = new(
        "phd2",
        "PHD2",
        new Version(0, 1, 0),
        "PHD2");

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
