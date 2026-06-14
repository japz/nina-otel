using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Abstractions.Addons;

public sealed record AddonMetadata(
    string Id,
    string DisplayName,
    Version Version,
    string SourceType);

public sealed record AddonValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static AddonValidationResult Success { get; } = new(true, Array.Empty<string>());

    public static AddonValidationResult Failure(params string[] errors)
        => new(false, errors);
}

public interface IAddonContext
{
    ITelemetrySink Sink { get; }
    TimeProvider TimeProvider { get; }
    CancellationToken ShutdownToken { get; }
    void ReportHealth(string addonId, string status, string message, TelemetryPriority priority);
}

public interface ITelemetryAddon
{
    AddonMetadata Metadata { get; }
    AddonValidationResult Validate();
    Task StartAsync(IAddonContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
