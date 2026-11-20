using NinaOtel.Abstractions.Logs;
using NinaOtel.Core.Options;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NinaOtel.Plugin.Options;

public sealed class AddonOptionViewModel : INotifyPropertyChanged
{
    private const string Phd2AddonId = "phd2";
    private const string Phd2DebugLogPathSettingName = "DebugLogPath";
    private const string Phd2GuideLogPathSettingName = "GuideLogPath";
    internal const string TargetSchedulerAddonId = "target-scheduler";
    internal const string TargetSchedulerLogPathSettingName = "LogPath";
    internal const string NightSummaryAddonId = "night-summary";
    internal const string NightSummaryLogPathSettingName = "LogPath";
    internal const string OnStepXAddonId = "onstepx";
    internal const string OnStepXHostSettingName = "Host";
    internal const string OnStepXPortSettingName = "Port";
    internal const string OnStepXPollingIntervalSecondsSettingName = "PollingIntervalSeconds";
    internal const string OnStepXCommandTimeoutMillisecondsSettingName = "CommandTimeoutMilliseconds";

    private readonly Action<AddonOptionViewModel, string, object> settingChanged;
    private readonly ILogFilePicker filePicker;
    private readonly LogSourceCatalog catalog;
    private bool isEnabled;
    private bool rawForwardingEnabled;
    private bool hasSourceSpecificHealth;
    private string phd2DebugLogPath = string.Empty;
    private string phd2GuideLogPath = string.Empty;
    private string targetSchedulerLogPath = string.Empty;
    private string nightSummaryLogPath = string.Empty;
    private string onStepXHost = string.Empty;
    private string onStepXPort = string.Empty;
    private string onStepXPollingIntervalSeconds = string.Empty;
    private string onStepXCommandTimeoutMilliseconds = string.Empty;
    private string status = "disabled";
    private string message = "Add-on disabled.";

    internal AddonOptionViewModel(
        string id,
        string displayName,
        string source,
        Action<AddonOptionViewModel, string, object> settingChanged,
        ILogFilePicker? filePicker = null,
        LogSourceCatalog? catalog = null)
    {
        Id = id;
        DisplayName = displayName;
        Source = source;
        this.settingChanged = settingChanged;
        this.filePicker = filePicker ?? LogPathUi.DefaultPicker;
        this.catalog = catalog ?? LogSourceCatalog.Default;
        BrowsePhd2DebugLogPathCommand = new RelayCommand(BrowsePhd2DebugLogPath);
        BrowsePhd2GuideLogPathCommand = new RelayCommand(BrowsePhd2GuideLogPath);
        BrowseTargetSchedulerLogPathCommand = new RelayCommand(BrowseTargetSchedulerLogPath);
        BrowseNightSummaryLogPathCommand = new RelayCommand(BrowseNightSummaryLogPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string DisplayName { get; }
    public string Source { get; }
    public bool IsPhd2 => string.Equals(Id, Phd2AddonId, StringComparison.Ordinal);
    public bool IsTargetScheduler => string.Equals(Id, TargetSchedulerAddonId, StringComparison.Ordinal);
    public bool IsNightSummary => string.Equals(Id, NightSummaryAddonId, StringComparison.Ordinal);
    public bool IsOnStepX => string.Equals(Id, OnStepXAddonId, StringComparison.Ordinal);

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (!SetField(ref isEnabled, value))
            {
                return;
            }

            ApplyConfiguredStatus();
            settingChanged(this, "Enabled", value);
        }
    }

    public bool RawForwardingEnabled
    {
        get => rawForwardingEnabled;
        set
        {
            if (SetField(ref rawForwardingEnabled, value))
            {
                settingChanged(this, "RawForwardingEnabled", value);
            }
        }
    }

