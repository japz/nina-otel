using FluentAssertions;
using NinaOtel.Abstractions.Logs;
using Xunit;

namespace NinaOtel.Core.Tests.Logs;

public sealed class LogSourceResolverTests
{
    private const string NinaOwnLog = "20261120-165600-3.3.0.1069.4242-202611.log";
    private const string NinaOtherProcessLog = "20261119-090000-3.3.0.1069.9999-202611.log";

    [Fact]
    public void Resolve_WhenFolderIsEmpty_AutodetectsWithoutAPathAndNamesTheFolder()
    {
        using var folder = new TempDirectory();
        var source = RotatingLogSource.Nina.InDirectory(folder.Path);

        var resolution = LogSourceResolver.Default.Resolve(source, configuredPath: null);

        resolution.Mode.Should().Be(LogSourceResolutionMode.Autodetect);
        resolution.FollowsRotation.Should().BeTrue();
        resolution.Path.Should().BeNull();
        resolution.Directory.Should().Be(folder.Path);
        new LogSourceWatcher(source, null)
            .NotFoundMessage
            .Should()
            .Be($"No NINA log found in {folder.Path}");
    }

    [Fact]
    public void Resolve_WhenFolderDoesNotExist_AutodetectsWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ninaotel-missing-" + Guid.NewGuid().ToString("N"));

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.TargetScheduler.InDirectory(missing),
            configuredPath: null);

        resolution.Path.Should().BeNull();
        resolution.Directory.Should().Be(missing);
    }

    [Fact]
    public void Resolve_IgnoresFilesThatDoNotMatchThePattern()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(folder.Path, "PHD2_GuideLog.txt"), "x");

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Phd2Guide.InDirectory(folder.Path),
            configuredPath: null);

        resolution.Path.Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersTheFileNamedWithTheCurrentProcessId()
    {
        using var folder = new TempDirectory();
        WriteFile(folder.Path, NinaOtherProcessLog, lastWriteUtc: new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc));
        WriteFile(folder.Path, NinaOwnLog, lastWriteUtc: new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc));

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Nina.InDirectory(folder.Path),
            configuredPath: null,
            processId: 4242);

        resolution.Path.Should().Be(Path.Combine(folder.Path, NinaOwnLog));
    }

    [Theory]
    [InlineData("20261120-165600-3.3.0.1069.4242-202611.log", 4242, true)]
    [InlineData("TS-20261120-165603-3.3.0.1069.4242.log", 4242, true)]
    [InlineData("20261120-165600-3.3.0.1072.4242-202611.log", 1072, false)]
    [InlineData("TS-20261120-165603-3.3.0.1072.4242.log", 1072, false)]
    [InlineData("20261120-165600-3.3.0.1069.42420-202611.log", 4242, false)]
    public void IsOwnProcessFile_MatchesOnlyTheProcessIdComponent(string fileName, int processId, bool expected)
    {
        LogSourceResolver.IsOwnProcessFile(fileName, processId).Should().Be(expected);
    }

    [Fact]
    public void Resolve_FallsBackToTheNewestFileWhenNoneHasTheProcessId()
    {
        using var folder = new TempDirectory();
        WriteFile(folder.Path, NinaOtherProcessLog);
        WriteFile(folder.Path, NinaOwnLog.Replace(".4242", ".777"));

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Nina.InDirectory(folder.Path),
            configuredPath: null,
            processId: 4242);

        resolution.Path.Should().Be(Path.Combine(folder.Path, "20261120-165600-3.3.0.1069.777-202611.log"));
    }

    [Fact]
    public void Resolve_UsesTheFileNameTimestampRatherThanTheWriteTime()
    {
        using var folder = new TempDirectory();
        // The newer file by name is written first, so its LastWriteTimeUtc is the older one.
        WriteFile(
            folder.Path,
            "20261121-120000-3.3.0.1069.4242-202611.log",
            lastWriteUtc: new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc));
        WriteFile(
            folder.Path,
            "20261120-165600-3.3.0.1069.4242-202611.log",
            lastWriteUtc: new DateTime(2026, 12, 1, 1, 0, 0, DateTimeKind.Utc));

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Nina.InDirectory(folder.Path),
            configuredPath: null,
            processId: 4242);

        resolution.Path.Should().Be(Path.Combine(folder.Path, "20261121-120000-3.3.0.1069.4242-202611.log"));
    }

    [Fact]
    public void Resolve_FallsBackToWriteTimeWhenFileNamesHaveNoTimestamp()
    {
        using var folder = new TempDirectory();
        var source = new RotatingLogSource("Custom log", folder.Path, "*.log");
        WriteFile(folder.Path, "older.log", lastWriteUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(folder.Path, "newer.log", lastWriteUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var resolution = LogSourceResolver.Default.Resolve(source, configuredPath: null);

        resolution.Path.Should().Be(Path.Combine(folder.Path, "newer.log"));
    }

    [Fact]
    public void Resolve_PicksTheNewestPhd2GuideLogByName()
    {
        using var folder = new TempDirectory();
        WriteFile(folder.Path, "PHD2_GuideLog_2026-11-19_210000.txt");
        WriteFile(folder.Path, "PHD2_GuideLog_2026-11-20_165716.txt");

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Phd2Guide.InDirectory(folder.Path),
            configuredPath: null);

        resolution.Path.Should().Be(Path.Combine(folder.Path, "PHD2_GuideLog_2026-11-20_165716.txt"));
    }

    [Fact]
    public void Resolve_WhenPickedFileMatchesThePattern_FollowsRotationInItsFolder()
    {
        using var folder = new TempDirectory();
        var picked = Path.Combine(folder.Path, "PHD2_DebugLog_2026-11-19_210000.txt");
        var newest = Path.Combine(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");
        WriteFile(picked);
        WriteFile(newest);

        var resolution = LogSourceResolver.Default.Resolve(
            RotatingLogSource.Phd2Debug.InDirectory(Path.Combine(folder.Path, "elsewhere")),
            picked);

        resolution.Mode.Should().Be(LogSourceResolutionMode.Autodetect);
        resolution.Directory.Should().Be(folder.Path);
        resolution.Path.Should().Be(newest);
    }

    [Fact]
    public void Resolve_WhenPickedFileDoesNotMatchThePattern_PinsThatFile()
    {
        using var folder = new TempDirectory();
        var picked = Path.Combine(folder.Path, "phd2-custom.txt");
        WriteFile(picked);
        WriteFile(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");

        var resolution = LogSourceResolver.Default.Resolve(RotatingLogSource.Phd2Debug, picked);

        resolution.Mode.Should().Be(LogSourceResolutionMode.Pinned);
        resolution.FollowsRotation.Should().BeFalse();
        resolution.Path.Should().Be(picked);
    }

    [Fact]
    public void Resolve_WhenPickedRotatingFileIsGone_ResolvesToAnotherFileInItsFolder()
    {
        using var folder = new TempDirectory();
        var picked = Path.Combine(folder.Path, "PHD2_DebugLog_2026-11-19_210000.txt");
        WriteFile(picked);

        var watcher = new LogSourceWatcher(RotatingLogSource.Phd2Debug, picked);
        watcher.CurrentPath.Should().Be(picked);

        File.Delete(picked);
        var replacement = Path.Combine(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");
        WriteFile(replacement);

        watcher.Refresh().Should().BeTrue();
        watcher.CurrentPath.Should().Be(replacement);
    }

    [Fact]
    public void Watcher_WhenPinned_DoesNotSwitchToANewerMatchingFile()
    {
        using var folder = new TempDirectory();
        var pinnedFile = Path.Combine(folder.Path, "pinned.txt");
        WriteFile(pinnedFile);
        var watcher = new LogSourceWatcher(RotatingLogSource.Phd2Debug, pinnedFile);

        WriteFile(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");

        watcher.FollowsRotation.Should().BeFalse();
        watcher.Refresh().Should().BeFalse();
        watcher.CurrentPath.Should().Be(pinnedFile);
        watcher.NotFoundMessage.Should().Be($"PHD2 debug log not found at {pinnedFile}");
    }

    [Fact]
    public void Watcher_WhenRotating_ReportsTheSwitchAndNamesTheFolderWhenNothingIsLeft()
    {
        using var folder = new TempDirectory();
        var first = Path.Combine(folder.Path, "TS-20261119-090000-3.3.0.1069.4242.log");
        WriteFile(first);
        var watcher = new LogSourceWatcher(
            RotatingLogSource.TargetScheduler.InDirectory(folder.Path),
            configuredPath: null,
            processId: 4242);
        watcher.CurrentPath.Should().Be(first);

        var second = Path.Combine(folder.Path, "TS-20261120-165603-3.3.0.1069.4242.log");
        WriteFile(second);

        watcher.Refresh().Should().BeTrue();
        watcher.CurrentPath.Should().Be(second);

        File.Delete(second);
        watcher.Refresh().Should().BeTrue("the remaining older file is still a match");
        watcher.CurrentPath.Should().Be(first);

        File.Delete(first);
        watcher.Refresh().Should().BeTrue();
        watcher.CurrentPath.Should().BeNull();
        watcher.NotFoundMessage.Should().Be($"No Target Scheduler log found in {folder.Path}");
    }

    private static void WriteFile(string directory, string fileName, DateTime? lastWriteUtc = null) =>
        WriteFile(Path.Combine(directory, fileName), lastWriteUtc);

    private static void WriteFile(string path, DateTime? lastWriteUtc = null)
    {
        File.WriteAllText(path, "line" + Environment.NewLine);
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-resolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Temp cleanup must not fail the test.
            }
        }
    }
}
