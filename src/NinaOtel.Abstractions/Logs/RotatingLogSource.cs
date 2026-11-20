using System.Globalization;

namespace NinaOtel.Abstractions.Logs;

/// <summary>
/// Describes one rotating log source: the folder its files live in, the file-name
/// pattern that identifies those files, and how to order them.
/// </summary>
/// <param name="DisplayName">Human readable name used in health and UI messages.</param>
/// <param name="Directory">Default folder to search when no path is configured.</param>
/// <param name="FileNamePattern">Glob with <c>*</c> and <c>?</c>, matched case-insensitively.</param>
/// <param name="PreferCurrentProcessId">
/// Prefer files whose name contains <c>.{processId}</c>, used for logs written by NINA in-process.
/// </param>
/// <param name="TimestampRegex">
/// Optional regular expression whose first group captures a <c>yyyyMMdd-HHmmss</c> or
/// <c>yyyy-MM-dd_HHmmss</c> stamp in the file name, used to pick the newest file.
/// </param>
public sealed record RotatingLogSource(
    string DisplayName,
    string Directory,
    string FileNamePattern,
    bool PreferCurrentProcessId = false,
    string? TimestampRegex = null)
{
    private static readonly string NinaLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA",
        "Logs");

    private static readonly string TargetSchedulerLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA",
        "SchedulerPlugin",
        "Logs");

    private static readonly string Phd2LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PHD2");

    /// <summary>NINA core log, one file per launch named like 20261120-165600-3.3.0.1069.13204-202611.log.</summary>
    public static RotatingLogSource Nina { get; } = new(
        "NINA log",
        NinaLogDirectory,
        "*.log",
        PreferCurrentProcessId: true,
        TimestampRegex: @"^(\d{8}-\d{6})-");

    /// <summary>Target Scheduler log, one file per launch named like TS-20261120-165603-3.3.0.1069.13204.log.</summary>
    public static RotatingLogSource TargetScheduler { get; } = new(
        "Target Scheduler log",
        TargetSchedulerLogDirectory,
        "TS-*.log",
        PreferCurrentProcessId: true,
        TimestampRegex: @"^TS-(\d{8}-\d{6})");

    /// <summary>PHD2 debug log, one file per PHD2 start named like PHD2_DebugLog_2026-11-20_165716.txt.</summary>
    public static RotatingLogSource Phd2Debug { get; } = new(
        "PHD2 debug log",
        Phd2LogDirectory,
        "PHD2_DebugLog_*.txt",
        PreferCurrentProcessId: false,
        TimestampRegex: @"^PHD2_DebugLog_(\d{4}-\d{2}-\d{2}_\d{6})");

    /// <summary>PHD2 guide log, one file per guiding session named like PHD2_GuideLog_2026-11-20_165716.txt.</summary>
    public static RotatingLogSource Phd2Guide { get; } = new(
        "PHD2 guide log",
        Phd2LogDirectory,
        "PHD2_GuideLog_*.txt",
        PreferCurrentProcessId: false,
        TimestampRegex: @"^PHD2_GuideLog_(\d{4}-\d{2}-\d{2}_\d{6})");

    /// <summary>Night Summary reads its lines out of the NINA core log.</summary>
    public static RotatingLogSource NightSummary { get; } = Nina with { DisplayName = "Night Summary/NINA log" };

    /// <summary>Placeholder source for a file the user pointed at explicitly (never rotated).</summary>
    public static RotatingLogSource Explicit { get; } = new("Log file", string.Empty, "*");

    /// <summary>Returns a copy of this source that searches <paramref name="directory"/> instead.</summary>
    public RotatingLogSource InDirectory(string directory) => this with { Directory = directory };
}

/// <summary>
/// The set of log sources the plugin autodetects. Injectable so tests and custom
/// installations can point sources at other folders.
/// </summary>
public sealed class LogSourceCatalog
{
    public LogSourceCatalog()
        : this(
            RotatingLogSource.Nina,
            RotatingLogSource.NightSummary,
            RotatingLogSource.TargetScheduler,
            RotatingLogSource.Phd2Debug,
            RotatingLogSource.Phd2Guide)
    {
    }

    public LogSourceCatalog(
        RotatingLogSource nina,
        RotatingLogSource nightSummary,
        RotatingLogSource targetScheduler,
        RotatingLogSource phd2Debug,
        RotatingLogSource phd2Guide)
    {
        Nina = nina ?? throw new ArgumentNullException(nameof(nina));
        NightSummary = nightSummary ?? throw new ArgumentNullException(nameof(nightSummary));
        TargetScheduler = targetScheduler ?? throw new ArgumentNullException(nameof(targetScheduler));
        Phd2Debug = phd2Debug ?? throw new ArgumentNullException(nameof(phd2Debug));
        Phd2Guide = phd2Guide ?? throw new ArgumentNullException(nameof(phd2Guide));
    }