    public string Phd2DebugLogPath
    {
        get => phd2DebugLogPath;
        set
        {
            if (SetPhd2Path(ref phd2DebugLogPath, value, Phd2DebugLogPathSettingName))
            {
                RaisePropertyChanged(nameof(Phd2DebugLogPathAutoHint));
            }
        }
    }

    /// <summary>Opens a file dialog to pick the PHD2 debug log.</summary>
    public ICommand BrowsePhd2DebugLogPathCommand { get; }

    /// <summary>What autodetection would tail while <see cref="Phd2DebugLogPath"/> is empty.</summary>
    public string Phd2DebugLogPathAutoHint => LogPathUi.AutoHint(catalog.Phd2Debug, phd2DebugLogPath);

    public string Phd2GuideLogPath
    {
        get => phd2GuideLogPath;
        set
        {
            if (SetPhd2Path(ref phd2GuideLogPath, value, Phd2GuideLogPathSettingName))
            {
                RaisePropertyChanged(nameof(Phd2GuideLogPathAutoHint));
            }
        }
    }

    /// <summary>Opens a file dialog to pick the PHD2 guide log.</summary>
    public ICommand BrowsePhd2GuideLogPathCommand { get; }

    /// <summary>What autodetection would tail while <see cref="Phd2GuideLogPath"/> is empty.</summary>
    public string Phd2GuideLogPathAutoHint => LogPathUi.AutoHint(catalog.Phd2Guide, phd2GuideLogPath);

    public string TargetSchedulerLogPath
    {
        get => targetSchedulerLogPath;
        set
        {
            if (!IsTargetScheduler)
            {
                return;
            }

            var normalized = value?.Trim() ?? string.Empty;
            if (SetField(ref targetSchedulerLogPath, normalized))
            {
                settingChanged(this, TargetSchedulerLogPathSettingName, normalized);
                RaisePropertyChanged(nameof(TargetSchedulerLogPathAutoHint));
            }
        }
    }

    /// <summary>Opens a file dialog to pick the Target Scheduler log.</summary>
    public ICommand BrowseTargetSchedulerLogPathCommand { get; }

    /// <summary>What autodetection would tail while <see cref="TargetSchedulerLogPath"/> is empty.</summary>
    public string TargetSchedulerLogPathAutoHint =>
        LogPathUi.AutoHint(catalog.TargetScheduler, targetSchedulerLogPath);

    public string NightSummaryLogPath
    {
        get => nightSummaryLogPath;
        set
        {
            if (!IsNightSummary)
            {
                return;
            }

            var normalized = value?.Trim() ?? string.Empty;
            if (SetField(ref nightSummaryLogPath, normalized))
            {
                settingChanged(this, NightSummaryLogPathSettingName, normalized);
                RaisePropertyChanged(nameof(NightSummaryLogPathAutoHint));
            }
        }
    }

    /// <summary>Opens a file dialog to pick the Night Summary (NINA) log.</summary>
    public ICommand BrowseNightSummaryLogPathCommand { get; }

    /// <summary>What autodetection would tail while <see cref="NightSummaryLogPath"/> is empty.</summary>
    public string NightSummaryLogPathAutoHint => LogPathUi.AutoHint(catalog.NightSummary, nightSummaryLogPath);

    public string OnStepXHost
    {
        get => onStepXHost;
        set => SetOnStepXSetting(ref onStepXHost, value, OnStepXHostSettingName);
    }

    public string OnStepXPort
    {
        get => onStepXPort;
        set => SetOnStepXSetting(ref onStepXPort, value, OnStepXPortSettingName);
    }

    public string OnStepXPollingIntervalSeconds
    {
        get => onStepXPollingIntervalSeconds;
        set => SetOnStepXSetting(
            ref onStepXPollingIntervalSeconds,
            value,
            OnStepXPollingIntervalSecondsSettingName);
    }

    public string OnStepXCommandTimeoutMilliseconds
    {
        get => onStepXCommandTimeoutMilliseconds;
        set => SetOnStepXSetting(
            ref onStepXCommandTimeoutMilliseconds,
            value,
            OnStepXCommandTimeoutMillisecondsSettingName);
    }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public string Message
    {
        get => message;
        private set => SetField(ref message, value);
    }

