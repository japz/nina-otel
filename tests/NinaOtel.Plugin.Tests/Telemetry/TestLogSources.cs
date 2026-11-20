using NinaOtel.Abstractions.Logs;

namespace NinaOtel.Plugin.Tests.Telemetry;

/// <summary>
/// Log source definitions for collector tests, so autodetection uses test folders instead of
/// the machine running the tests.
/// </summary>
internal static class TestLogSources
{
    /// <summary>A folder that stays empty, so autodetection never finds anything.</summary>
    public static string EmptyDirectory { get; } = CreateEmptyDirectory();

    public static LogSourceCatalog Empty() => Catalog(EmptyDirectory);

    public static LogSourceCatalog Catalog(string directory) => new(
        RotatingLogSource.Nina.InDirectory(directory),
        RotatingLogSource.NightSummary.InDirectory(directory),
        RotatingLogSource.TargetScheduler.InDirectory(directory),
        RotatingLogSource.Phd2Debug.InDirectory(directory),
        RotatingLogSource.Phd2Guide.InDirectory(directory));

    /// <summary>NINA log file name for a launch, shaped like the ones NINA writes.</summary>
    public static string NinaLogFileName(DateTime stamp, int processId) =>
        $"{stamp:yyyyMMdd-HHmmss}-3.3.0.1069.{processId}-{stamp:yyyyMM}.log";

    private static string CreateEmptyDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-plugin-empty-log-dir");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
