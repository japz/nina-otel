using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Addons.OnStepX;

public sealed class OnStepXTelemetryAddon : ITelemetryAddon
{
    public AddonMetadata Metadata { get; } = new(
        "onstepx",
        "OnStepX",
        new Version(0, 1, 0),
        "OnStepX");

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
