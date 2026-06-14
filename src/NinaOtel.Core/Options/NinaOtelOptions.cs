namespace NinaOtel.Core.Options;

public enum OtlpProtocol
{
    Grpc,
    HttpProtobuf,
}

public sealed record NinaOtelOptions
{
    public OtlpOptions Otlp { get; init; } = new();
    public BufferOptions Buffer { get; init; } = new();
    public CoreTelemetryOptions CoreTelemetry { get; init; } = new();
    public IReadOnlyDictionary<string, AddonOptions> Addons { get; init; } =
        new Dictionary<string, AddonOptions>();

    public static NinaOtelOptions CreateDefault() => new();
}

public sealed record OtlpOptions
{
    public Uri Endpoint { get; init; } = new("http://localhost:4317");
    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.Grpc;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();
    public OtlpAuthOptions Auth { get; init; } = new();
}

public sealed record OtlpAuthOptions
{
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
    public bool FilteredLogsEnabled { get; init; } = true;
    public bool RawForwardingEnabled { get; init; } = false;
}

public sealed record AddonOptions
{
    public bool Enabled { get; init; } = false;
    public bool RawForwardingEnabled { get; init; } = false;
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>();
}