    internal AddonOptions CreateOptions() => new()
    {
        Enabled = IsEnabled,
        RawForwardingEnabled = RawForwardingEnabled,
        Settings = CreateSettings(),
    };

    internal void Load(bool enabled, bool rawForwarding, IReadOnlyDictionary<string, string> settings)
    {
        SetField(ref isEnabled, enabled, nameof(IsEnabled));
        SetField(ref rawForwardingEnabled, rawForwarding, nameof(RawForwardingEnabled));
        SetField(
            ref phd2DebugLogPath,
            IsPhd2 && settings.TryGetValue(Phd2DebugLogPathSettingName, out var debugLogPath)
                ? debugLogPath
                : string.Empty,
            nameof(Phd2DebugLogPath));
        SetField(
            ref phd2GuideLogPath,
            IsPhd2 && settings.TryGetValue(Phd2GuideLogPathSettingName, out var guideLogPath)
                ? guideLogPath
                : string.Empty,
            nameof(Phd2GuideLogPath));
        SetField(
            ref targetSchedulerLogPath,
            IsTargetScheduler && settings.TryGetValue(TargetSchedulerLogPathSettingName, out var targetSchedulerLogPathSetting)
                ? targetSchedulerLogPathSetting
                : string.Empty,
            nameof(TargetSchedulerLogPath));
        SetField(
            ref nightSummaryLogPath,
            IsNightSummary && settings.TryGetValue(NightSummaryLogPathSettingName, out var nightSummaryLogPathSetting)
                ? nightSummaryLogPathSetting
                : string.Empty,
            nameof(NightSummaryLogPath));
        SetField(
            ref onStepXHost,
            IsOnStepX && settings.TryGetValue(OnStepXHostSettingName, out var onStepXHostSetting)
                ? onStepXHostSetting
                : string.Empty,
            nameof(OnStepXHost));
        SetField(
            ref onStepXPort,
            IsOnStepX && settings.TryGetValue(OnStepXPortSettingName, out var onStepXPortSetting)
                ? onStepXPortSetting
                : string.Empty,
            nameof(OnStepXPort));
        SetField(
            ref onStepXPollingIntervalSeconds,
            IsOnStepX &&
                settings.TryGetValue(
                    OnStepXPollingIntervalSecondsSettingName,
                    out var onStepXPollingIntervalSecondsSetting)
                    ? onStepXPollingIntervalSecondsSetting
                    : string.Empty,
            nameof(OnStepXPollingIntervalSeconds));
        SetField(
            ref onStepXCommandTimeoutMilliseconds,
            IsOnStepX &&
                settings.TryGetValue(
                    OnStepXCommandTimeoutMillisecondsSettingName,
                    out var onStepXCommandTimeoutMillisecondsSetting)
                    ? onStepXCommandTimeoutMillisecondsSetting
                    : string.Empty,
            nameof(OnStepXCommandTimeoutMilliseconds));
        ApplyConfiguredStatus();
    }

    internal void UpdateHealth(string newStatus, string newMessage)
    {
        if (hasSourceSpecificHealth && IsGenericStartedHealth(newStatus, newMessage))
        {
            return;
        }

        Status = newStatus;
        Message = newMessage;
        if (!IsGenericHostHealth(newStatus, newMessage))
        {
            hasSourceSpecificHealth = true;
        }
    }

    private void ApplyConfiguredStatus()
    {
        hasSourceSpecificHealth = false;

        if (IsEnabled)
        {
            Status = "enabled";
            Message = "Add-on enabled.";
            return;
        }

        Status = "disabled";
        Message = "Add-on disabled.";
    }

    private static bool IsGenericStartedHealth(string status, string message) =>
        string.Equals(status, "started", StringComparison.Ordinal) &&
        string.Equals(message, "Add-on started.", StringComparison.Ordinal);

