using FluentAssertions;
using NinaOtel.Abstractions.Logs;
using NinaOtel.Plugin.Options;
using NinaOtel.Plugin.Tests.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Options;

public sealed class LogPathOptionsViewModelTests
{
    [Fact]
    public void NinaLogPathAutoHint_WhenPathIsEmpty_NamesTheAutodetectedFile()
    {
        using var folder = new TempLogDirectory();
        var current = WriteNinaLog(folder.Path, new DateTime(2026, 11, 19, 9, 0, 0), Environment.ProcessId);
        var viewModel = CreateCoreViewModel(out _, catalog: TestLogSources.Catalog(folder.Path));

        viewModel.NinaLogPathAutoHint.Should().Be($"Auto: {current}");
    }

    [Fact]
    public void NinaLogPathAutoHint_WhenNothingIsFound_NamesTheSearchedFolder()
    {
        var viewModel = CreateCoreViewModel(out _, catalog: TestLogSources.Empty());

        viewModel.NinaLogPathAutoHint.Should().Be($"Auto: none found in {TestLogSources.EmptyDirectory}");
    }

    [Fact]
    public void NinaLogPathAutoHint_WhenPathIsConfigured_IsEmpty()
    {
        var viewModel = CreateCoreViewModel(out _, catalog: TestLogSources.Empty());

        viewModel.NinaLogPath = @"C:\NINA\Logs\custom.log";

        viewModel.NinaLogPathAutoHint.Should().BeEmpty();
    }

    [Fact]
    public void BrowseNinaLogPathCommand_WhenFileIsPicked_StoresThePath()
    {
        using var folder = new TempLogDirectory();
        var picker = new FakeLogFilePicker();
        var viewModel = CreateCoreViewModel(out var settings, picker: picker, catalog: TestLogSources.Empty());
        var picked = WriteFile(folder.Path, "picked-nina.log");

        picker.Result = picked;
        viewModel.BrowseNinaLogPathCommand.Execute(null);

        viewModel.NinaLogPath.Should().Be(picked);
        settings.GetString("NinaLogPath", string.Empty).Should().Be(picked);
        picker.Title.Should().Contain("NINA");
        picker.Filter.Should().Contain("*.log");
        picker.InitialDirectory.Should().Be(TestLogSources.EmptyDirectory);
    }

    [Fact]
    public void BrowseNinaLogPathCommand_WhenDialogIsCancelled_KeepsThePath()
    {
        var picker = new FakeLogFilePicker();
        var viewModel = CreateCoreViewModel(out _, picker: picker, catalog: TestLogSources.Empty());

        picker.Result = null;
        viewModel.BrowseNinaLogPathCommand.Execute(null);

        viewModel.NinaLogPath.Should().BeEmpty();
    }

    [Fact]
    public void BrowseNinaLogPathCommand_WhenAFileIsConfigured_OpensInThePickedFolder()
    {
        using var folder = new TempLogDirectory();
        var configured = WriteFile(folder.Path, "some.log");
        var picker = new FakeLogFilePicker { Result = configured };
        var viewModel = CreateCoreViewModel(out _, picker: picker, catalog: TestLogSources.Empty());

        viewModel.NinaLogPath = configured;
        viewModel.BrowseNinaLogPathCommand.Execute(null);

        picker.InitialDirectory.Should().Be(folder.Path);
    }