    public static LogSourceCatalog Default { get; } = new();

    public RotatingLogSource Nina { get; }

    public RotatingLogSource NightSummary { get; }

    public RotatingLogSource TargetScheduler { get; }

    public RotatingLogSource Phd2Debug { get; }

    public RotatingLogSource Phd2Guide { get; }
}

/// <summary>
/// Finds the file to tail for a <see cref="RotatingLogSource"/>: the autodetected or
/// rotating current file, or the exact file the user pointed at.
/// </summary>
public sealed class LogSourceResolver
{
    private static readonly System.Text.RegularExpressions.RegexOptions RegexOptions =
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Singleline;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex?> RegexCache =
        new(StringComparer.Ordinal);

    public static LogSourceResolver Default { get; } = new();

    private const string TimestampFormat = "yyyyMMddHHmmss";

    /// <summary>
    /// Resolves the file to read for <paramref name="source"/> given the configured path.
    /// An empty path autodetects in the source's folder; a path whose file name matches the
    /// source pattern follows rotation inside that file's folder; any other path is pinned.
    /// </summary>
    public LogSourceResolution Resolve(
        RotatingLogSource source,
        string? configuredPath,
        int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var configured = configuredPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new LogSourceResolution(
                LogSourceResolutionMode.Autodetect,
                FindCurrentFile(source, source.Directory, processId),
                source.Directory,
                ConfiguredPath: null);
        }

        var fullPath = TryGetFullPath(configured);
        if (fullPath is null)
        {
            return new LogSourceResolution(
                LogSourceResolutionMode.Pinned,
                configured,
                Directory: string.Empty,
                ConfiguredPath: configured);
        }

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var fileName = Path.GetFileName(fullPath);
        if (!MatchesPattern(fileName, source.FileNamePattern))
        {
            return new LogSourceResolution(
                LogSourceResolutionMode.Pinned,
                fullPath,
                directory,
                ConfiguredPath: configured);
        }

