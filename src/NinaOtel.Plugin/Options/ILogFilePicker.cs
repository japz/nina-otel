namespace NinaOtel.Plugin.Options;

/// <summary>
/// Opens a file dialog for the options UI. Kept behind an interface so the view models can be
/// unit tested without WPF.
/// </summary>
public interface ILogFilePicker
{
    /// <summary>Shows an open-file dialog and returns the chosen path, or null when cancelled.</summary>
    string? BrowseLogfile(string title, string? initialDirectory, string filter);
}

internal sealed class NullLogFilePicker : ILogFilePicker
{
    public static readonly NullLogFilePicker Instance = new();

    private NullLogFilePicker()
    {
    }

    public string? BrowseLogfile(string title, string? initialDirectory, string filter) => null;
}

#if NINAOTEL_WPF
internal sealed class WpfLogFilePicker : ILogFilePicker
{
    public static readonly WpfLogFilePicker Instance = new();

    private WpfLogFilePicker()
    {
    }

    public string? BrowseLogfile(string title, string? initialDirectory, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = false,
            CheckPathExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) &&
            System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
#endif
