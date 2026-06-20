using NinaOtel.Core.Health;
using NinaOtel.Core.Options;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace NinaOtel.Plugin.Options;

public sealed class NinaOtelOptionsViewModel : INotifyPropertyChanged
{
    private const string CollectorEndpointKey = nameof(CollectorEndpoint);
    private const string CollectorProtocolKey = nameof(CollectorProtocol);
    private const string DiskOnFailureEnabledKey = nameof(DiskOnFailureEnabled);
    private const string SpoolPathKey = nameof(SpoolPath);
    private const string MaxSpoolSizeGbKey = nameof(MaxSpoolSizeGb);
    private const string MaxSpoolAgeDaysKey = nameof(MaxSpoolAgeDays);
    private const string StaticHeadersKey = nameof(StaticHeaders);
    private const string AuthenticationModeKey = nameof(AuthenticationMode);
    private const string BearerTokenProtectedKey = "BearerTokenProtected";
    private const string BasicUsernameKey = nameof(BasicUsername);
    private const string BasicPasswordProtectedKey = "BasicPasswordProtected";
    private const decimal BytesPerGb = 1024m * 1024m * 1024m;
    private const decimal TicksPerDay = TimeSpan.TicksPerDay;
    private const decimal MaxSpoolSizeGbValue = long.MaxValue / BytesPerGb;
    private static readonly decimal MaxSpoolAgeDaysValue = TimeSpan.MaxValue.Ticks / TicksPerDay;

    private readonly INinaOtelSettingsStore settingsStore;
    private readonly ISecretProtector secretProtector;
    private readonly NinaOtelOptions defaults = NinaOtelOptions.CreateDefault();
    private readonly SynchronizationContext? synchronizationContext = SynchronizationContext.Current;
    private string collectorEndpoint = string.Empty;
    private string appliedCollectorEndpoint = string.Empty;
    private OtlpProtocol collectorProtocol;
    private bool diskOnFailureEnabled;
    private string spoolPath = string.Empty;
    private string appliedSpoolPath = string.Empty;
    private string maxSpoolSizeGb = string.Empty;
    private long appliedMaxSpoolBytes;
    private string maxSpoolAgeDays = string.Empty;
    private TimeSpan appliedMaxSpoolAge;
    private string staticHeaders = string.Empty;
    private IReadOnlyDictionary<string, string> appliedStaticHeaders = new Dictionary<string, string>();
    private OtlpAuthenticationMode authenticationMode;
    private string bearerToken = string.Empty;
    private string bearerTokenProtected = string.Empty;
    private string basicUsername = string.Empty;
    private string basicPassword = string.Empty;
    private string basicPasswordProtected = string.Empty;
    private string status = "NinaOtel foundation loaded";
    private CollectorHealthState collectorHealthState = CollectorHealthState.Unknown;
    private CollectorHealthSnapshot? collectorHealthSnapshot;

    public NinaOtelOptionsViewModel(INinaOtelSettingsStore settingsStore)
        : this(settingsStore, DpapiSecretProtector.Instance)
    {
    }

    internal NinaOtelOptionsViewModel(INinaOtelSettingsStore settingsStore, ISecretProtector secretProtector)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        LoadFromSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<OtlpProtocol> AvailableProtocols { get; } = Enum.GetValues<OtlpProtocol>();

    public IReadOnlyList<OtlpAuthenticationMode> AvailableAuthenticationModes { get; } =
        Enum.GetValues<OtlpAuthenticationMode>();

    public NinaOtelOptions Options => CreateOptions();

    public CollectorHealthState CollectorHealthState => collectorHealthState;

    public string CollectorHealthBrush => collectorHealthState switch
    {
        CollectorHealthState.Healthy => "#2E7D32",
        CollectorHealthState.Unhealthy => "#C62828",
        _ => "#808080",
    };

    public string CollectorHealthSummary => collectorHealthState switch
    {
        CollectorHealthState.Healthy => "Collector connected",
        CollectorHealthState.Unhealthy => "Collector export failed",
        _ => "Collector not checked yet",
    };