    private static bool IsGenericHostHealth(string status, string message) =>
        IsGenericStartedHealth(status, message) ||
        IsKnownGenericHostStatus(status);

    private static bool IsKnownGenericHostStatus(string status) =>
        status is
            "disabled" or
            "validation_failed" or
            "start_timeout" or
            "start_error" or
            "stopped" or
            "stop_timeout" or
            "stop_error";

    private IReadOnlyDictionary<string, string> CreateSettings()
    {
        var settings = new Dictionary<string, string>();
        if (IsPhd2)
        {
            AddSettingIfConfigured(settings, Phd2DebugLogPathSettingName, Phd2DebugLogPath);
            AddSettingIfConfigured(settings, Phd2GuideLogPathSettingName, Phd2GuideLogPath);
        }

        if (IsTargetScheduler)
        {
            AddSettingIfConfigured(settings, TargetSchedulerLogPathSettingName, TargetSchedulerLogPath);
        }

        if (IsNightSummary)
        {
            AddSettingIfConfigured(settings, NightSummaryLogPathSettingName, NightSummaryLogPath);
        }

        if (IsOnStepX)
        {
            AddSettingIfConfigured(settings, OnStepXHostSettingName, OnStepXHost);
            AddSettingIfConfigured(settings, OnStepXPortSettingName, OnStepXPort);
            AddSettingIfConfigured(
                settings,
                OnStepXPollingIntervalSecondsSettingName,
                OnStepXPollingIntervalSeconds);
            AddSettingIfConfigured(
                settings,
                OnStepXCommandTimeoutMillisecondsSettingName,
                OnStepXCommandTimeoutMilliseconds);
        }

        return settings;
    }

    private bool SetPhd2Path(ref string field, string? value, string settingName)
    {
        if (!IsPhd2)
        {
            return false;
        }

        var normalized = value?.Trim() ?? string.Empty;
        if (SetField(ref field, normalized))
        {
            settingChanged(this, settingName, normalized);
            return true;
        }

        return false;
    }

    private void BrowsePhd2DebugLogPath()
    {
        var picked = LogPathUi.Browse(
            filePicker,
            catalog.Phd2Debug,
            phd2DebugLogPath,
            LogPathUi.Phd2LogFilter,
            "Select the PHD2 debug log to tail");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Phd2DebugLogPath = picked;
        }
    }

    private void BrowsePhd2GuideLogPath()
    {
        var picked = LogPathUi.Browse(
            filePicker,
            catalog.Phd2Guide,
            phd2GuideLogPath,
            LogPathUi.Phd2LogFilter,
            "Select the PHD2 guide log to tail");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Phd2GuideLogPath = picked;
        }
    }

    private void BrowseTargetSchedulerLogPath()
    {
        var picked = LogPathUi.Browse(
            filePicker,
            catalog.TargetScheduler,
            targetSchedulerLogPath,
            LogPathUi.TargetSchedulerLogFilter,
            "Select the Target Scheduler log to tail");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            TargetSchedulerLogPath = picked;
        }
    }

    private void BrowseNightSummaryLogPath()
    {
        var picked = LogPathUi.Browse(
            filePicker,
            catalog.NightSummary,
            nightSummaryLogPath,
            LogPathUi.NightSummaryLogFilter,
            "Select the Night Summary (NINA) log to tail");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            NightSummaryLogPath = picked;
        }
    }

    private void SetOnStepXSetting(ref string field, string? value, string settingName)
    {
        if (!IsOnStepX)
        {
            return;
        }

        var normalized = value?.Trim() ?? string.Empty;
        if (SetField(ref field, normalized))
        {
            settingChanged(this, settingName, normalized);
        }
    }

    private static void AddSettingIfConfigured(
        Dictionary<string, string> settings,
        string name,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            settings[name] = value;
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
