using NINA.Profile;
using NINA.Profile.Interfaces;

namespace NinaOtel.Plugin.Options;

public sealed class ProfilePluginSettingsStore : INinaOtelSettingsStore
{
    private readonly IPluginOptionsAccessor pluginSettings;

    public ProfilePluginSettingsStore(IProfileService profileService, Guid pluginGuid)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        pluginSettings = new PluginOptionsAccessor(profileService, pluginGuid);
    }

    public string GetString(string name, string defaultValue) =>
        pluginSettings.GetValueString(name, defaultValue);

    public void SetString(string name, string value) =>
        pluginSettings.SetValueString(name, value);

    public bool GetBoolean(string name, bool defaultValue) =>
        pluginSettings.GetValueBoolean(name, defaultValue);

    public void SetBoolean(string name, bool value) =>
        pluginSettings.SetValueBoolean(name, value);
}
