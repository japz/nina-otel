using System.Collections.ObjectModel;

namespace NinaOtel.Core.Options;

public enum OtlpProtocol
{
    Grpc,
    HttpProtobuf,
}

public enum OtlpAuthenticationMode
{
    None,
    BearerToken,
    Basic,
}

public sealed record NinaOtelOptions
{
    private IReadOnlyDictionary<string, AddonOptions> _addons =
        OptionDictionary.Snapshot<string, AddonOptions>(null);

    public OtlpOptions Otlp { get; init; } = new();
    public BufferOptions Buffer { get; init; } = new();
    public CoreTelemetryOptions CoreTelemetry { get; init; } = new();
    public IReadOnlyDictionary<string, AddonOptions> Addons
    {
        get => _addons;
        init => _addons = OptionDictionary.Snapshot(value);
    }

    public static NinaOtelOptions CreateDefault() => new();
}

public sealed record OtlpOptions
{
    private IReadOnlyDictionary<string, string> _headers =
        OptionDictionary.Snapshot<string, string>(null);

    public Uri Endpoint { get; init; } = new("http://localhost:4317");
    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.Grpc;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> Headers
    {
        get => _headers;
        init => _headers = OptionDictionary.Snapshot(value);
    }
    public OtlpAuthOptions Auth { get; init; } = new();
}

public sealed class OtlpAuthOptions
{
    public OtlpAuthenticationMode Mode { get; init; } = OtlpAuthenticationMode.None;
    public string? BearerToken { get; init; }
    public string? BearerTokenFile { get; init; }
    public string? BasicUsername { get; init; }
    public string? BasicPasswordProtected { get; init; }
    public string? CaCertificatePemPath { get; init; }
    public string? ClientCertificatePemPath { get; init; }
    public string? ClientPrivateKeyPemPath { get; init; }
    public string? ClientCertificatePfxPath { get; init; }
    public string? ClientCertificatePfxPasswordProtected { get; init; }
    public string? WindowsCertificateFingerprint { get; init; }

    public override string ToString() =>
        $"{nameof(OtlpAuthOptions)} {{ " +
        $"{nameof(Mode)} = {Mode}, " +
        $"{FormatConfigured(nameof(BearerToken), BearerToken)}, " +
        $"{nameof(BearerTokenFile)} = {FormatPath(BearerTokenFile)}, " +
        $"{FormatConfigured(nameof(BasicUsername), BasicUsername)}, " +
        $"{FormatConfigured("BasicPassword", BasicPasswordProtected)}, " +
        $"{nameof(CaCertificatePemPath)} = {FormatPath(CaCertificatePemPath)}, " +
        $"{nameof(ClientCertificatePemPath)} = {FormatPath(ClientCertificatePemPath)}, " +
        $"{nameof(ClientPrivateKeyPemPath)} = {FormatPath(ClientPrivateKeyPemPath)}, " +
        $"{nameof(ClientCertificatePfxPath)} = {FormatPath(ClientCertificatePfxPath)}, " +
        $"{FormatConfigured("ClientCertificatePfxPassword", ClientCertificatePfxPasswordProtected)}, " +
        $"{FormatConfigured(nameof(WindowsCertificateFingerprint), WindowsCertificateFingerprint)} " +
        "}";

    private static bool IsConfigured(string? value) => !string.IsNullOrEmpty(value);

    private static string FormatConfigured(string name, string? value) =>
        $"{name}Configured = {IsConfigured(value)}";

    private static string FormatPath(string? value) =>
        string.IsNullOrEmpty(value) ? "<not configured>" : value;
}

public sealed record BufferOptions
{
    public int MemoryQueueCapacity { get; init; } = 10_000;
    public bool DiskOnFailureEnabled { get; init; } = true;
    public bool SpoolsDuringHealthyExport { get; init; } = false;
    public string SpoolPath { get; init; } = "%LOCALAPPDATA%\\NINA\\NinaOtel\\spool";
    public long MaxSpoolBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public TimeSpan MaxSpoolAge { get; init; } = TimeSpan.FromDays(7);
    public int RecoveryFlushRecordsPerSecond { get; init; } = 500;
}

public sealed record CoreTelemetryOptions
{
    public bool EquipmentEnabled { get; init; } = true;
    public bool ImageStatsEnabled { get; init; } = true;
    public bool WorkflowTracesEnabled { get; init; } = true;
    public string NinaLogPath { get; init; } = string.Empty;
    public bool FilteredLogsEnabled { get; init; } = true;
    public bool RawForwardingEnabled { get; init; } = false;
}

public sealed record AddonOptions
{
    private IReadOnlyDictionary<string, string> _settings =
        OptionDictionary.Snapshot<string, string>(null);

    public bool Enabled { get; init; } = false;
    public bool RawForwardingEnabled { get; init; } = false;
    public IReadOnlyDictionary<string, string> Settings
    {
        get => _settings;
        init => _settings = OptionDictionary.Snapshot(value);
    }
}

internal static class OptionDictionary
{
    public static IReadOnlyDictionary<TKey, TValue> Snapshot<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? source)
        where TKey : notnull
    {
        var copy = source is null
            ? new Dictionary<TKey, TValue>()
            : new Dictionary<TKey, TValue>(source);

        return new ReadOnlyDictionary<TKey, TValue>(copy);
    }
}
