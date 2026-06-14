using NinaOtel.Abstractions.Telemetry;
using System.Collections.ObjectModel;

namespace NinaOtel.Abstractions.Addons;

public sealed record AddonMetadata(
    string Id,
    string DisplayName,
    Version Version,
    string SourceType,
    int SupportedConfigVersion = 1);

public sealed record AddonConfiguration
{
    private static readonly IReadOnlyDictionary<string, string> EmptySettings =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private IReadOnlyDictionary<string, string> _settings = EmptySettings;

    public AddonConfiguration()
    {
    }

    public AddonConfiguration(
        int configVersion = 1,
        bool rawForwardingEnabled = false,
        IReadOnlyDictionary<string, string>? settings = null,
        bool enabled = true)
    {
        ConfigVersion = configVersion;
        RawForwardingEnabled = rawForwardingEnabled;
        Settings = settings ?? EmptySettings;
        Enabled = enabled;
    }

    public int ConfigVersion { get; init; } = 1;
    public bool RawForwardingEnabled { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string> Settings
    {
        get => _settings;
        init => _settings = SnapshotSettings(value);
    }

    public static AddonConfiguration Default { get; } = new();

    private static IReadOnlyDictionary<string, string> SnapshotSettings(
        IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return EmptySettings;
        }

        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(settings));
    }
}

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
    AddonConfiguration Configuration { get; }
    TimeProvider TimeProvider { get; }
    CancellationToken ShutdownToken { get; }
    void ReportHealth(string addonId, string status, string message, TelemetryPriority priority);
}

public interface ITelemetryAddon
{
    AddonMetadata Metadata { get; }
    AddonValidationResult Validate(AddonConfiguration configuration);
    Task StartAsync(IAddonContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
