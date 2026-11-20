using NinaOtel.Abstractions.Logs;
using System.Windows.Input;

namespace NinaOtel.Plugin.Options;

/// <summary>Minimal command used by the options UI buttons.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action execute;
    private readonly Func<bool>? canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Shared browse/hint behaviour for the log path fields in the options UI.</summary>
internal static class LogPathUi
{
    public const string NinaLogFilter = "NINA logs (*.log)|*.log|All files (*.*)|*.*";
    public const string NightSummaryLogFilter = "NINA logs (*.log)|*.log|All files (*.*)|*.*";
    public const string TargetSchedulerLogFilter = "Target Scheduler logs (TS-*.log)|TS-*.log|All files (*.*)|*.*";
    public const string Phd2LogFilter = "PHD2 logs (*.txt)|*.txt|All files (*.*)|*.*";

    public static ILogFilePicker DefaultPicker { get; } =
#if NINAOTEL_WPF
        WpfLogFilePicker.Instance;
#else
        NullLogFilePicker.Instance;
#endif

    /// <summary>
    /// Hint shown under an empty path box: the file autodetection would tail, or where it looked.
    /// Empty when the user pinned an explicit file.
    /// </summary>
    public static string AutoHint(RotatingLogSource source, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        var watcher = new LogSourceWatcher(source, configuredPath);
        return watcher.CurrentPath is { } path
            ? $"Auto: {path}"
            : $"Auto: none found in {watcher.SearchedDirectory}";
    }

    /// <summary>Folder the browse dialog opens in: the picked file's folder, else the detected folder.</summary>
    public static string? InitialDirectory(RotatingLogSource source, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetFullPath(configuredPath.Trim()));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
            catch (Exception)
            {
                // Fall through to the source's default folder.
            }
        }

        return source.Directory;
    }

    /// <summary>Opens the dialog for one source and returns the chosen path, or null.</summary>
    public static string? Browse(
        ILogFilePicker picker,
        RotatingLogSource source,
        string? configuredPath,
        string filter,
        string title)
    {
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(source);

        return picker.BrowseLogfile(
            title,
            InitialDirectory(source, configuredPath),
            filter);
    }
}