    public string CollectorHealthDebugInfo => collectorHealthSnapshot is null
        ? string.Empty
        : collectorHealthSnapshot.State switch
        {
            CollectorHealthState.Healthy => FormatHealthyDebugInfo(collectorHealthSnapshot),
            CollectorHealthState.Unhealthy => FormatUnhealthyDebugInfo(collectorHealthSnapshot),
            _ => string.Empty,
        };

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

    public string SpoolPath
    {
        get => spoolPath;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SetField(ref spoolPath, value, saveOptions: false);
                Status = "Spool path cannot be empty.";
                return;
            }

            if (SetField(ref spoolPath, value, saveOptions: false))
            {
                appliedSpoolPath = value;
                settingsStore.SetString(SpoolPathKey, value);
                RaisePropertyChanged(nameof(Options));
                Status = "Settings saved";
            }
        }
    }

    public string MaxSpoolSizeGb
    {
        get => maxSpoolSizeGb;
        set
        {
            if (!TryConvertGbToBytes(value, out var maxBytes, out var failure))
            {
                SetField(ref maxSpoolSizeGb, value, saveOptions: false);
                Status = GetMaxSpoolSizeStatus(failure);
                return;
            }

            if (SetField(ref maxSpoolSizeGb, value, saveOptions: false))
            {
                appliedMaxSpoolBytes = maxBytes;
                settingsStore.SetString(MaxSpoolSizeGbKey, value);
                RaisePropertyChanged(nameof(Options));
                Status = "Settings saved";
            }
        }
    }

    public string MaxSpoolAgeDays
    {
        get => maxSpoolAgeDays;
        set
        {
            if (!TryConvertDaysToAge(value, out var maxAge, out var failure))
            {
                SetField(ref maxSpoolAgeDays, value, saveOptions: false);
                Status = GetMaxSpoolAgeStatus(failure);
                return;
            }

            if (SetField(ref maxSpoolAgeDays, value, saveOptions: false))
            {
                appliedMaxSpoolAge = maxAge;
                settingsStore.SetString(MaxSpoolAgeDaysKey, value);
                RaisePropertyChanged(nameof(Options));
                Status = "Settings saved";
            }
        }
    }

    public string StaticHeaders
    {
        get => staticHeaders;
        set
        {
            if (!TryParseHeaders(value, out var headers, out var failure))
            {
                SetField(ref staticHeaders, value, saveOptions: false);
                Status = failure;
                return;
            }

            if (SetField(ref staticHeaders, value, saveOptions: false))
            {
                appliedStaticHeaders = headers;
                settingsStore.SetString(StaticHeadersKey, value);
                RaisePropertyChanged(nameof(Options));
                Status = "Settings saved";
            }
        }
    }

    public OtlpAuthenticationMode AuthenticationMode
    {
        get => authenticationMode;
        set
        {
            if (SetField(ref authenticationMode, value))
            {
                settingsStore.SetString(AuthenticationModeKey, value.ToString());
                Status = "Settings saved";
            }
        }
    }

    public string BasicUsername
    {
        get => basicUsername;
        set
        {
            if (SetField(ref basicUsername, value ?? string.Empty))
            {
                settingsStore.SetString(BasicUsernameKey, basicUsername);
                Status = "Settings saved";
            }
        }
    }

    public string GetBearerToken() => bearerToken;

    public void SetBearerToken(string? token)
    {
        SetProtectedSecret(
            token,
            ref bearerToken,
            ref bearerTokenProtected,
            BearerTokenProtectedKey,
            nameof(GetBearerToken));
    }

    public string GetBasicPassword() => basicPassword;

    public void SetBasicPassword(string? password)
    {
        SetProtectedSecret(
            password,
            ref basicPassword,
            ref basicPasswordProtected,
            BasicPasswordProtectedKey,
            nameof(GetBasicPassword));
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

    public void UpdateCollectorHealth(CollectorHealthSnapshot snapshot)
    {
        try
        {
            var context = synchronizationContext;
            if (context is not null && context != SynchronizationContext.Current)
            {
                context.Post(
                    static state =>
                    {
                        var update = (CollectorHealthUpdate)state!;
                        update.ViewModel.ApplyCollectorHealthSafely(update.Snapshot);
                    },
                    new CollectorHealthUpdate(this, snapshot));
                return;
            }

            ApplyCollectorHealthSafely(snapshot);
        }
        catch
        {
            // Health updates originate from exporter background work and must not break export.
        }
    }

    private void LoadFromSettings()
    {
        collectorEndpoint = LoadEndpoint();
        appliedCollectorEndpoint = collectorEndpoint;
        collectorProtocol = LoadProtocol();
        diskOnFailureEnabled = settingsStore.GetBoolean(
            DiskOnFailureEnabledKey,
            defaults.Buffer.DiskOnFailureEnabled);
        spoolPath = settingsStore.GetString(SpoolPathKey, defaults.Buffer.SpoolPath);
        appliedSpoolPath = string.IsNullOrWhiteSpace(spoolPath) ? defaults.Buffer.SpoolPath : spoolPath;
        maxSpoolSizeGb = settingsStore.GetString(MaxSpoolSizeGbKey, FormatGb(defaults.Buffer.MaxSpoolBytes));
        appliedMaxSpoolBytes = TryConvertGbToBytes(maxSpoolSizeGb, out var bytes, out _)
            ? bytes
            : defaults.Buffer.MaxSpoolBytes;
        maxSpoolAgeDays = settingsStore.GetString(MaxSpoolAgeDaysKey, FormatDays(defaults.Buffer.MaxSpoolAge));
        appliedMaxSpoolAge = TryConvertDaysToAge(maxSpoolAgeDays, out var age, out _)
            ? age
            : defaults.Buffer.MaxSpoolAge;
        staticHeaders = settingsStore.GetString(StaticHeadersKey, string.Empty);
        appliedStaticHeaders = TryParseHeaders(staticHeaders, out var parsedHeaders, out _)
            ? parsedHeaders
            : defaults.Otlp.Headers;
        authenticationMode = LoadAuthenticationMode();
        basicUsername = settingsStore.GetString(BasicUsernameKey, string.Empty);
        bearerTokenProtected = settingsStore.GetString(BearerTokenProtectedKey, string.Empty);
        basicPasswordProtected = settingsStore.GetString(BasicPasswordProtectedKey, string.Empty);
        bearerToken = UnprotectOrWarn(bearerTokenProtected, "Bearer token");
        basicPassword = UnprotectOrWarn(basicPasswordProtected, "Basic password");

        RaisePropertyChanged(nameof(CollectorEndpoint));
        RaisePropertyChanged(nameof(CollectorProtocol));
        RaisePropertyChanged(nameof(DiskOnFailureEnabled));
        RaisePropertyChanged(nameof(SpoolPath));
        RaisePropertyChanged(nameof(MaxSpoolSizeGb));
        RaisePropertyChanged(nameof(MaxSpoolAgeDays));
        RaisePropertyChanged(nameof(StaticHeaders));
        RaisePropertyChanged(nameof(AuthenticationMode));
        RaisePropertyChanged(nameof(BasicUsername));
        RaisePropertyChanged(nameof(AvailableAuthenticationModes));
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

    private OtlpAuthenticationMode LoadAuthenticationMode()
    {
        var configured = settingsStore.GetString(
            AuthenticationModeKey,
            OtlpAuthenticationMode.None.ToString());

        return Enum.TryParse<OtlpAuthenticationMode>(configured, ignoreCase: true, out var mode)
            ? mode
            : OtlpAuthenticationMode.None;
    }

    private NinaOtelOptions CreateOptions()
    {
        return defaults with
        {
            Otlp = defaults.Otlp with
            {
                Endpoint = new Uri(appliedCollectorEndpoint),
                Protocol = collectorProtocol,
                Headers = CreateEffectiveHeaders(),
                Auth = new OtlpAuthOptions
                {
                    Mode = authenticationMode,
                    BearerToken = authenticationMode == OtlpAuthenticationMode.BearerToken &&
                        !string.IsNullOrEmpty(bearerToken)
                            ? bearerToken
                            : null,
                    BasicUsername = authenticationMode == OtlpAuthenticationMode.Basic &&
                        !string.IsNullOrEmpty(basicUsername)
                            ? basicUsername
                            : null,
                    BasicPasswordProtected = authenticationMode == OtlpAuthenticationMode.Basic &&
                        !string.IsNullOrEmpty(basicPasswordProtected)
                            ? basicPasswordProtected
                            : null,
                },
            },
            Buffer = defaults.Buffer with
            {
                DiskOnFailureEnabled = diskOnFailureEnabled,
                SpoolPath = appliedSpoolPath,
                MaxSpoolBytes = appliedMaxSpoolBytes,
                MaxSpoolAge = appliedMaxSpoolAge,
            },
        };
    }

    private IReadOnlyDictionary<string, string> CreateEffectiveHeaders()
    {
        var headers = new Dictionary<string, string>(appliedStaticHeaders, StringComparer.OrdinalIgnoreCase);
        switch (authenticationMode)
        {
            case OtlpAuthenticationMode.BearerToken when !string.IsNullOrEmpty(bearerToken):
                headers["Authorization"] = $"Bearer {bearerToken}";
                break;
            case OtlpAuthenticationMode.Basic
                when !string.IsNullOrEmpty(basicUsername) && !string.IsNullOrEmpty(basicPassword):
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicUsername}:{basicPassword}"));
                headers["Authorization"] = $"Basic {credentials}";
                break;
        }

        return headers;
    }

    private string UnprotectOrWarn(string protectedSecret, string label)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return string.Empty;
        }

        if (secretProtector.TryUnprotect(protectedSecret, out var secret))
        {
            return secret;
        }

        Status = $"{label} could not be decrypted; re-enter it.";
        return string.Empty;
    }

    private void SetProtectedSecret(
        string? value,
        ref string secretField,
        ref string protectedField,
        string settingsKey,
        string propertyName)
    {
        var normalized = value ?? string.Empty;
        if (secretField == normalized &&
            (!string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(protectedField)))
        {
            return;
        }

        secretField = normalized;
        protectedField = string.IsNullOrEmpty(normalized)
            ? string.Empty
            : secretProtector.Protect(normalized);
        settingsStore.SetString(settingsKey, protectedField);
        RaisePropertyChanged(propertyName);
        RaisePropertyChanged(nameof(Options));
        Status = "Settings saved";
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

    private void ApplyCollectorHealthSafely(CollectorHealthSnapshot snapshot)
    {
        try
        {
            collectorHealthSnapshot = snapshot;
            if (collectorHealthState != snapshot.State)
            {
                collectorHealthState = snapshot.State;
                RaisePropertyChanged(nameof(CollectorHealthState));
            }

            RaisePropertyChanged(nameof(CollectorHealthBrush));
            RaisePropertyChanged(nameof(CollectorHealthSummary));
            RaisePropertyChanged(nameof(CollectorHealthDebugInfo));
        }
        catch
        {
            // PropertyChanged subscribers are UI-owned; health reporting should never throw outward.
        }
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

    private static bool TryParseHeaders(
        string? value,
        out IReadOnlyDictionary<string, string> headers,
        out string status)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headers = parsed;
        status = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        using var reader = new StringReader(value);
        for (var lineNumber = 1; ; lineNumber++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var equalsIndex = line.IndexOf('=');
            var separatorIndex = (colonIndex, equalsIndex) switch
            {
                (>= 0, >= 0) => Math.Min(colonIndex, equalsIndex),
                (>= 0, _) => colonIndex,
                (_, >= 0) => equalsIndex,
                _ => -1,
            };

            if (separatorIndex <= 0)
            {
                status = $"Static header line {lineNumber} must use 'Name: value'.";
                return false;
            }

            var name = line[..separatorIndex].Trim();
            var headerValue = line[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                status = $"Static header line {lineNumber} must include a header name.";
                return false;
            }

            if (string.IsNullOrEmpty(headerValue))
            {
                status = $"Static header line {lineNumber} must include a header value.";
                return false;
            }

            parsed[name] = headerValue;
        }

        return true;
    }

    private static bool TryParsePositiveDecimal(string? value, out decimal parsed)
    {
        return decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed) &&
            parsed > 0;
    }

    private static bool TryConvertGbToBytes(
        string? value,
        out long bytes,
        out NumericRangeValidationFailure failure)
    {
        bytes = default;
        if (!TryParsePositiveDecimal(value, out var gb))
        {
            failure = NumericRangeValidationFailure.NotPositive;
            return false;
        }

        if (gb > MaxSpoolSizeGbValue)
        {
            failure = NumericRangeValidationFailure.TooLarge;
            return false;
        }

        var byteCount = gb * BytesPerGb;
        if (byteCount < 1)
        {
            failure = NumericRangeValidationFailure.TooSmall;
            return false;
        }

        bytes = (long)byteCount;
        failure = NumericRangeValidationFailure.None;
        return true;
    }

    private static bool TryConvertDaysToAge(
        string? value,
        out TimeSpan age,
        out NumericRangeValidationFailure failure)
    {
        age = default;
        if (!TryParsePositiveDecimal(value, out var days))
        {
            failure = NumericRangeValidationFailure.NotPositive;
            return false;
        }

        if (days > MaxSpoolAgeDaysValue)
        {
            failure = NumericRangeValidationFailure.TooLarge;
            return false;
        }

        var ticks = days * TicksPerDay;
        if (ticks < 1)
        {
            failure = NumericRangeValidationFailure.TooSmall;
            return false;
        }

        age = TimeSpan.FromTicks((long)ticks);
        failure = NumericRangeValidationFailure.None;
        return true;
    }

    private static string FormatGb(long bytes) =>
        (bytes / BytesPerGb).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatDays(TimeSpan age) =>
        age.TotalDays.ToString("0.###", CultureInfo.InvariantCulture);

    private static string GetMaxSpoolSizeStatus(NumericRangeValidationFailure failure) =>
        failure switch
        {
            NumericRangeValidationFailure.TooSmall => "Max spool size must be at least 1 byte.",
            NumericRangeValidationFailure.TooLarge => "Max spool size is too large.",
            _ => "Max spool size must be greater than 0 GB.",
        };

    private static string GetMaxSpoolAgeStatus(NumericRangeValidationFailure failure) =>
        failure switch
        {
            NumericRangeValidationFailure.TooSmall => "Max spool age must be at least 1 tick.",
            NumericRangeValidationFailure.TooLarge => "Max spool age is too large.",
            _ => "Max spool age must be greater than 0 days.",
        };

    private static string FormatHealthyDebugInfo(CollectorHealthSnapshot snapshot) =>
        $"Endpoint: {snapshot.Endpoint}; Protocol: {snapshot.Protocol}; Exported: {snapshot.ExportedRecords} record(s); Checked: {snapshot.CheckedAt:O}";

    private static string FormatUnhealthyDebugInfo(CollectorHealthSnapshot snapshot) =>
        $"Endpoint: {snapshot.Endpoint}; Protocol: {snapshot.Protocol}; Failure: {snapshot.ErrorType}: {snapshot.ErrorMessage}; Checked: {snapshot.CheckedAt:O}";

    private sealed record CollectorHealthUpdate(
        NinaOtelOptionsViewModel ViewModel,
        CollectorHealthSnapshot Snapshot);

    private enum NumericRangeValidationFailure
    {
        None,
        NotPositive,
        TooSmall,
        TooLarge,
    }
}