        return new LogSourceResolution(
            LogSourceResolutionMode.Autodetect,
            FindCurrentFile(source, directory, processId),
            directory,
            ConfiguredPath: configured);
    }

    /// <summary>Matches a file name against the source glob, case-insensitively.</summary>
    public static bool MatchesPattern(string fileName, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        return MatchesGlob(fileName, pattern, 0, 0);
    }

    internal static DateTime? ParseTimestampFromName(string fileName, string? timestampRegex)
    {
        var regex = GetRegex(timestampRegex);
        if (regex is null)
        {
            return null;
        }

        var match = regex.Match(fileName);
        if (!match.Success || match.Groups.Count < 2 || !match.Groups[1].Success)
        {
            return null;
        }

        var digits = new string(match.Groups[1].Value.Where(char.IsDigit).ToArray());
        if (digits.Length != TimestampFormat.Length)
        {
            return null;
        }

        return DateTime.TryParseExact(
            digits,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static System.Text.RegularExpressions.Regex? GetRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        return RegexCache.GetOrAdd(
            pattern,
            static key =>
            {
                try
                {
                    return new System.Text.RegularExpressions.Regex(key, RegexOptions);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindCurrentFile(RotatingLogSource source, string directory, int? processId)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidates = new List<LogFileInfo>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileName(file);
                if (!MatchesPattern(fileName, source.FileNamePattern))
                {
                    continue;
                }

                candidates.Add(LogFileInfo.Read(file, source));
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (source.PreferCurrentProcessId && processId is { } pid)
        {
            var token = $".{pid.ToString(CultureInfo.InvariantCulture)}";
            var ownFiles = candidates.FindAll(
                candidate => candidate.FileName.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (ownFiles.Count > 0)
            {
                candidates = ownFiles;
            }
        }

        var useFileNames = candidates.TrueForAll(static candidate => candidate.Timestamp.HasValue);
        candidates.Sort(
            (left, right) =>
            {
                var leftKey = useFileNames && left.Timestamp.HasValue
                    ? left.Timestamp.Value
                    : left.LastWriteTimeUtc;
                var rightKey = useFileNames && right.Timestamp.HasValue
                    ? right.Timestamp.Value
                    : right.LastWriteTimeUtc;
                var byTime = rightKey.CompareTo(leftKey);
                if (byTime != 0)
                {
                    return byTime;
                }

                var byWriteTime = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                if (byWriteTime != 0)
                {
                    return byWriteTime;
                }

                return string.Compare(right.FileName, left.FileName, StringComparison.OrdinalIgnoreCase);
            });

        return candidates[0].FullPath;
    }

    private static bool MatchesGlob(
        string value,
        string pattern,
        int valueIndex,
        int patternIndex)
    {
        while (patternIndex < pattern.Length)
        {
            var patternCharacter = pattern[patternIndex];
            if (patternCharacter == '*')
            {
                patternIndex++;
                for (var probe = valueIndex; probe <= value.Length; probe++)
                {
                    if (MatchesGlob(value, pattern, probe, patternIndex))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (valueIndex >= value.Length)
            {
                return false;
            }

            if (patternCharacter != '?' &&
                char.ToLowerInvariant(patternCharacter) != char.ToLowerInvariant(value[valueIndex]))
            {
                return false;
            }

            valueIndex++;
            patternIndex++;
        }

        return valueIndex == value.Length;
    }

    private readonly struct LogFileInfo
    {
        private LogFileInfo(string fullPath, string fileName, DateTime lastWriteTimeUtc, DateTime? timestamp)
        {
            FullPath = fullPath;
            FileName = fileName;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Timestamp = timestamp;
        }

        public string FullPath { get; }

        public string FileName { get; }

        public DateTime LastWriteTimeUtc { get; }

        public DateTime? Timestamp { get; }

        public static LogFileInfo Read(string file, RotatingLogSource source)
        {
            DateTime lastWriteTimeUtc = default;
            try
            {
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception)
            {
                lastWriteTimeUtc = default;
            }

            var fileName = Path.GetFileName(file);
            return new LogFileInfo(
                file,
                fileName,
                lastWriteTimeUtc,
                ParseTimestampFromName(fileName, source.TimestampRegex));
        }
    }
}

public enum LogSourceResolutionMode
{
    /// <summary>No path, or a path whose name matches the rotating pattern: re-resolve as files rotate.</summary>
    Autodetect,

    /// <summary>An explicit file that does not match the rotating pattern: tail exactly that file.</summary>
    Pinned,
}

public sealed record LogSourceResolution(
    LogSourceResolutionMode Mode,
    string? Path,
    string Directory,
    string? ConfiguredPath)
{
    public bool FollowsRotation => Mode == LogSourceResolutionMode.Autodetect;

    /// <summary>A resolution that reads exactly one file and never switches.</summary>
    public static LogSourceResolution PinnedFile(string path) =>
        new(
            LogSourceResolutionMode.Pinned,
            path,
            System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            path);
}

/// <summary>
/// Tracks which file a consumer should read right now. Call <see cref="Refresh"/> on every
/// poll; it re-resolves while following rotation and reports when the current file changed.
/// Not thread safe: use from the consumer that owns the tailer.
/// </summary>
public sealed class LogSourceWatcher
{
    private readonly LogSourceResolver resolver;
    private readonly int? processId;

    public LogSourceWatcher(
        RotatingLogSource source,
        string? configuredPath,
        LogSourceResolver? resolver = null,
        int? processId = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        this.resolver = resolver ?? LogSourceResolver.Default;
        this.processId = processId ?? (source.PreferCurrentProcessId ? Environment.ProcessId : null);
        Resolution = this.resolver.Resolve(source, configuredPath, this.processId);
    }

    private LogSourceWatcher(
        RotatingLogSource source,
        LogSourceResolution resolution,
        LogSourceResolver resolver,
        int? processId)
    {
        Source = source;
        Resolution = resolution;
        this.resolver = resolver;
        this.processId = processId;
    }

    /// <summary>Watches one explicit file that never rotates.</summary>
    public static LogSourceWatcher Pinned(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new LogSourceWatcher(
            RotatingLogSource.Explicit,
            LogSourceResolution.PinnedFile(path),
            LogSourceResolver.Default,
            processId: null);
    }

    public RotatingLogSource Source { get; }

    public LogSourceResolution Resolution { get; private set; }

    public string? CurrentPath => Resolution.Path;

    public bool FollowsRotation => Resolution.FollowsRotation;

    public string SearchedDirectory => Resolution.Directory;

    /// <summary>Message describing what was searched when <see cref="CurrentPath"/> is null.</summary>
    public string NotFoundMessage =>
        FollowsRotation
            ? $"No {Source.DisplayName} found in {DescribeDirectory()}"
            : $"{Source.DisplayName} not found at {Resolution.Path}";

    /// <summary>Re-resolves when following rotation. Returns true when the current file changed.</summary>
    public bool Refresh()
    {
        if (!FollowsRotation)
        {
            return false;
        }

        var next = resolver.Resolve(Source, Resolution.ConfiguredPath, processId);
        var changed = !string.Equals(next.Path, Resolution.Path, StringComparison.OrdinalIgnoreCase);
        Resolution = next;
        return changed;
    }

    private string DescribeDirectory() =>
        string.IsNullOrWhiteSpace(Resolution.Directory) ? "<no folder>" : Resolution.Directory;
}
