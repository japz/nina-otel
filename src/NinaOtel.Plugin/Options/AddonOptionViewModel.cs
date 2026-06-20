using NinaOtel.Core.Options;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NinaOtel.Plugin.Options;

public sealed class AddonOptionViewModel : INotifyPropertyChanged
{
    private const string Phd2AddonId = "phd2";
    private const string Phd2DebugLogPathSettingName = "DebugLogPath";
    private const string Phd2GuideLogPathSettingName = "GuideLogPath";

    private readonly Action<AddonOptionViewModel, string, object> settingChanged;
    private bool isEnabled;
    private bool rawForwardingEnabled;
    private bool hasSourceSpecificHealth;
    private string phd2DebugLogPath = string.Empty;
    private string phd2GuideLogPath = string.Empty;
    private string status = "disabled";
    private string message = "Add-on disabled.";

    internal AddonOptionViewModel(
        string id,
        string displayName,
        string source,
        Action<AddonOptionViewModel, string, object> settingChanged)
    {
        Id = id;
        DisplayName = displayName;
        Source = source;
        this.settingChanged = settingChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string DisplayName { get; }
    public string Source { get; }
    public bool IsPhd2 => string.Equals(Id, Phd2AddonId, StringComparison.Ordinal);

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
        set => SetPhd2Path(ref phd2DebugLogPath, value, Phd2DebugLogPathSettingName);
    }

    public string Phd2GuideLogPath
    {
        get => phd2GuideLogPath;
        set => SetPhd2Path(ref phd2GuideLogPath, value, Phd2GuideLogPathSettingName);
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
            Message = "Add-on enabled; reload plugin to apply changes.";
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
        if (!IsPhd2)
        {
            return new Dictionary<string, string>();
        }

        var settings = new Dictionary<string, string>();
        AddSettingIfConfigured(settings, Phd2DebugLogPathSettingName, Phd2DebugLogPath);
        AddSettingIfConfigured(settings, Phd2GuideLogPathSettingName, Phd2GuideLogPath);
        return settings;
    }

    private void SetPhd2Path(ref string field, string? value, string settingName)
    {
        if (!IsPhd2)
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
}
