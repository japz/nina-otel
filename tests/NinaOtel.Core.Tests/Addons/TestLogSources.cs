using NinaOtel.Abstractions.Logs;

namespace NinaOtel.Core.Tests.Addons;

/// <summary>
/// Log source definitions for addon tests. Every test points its sources at its own folder so
/// autodetection never picks up logs from the machine running the tests.
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

    private static string CreateEmptyDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-empty-log-dir");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
