namespace NinaOtel.Plugin.Options;

public interface INinaOtelSettingsStore
{
    string GetString(string name, string defaultValue);
    void SetString(string name, string value);
    bool GetBoolean(string name, bool defaultValue);
    void SetBoolean(string name, bool value);
}
