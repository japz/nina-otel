using NinaOtel.Core.Options;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NinaOtel.Plugin.Options;

public sealed class NinaOtelOptionsViewModel : INotifyPropertyChanged
{
    private const string CollectorEndpointKey = nameof(CollectorEndpoint);
    private const string CollectorProtocolKey = nameof(CollectorProtocol);
    private const string DiskOnFailureEnabledKey = nameof(DiskOnFailureEnabled);

    private readonly INinaOtelSettingsStore settingsStore;
    private readonly NinaOtelOptions defaults = NinaOtelOptions.CreateDefault();
    private string collectorEndpoint = string.Empty;
    private string appliedCollectorEndpoint = string.Empty;
    private OtlpProtocol collectorProtocol;
    private bool diskOnFailureEnabled;
    private string status = "NinaOtel foundation loaded";

    public NinaOtelOptionsViewModel(INinaOtelSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        LoadFromSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<OtlpProtocol> AvailableProtocols { get; } = Enum.GetValues<OtlpProtocol>();

    public NinaOtelOptions Options => CreateOptions();

    public string CollectorEndpoint
    {
        get => collectorEndpoint;
        set
        {
            var normalized = NormalizeEndpoint(value);
            if (normalized is null)
            {
                SetField(ref collectorEndpoint, value, saveOptions: false);
                Status = "Collector endpoint must be an absolute URI.";
                return;
            }

            if (SetField(ref collectorEndpoint, normalized, saveOptions: false))
            {
                appliedCollectorEndpoint = normalized;
                settingsStore.SetString(CollectorEndpointKey, normalized);
                RaisePropertyChanged(nameof(Options));
                Status = "Settings saved";
            }
        }
    }

    public OtlpProtocol CollectorProtocol
    {
        get => collectorProtocol;
        set
        {
            if (SetField(ref collectorProtocol, value))
            {
                settingsStore.SetString(CollectorProtocolKey, value.ToString());
                Status = "Settings saved";
            }
        }
    }

    public bool DiskOnFailureEnabled
    {
        get => diskOnFailureEnabled;
        set
        {
            if (SetField(ref diskOnFailureEnabled, value))
            {
                settingsStore.SetBoolean(DiskOnFailureEnabledKey, value);
                Status = "Settings saved";
            }
        }
    }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value, saveOptions: false);
    }

    public void Reload()
    {
        LoadFromSettings();
        Status = "Settings loaded";
    }

    private void LoadFromSettings()
    {
        collectorEndpoint = LoadEndpoint();
        appliedCollectorEndpoint = collectorEndpoint;
        collectorProtocol = LoadProtocol();
        diskOnFailureEnabled = settingsStore.GetBoolean(
            DiskOnFailureEnabledKey,
            defaults.Buffer.DiskOnFailureEnabled);

        RaisePropertyChanged(nameof(CollectorEndpoint));
        RaisePropertyChanged(nameof(CollectorProtocol));
        RaisePropertyChanged(nameof(DiskOnFailureEnabled));
        RaisePropertyChanged(nameof(Options));
    }

    private string LoadEndpoint()
    {
        var configured = settingsStore.GetString(
            CollectorEndpointKey,
            defaults.Otlp.Endpoint.ToString());

        return NormalizeEndpoint(configured) ?? defaults.Otlp.Endpoint.ToString();
    }

    private OtlpProtocol LoadProtocol()
    {
        var configured = settingsStore.GetString(
            CollectorProtocolKey,
            defaults.Otlp.Protocol.ToString());

        return Enum.TryParse<OtlpProtocol>(configured, ignoreCase: true, out var protocol)
            ? protocol
            : defaults.Otlp.Protocol;
    }

    private NinaOtelOptions CreateOptions()
    {
        return defaults with
        {
            Otlp = defaults.Otlp with
            {
                Endpoint = new Uri(appliedCollectorEndpoint),
                Protocol = collectorProtocol,
            },
            Buffer = defaults.Buffer with
            {
                DiskOnFailureEnabled = diskOnFailureEnabled,
            },
        };
    }

    private bool SetField<T>(
        ref T field,
        T value,
        bool saveOptions = true,
        [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        if (saveOptions)
        {
            RaisePropertyChanged(nameof(Options));
        }

        return true;
    }

    private void RaisePropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string? NormalizeEndpoint(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            return endpoint.ToString();
        }

        return null;
    }
}
