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
    private readonly IReadOnlyList<string> _errors =
        SnapshotErrors(Errors);

    public IReadOnlyList<string> Errors
    {
        get => _errors;
        init => _errors = SnapshotErrors(value);
    }

    private static readonly IReadOnlyList<string> EmptyErrors =
        Array.AsReadOnly(Array.Empty<string>());

    public static AddonValidationResult Success { get; } = new(true, EmptyErrors);

    public static AddonValidationResult Failure(params string[] errors)
        => new(false, errors);

    private static IReadOnlyList<string> SnapshotErrors(IReadOnlyList<string>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return EmptyErrors;
        }

        var snapshot = new string[errors.Count];

        for (var index = 0; index < errors.Count; index++)
        {
            snapshot[index] = errors[index];
        }

        return Array.AsReadOnly(snapshot);
    }
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
