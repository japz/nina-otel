using NinaOtel.Core.Options;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NinaOtel.Plugin.Options;

public sealed class AddonOptionViewModel : INotifyPropertyChanged
{
    private readonly Action<AddonOptionViewModel, string, bool> settingChanged;
    private bool isEnabled;
    private bool rawForwardingEnabled;
    private string status = "disabled";
    private string message = "Add-on disabled.";

    internal AddonOptionViewModel(
        string id,
        string displayName,
        string source,
        Action<AddonOptionViewModel, string, bool> settingChanged)
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
    };

    internal void Load(bool enabled, bool rawForwarding)
    {
        SetField(ref isEnabled, enabled, nameof(IsEnabled));
        SetField(ref rawForwardingEnabled, rawForwarding, nameof(RawForwardingEnabled));
        ApplyConfiguredStatus();
    }

    internal void UpdateHealth(string newStatus, string newMessage)
    {
        Status = newStatus;
        Message = newMessage;
    }

    private void ApplyConfiguredStatus()
    {
        if (IsEnabled)
        {
            Status = "enabled";
            Message = "Add-on enabled; reload plugin to apply changes.";
            return;
        }

        Status = "disabled";
        Message = "Add-on disabled.";
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