    [Fact]
    public void AddonBrowseCommands_StoreTheirPickedPathsPerAddon()
    {
        using var folder = new TempLogDirectory();
        var picker = new FakeLogFilePicker();
        var phd2 = CreateAddonViewModel("phd2", picker);
        var targetScheduler = CreateAddonViewModel("target-scheduler", picker);
        var nightSummary = CreateAddonViewModel("night-summary", picker);

        var debug = WriteFile(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");
        picker.Result = debug;
        phd2.BrowsePhd2DebugLogPathCommand.Execute(null);
        phd2.Phd2DebugLogPath.Should().Be(debug);
        picker.Filter.Should().Contain("*.txt");

        var guide = WriteFile(folder.Path, "PHD2_GuideLog_2026-11-20_170000.txt");
        picker.Result = guide;
        phd2.BrowsePhd2GuideLogPathCommand.Execute(null);
        phd2.Phd2GuideLogPath.Should().Be(guide);

        var scheduler = WriteFile(folder.Path, "TS-20261120-165603-3.3.0.1069.1.log");
        picker.Result = scheduler;
        targetScheduler.BrowseTargetSchedulerLogPathCommand.Execute(null);
        targetScheduler.TargetSchedulerLogPath.Should().Be(scheduler);
        picker.Title.Should().Contain("Target Scheduler");

        var nightSummaryPath = WriteFile(folder.Path, "nina.log");
        picker.Result = nightSummaryPath;
        nightSummary.BrowseNightSummaryLogPathCommand.Execute(null);
        nightSummary.NightSummaryLogPath.Should().Be(nightSummaryPath);
        picker.Title.Should().Contain("Night Summary");
    }

    [Fact]
    public void AddonAutoHints_WhenPathIsEmpty_NameTheDetectedFileOrFolder()
    {
        using var folder = new TempLogDirectory();
        var catalog = TestLogSources.Catalog(folder.Path);
        var debug = WriteFile(folder.Path, "PHD2_DebugLog_2026-11-20_165716.txt");
        var guide = WriteFile(folder.Path, "PHD2_GuideLog_2026-11-20_170000.txt");
        var scheduler = WriteFile(folder.Path, "TS-20261120-165603-3.3.0.1069.1.log");
        var nina = WriteNinaLog(folder.Path, new DateTime(2026, 11, 20, 16, 56, 0), Environment.ProcessId);

        CreateAddonViewModel("phd2", catalog: catalog).Phd2DebugLogPathAutoHint.Should().Be($"Auto: {debug}");
        CreateAddonViewModel("phd2", catalog: catalog).Phd2GuideLogPathAutoHint.Should().Be($"Auto: {guide}");
        CreateAddonViewModel("target-scheduler", catalog: catalog)
            .TargetSchedulerLogPathAutoHint.Should().Be($"Auto: {scheduler}");
        CreateAddonViewModel("night-summary", catalog: catalog)
            .NightSummaryLogPathAutoHint.Should().Be($"Auto: {nina}");

        CreateAddonViewModel("phd2", catalog: TestLogSources.Empty())
            .Phd2DebugLogPathAutoHint.Should().Be($"Auto: none found in {TestLogSources.EmptyDirectory}");
    }

    [Fact]
    public void AddonPathSetter_WhenPathChanges_RaisesHintPropertyChanged()
    {
        var viewModel = CreateAddonViewModel("phd2", catalog: TestLogSources.Empty());
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.Phd2DebugLogPath = @"C:\PHD2\logs\PHD2_DebugLog_2026-11-20_165716.txt";

        changed.Should().Contain(new[] { "Phd2DebugLogPath", "Phd2DebugLogPathAutoHint" });
        viewModel.Phd2DebugLogPathAutoHint.Should().BeEmpty();
    }

    private static NinaOtelOptionsViewModel CreateCoreViewModel(
        out InMemoryPluginSettingsStore settings,
        ILogFilePicker? picker = null,
        LogSourceCatalog? catalog = null)
    {
        var store = new InMemoryPluginSettingsStore();
        settings = store;
        return new NinaOtelOptionsViewModel(store, new FakeSecretProtector(), picker, catalog);
    }

    private static AddonOptionViewModel CreateAddonViewModel(
        string id,
        ILogFilePicker? picker = null,
        LogSourceCatalog? catalog = null) =>
        new(id, id, id, (_, _, _) => { }, picker, catalog);

    private static string WriteFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "line" + Environment.NewLine);
        return path;
    }

    private static string WriteNinaLog(string directory, DateTime stamp, int processId)
    {
        var name = $"{stamp:yyyyMMdd-HHmmss}-3.3.0.1069.{processId}-{stamp:yyyyMM}.log";
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "line" + Environment.NewLine);
        return path;
    }

    private sealed class FakeLogFilePicker : ILogFilePicker
    {
        public string? Result { get; set; }
        public string? Title { get; private set; }
        public string? InitialDirectory { get; private set; }
        public string? Filter { get; private set; }

        public string? BrowseLogfile(string title, string? initialDirectory, string filter)
        {
            Title = title;
            InitialDirectory = initialDirectory;
            Filter = filter;
            return Result;
        }
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";

        public bool TryUnprotect(string protectedSecret, out string secret)
        {
            secret = string.Empty;
            return false;
        }
    }

    private sealed class InMemoryPluginSettingsStore : INinaOtelSettingsStore
    {
        private readonly Dictionary<string, object> values = new();

        public string GetString(string name, string defaultValue) =>
            values.TryGetValue(name, out var value) && value is string text ? text : defaultValue;

        public void SetString(string name, string value) => values[name] = value;

        public bool GetBoolean(string name, bool defaultValue) =>
            values.TryGetValue(name, out var value) && value is bool boolean ? boolean : defaultValue;

        public void SetBoolean(string name, bool value) => values[name] = value;
    }

    private sealed class TempLogDirectory : IDisposable
    {
        public TempLogDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninaotel-options-" + Guid.NewGuid().ToString("N"));
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
