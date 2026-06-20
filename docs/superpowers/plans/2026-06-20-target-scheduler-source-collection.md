# Target Scheduler Source Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Target Scheduler first-party add-on collect useful Target Scheduler log telemetry instead of only reporting that source collection is unimplemented.

**Architecture:** Keep Target Scheduler-specific parsing and file tailing inside `NinaOtel.Addons.TargetScheduler`. The plugin options UI persists a single Target Scheduler log path into `NinaOtelOptions.Addons["target-scheduler"].Settings["LogPath"]`. The add-on starts a non-blocking background tail worker for that path, parses NINA-style pipe-delimited log records, and publishes normalized `TelemetryRecord` logs/spans through `IAddonContext.Sink`.

**Tech Stack:** C#/.NET 8, NINA add-on contract, async file IO, xUnit/FluentAssertions, existing `mise`-managed dotnet.

---

## File Structure

- Modify `src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs`
  - Add Target Scheduler log path settings and `IsTargetScheduler`.
- Modify `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Load/save `Addon.target-scheduler.LogPath`.
- Modify `src/NinaOtel.Plugin/Options/Options.xaml`
  - Show a Target Scheduler log path text box only on the Target Scheduler add-on row.
- Modify `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Cover Target Scheduler log path persistence and options output.
- Modify `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`
  - Cover Target Scheduler log path binding.
- Create `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogEvent.cs`
  - Internal parsed-event record and event kind enum.
- Create `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogParser.cs`
  - Pure parser for pipe-delimited Target Scheduler log lines and multiline continuations.
- Create `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogTailer.cs`
  - Async append-only file tailer scoped to the Target Scheduler add-on.
- Modify `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerTelemetryAddon.cs`
  - Validate config, start/stop tail worker, convert events into telemetry records.
- Create `tests/NinaOtel.Core.Tests/Addons/TargetSchedulerLogParserTests.cs`
  - Parser red/green tests.
- Create `tests/NinaOtel.Core.Tests/Addons/TargetSchedulerTelemetryAddonTests.cs`
  - Add-on lifecycle and non-blocking source collection tests.

## Task 1: Target Scheduler Log Path Settings

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Test: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
- Test: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing options tests**

Add these tests to `NinaOtelOptionsViewModelTests`:

```csharp
[Fact]
public void TargetSchedulerAddonSettings_SaveLogPathAndExposeOptionsSettings()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);
    var scheduler = viewModel.Addons.Single(addon => addon.Id == "target-scheduler");

    scheduler.TargetSchedulerLogPath = "C:\\Users\\astro\\AppData\\Local\\NINA\\Logs\\target-scheduler.log";

    settings.GetString("Addon.target-scheduler.LogPath", string.Empty)
        .Should()
        .Be("C:\\Users\\astro\\AppData\\Local\\NINA\\Logs\\target-scheduler.log");
    viewModel.Options.Addons["target-scheduler"].Settings
        .Should()
        .Contain("LogPath", "C:\\Users\\astro\\AppData\\Local\\NINA\\Logs\\target-scheduler.log");
}

[Fact]
public void Constructor_LoadsPersistedTargetSchedulerLogPathSetting()
{
    var settings = new InMemoryPluginSettingsStore();
    settings.SetString("Addon.target-scheduler.LogPath", "D:\\NINA\\TargetScheduler.log");

    var viewModel = new NinaOtelOptionsViewModel(settings);
    var scheduler = viewModel.Addons.Single(addon => addon.Id == "target-scheduler");

    scheduler.TargetSchedulerLogPath.Should().Be("D:\\NINA\\TargetScheduler.log");
    viewModel.Options.Addons["target-scheduler"].Settings
        .Should()
        .Contain("LogPath", "D:\\NINA\\TargetScheduler.log");
}
```

Add this test to `OptionsXamlTests`:

```csharp
[Fact]
public void OptionsTemplate_TargetSchedulerLogPathTextBoxUsesTwoWayLostFocusBinding()
{
    var document = XDocument.Load(FindOptionsXamlPath());
    var itemsControl = document
        .Descendants(PresentationNamespace + "ItemsControl")
        .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);

    var textbox = SingleTextBoxBoundTo(itemsControl, "TargetSchedulerLogPath");
    var binding = textbox.Attribute("Text")?.Value;

    binding.Should().Contain("Mode=TwoWay");
    binding.Should().Contain("UpdateSourceTrigger=LostFocus");
    textbox.Attribute("Visibility")?.Value.Should().Contain("IsTargetScheduler");
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter FullyQualifiedName~TargetSchedulerAddonSettings -v:minimal
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~TargetSchedulerLogPathTextBox -v:minimal
```

Expected: compile/test failures because the Target Scheduler path property and XAML binding do not exist.

- [ ] **Step 3: Implement settings support**

In `AddonOptionViewModel`, add constants and fields:

```csharp
private const string TargetSchedulerAddonId = "target-scheduler";
private const string TargetSchedulerLogPathSettingName = "LogPath";
private string targetSchedulerLogPath = string.Empty;
```

Add properties:

```csharp
public bool IsTargetScheduler => string.Equals(Id, TargetSchedulerAddonId, StringComparison.Ordinal);

public string TargetSchedulerLogPath
{
    get => targetSchedulerLogPath;
    set => SetTargetSchedulerPath(ref targetSchedulerLogPath, value, TargetSchedulerLogPathSettingName);
}
```

In `Load`, set the field:

```csharp
SetField(
    ref targetSchedulerLogPath,
    IsTargetScheduler && settings.TryGetValue(TargetSchedulerLogPathSettingName, out var schedulerLogPath)
        ? schedulerLogPath
        : string.Empty,
    nameof(TargetSchedulerLogPath));
```

In `CreateSettings`, branch by add-on:

```csharp
var settings = new Dictionary<string, string>();
if (IsPhd2)
{
    AddSettingIfConfigured(settings, Phd2DebugLogPathSettingName, Phd2DebugLogPath);
    AddSettingIfConfigured(settings, Phd2GuideLogPathSettingName, Phd2GuideLogPath);
}
else if (IsTargetScheduler)
{
    AddSettingIfConfigured(settings, TargetSchedulerLogPathSettingName, TargetSchedulerLogPath);
}

return settings;
```

Add setter helper:

```csharp
private void SetTargetSchedulerPath(ref string field, string? value, string settingName)
{
    if (!IsTargetScheduler)
    {
        return;
    }

    var normalized = value?.Trim() ?? string.Empty;
    if (SetField(ref field, normalized))
    {
        settingChanged(this, settingName, normalized);
    }
}
```

In `NinaOtelOptionsViewModel`, update `AddonStringSettings`:

```csharp
["phd2"] = ["DebugLogPath", "GuideLogPath"],
["target-scheduler"] = ["LogPath"],
```

In `Options.xaml`, add a Target Scheduler-only row under the PHD2 rows:

```xml
<TextBlock
    Grid.Row="8"
    Grid.Column="0"
    Text="Target Scheduler log:"
    FontWeight="SemiBold"
    VerticalAlignment="Center"
    Visibility="{Binding IsTargetScheduler, Converter={StaticResource NinaOtel_BooleanToVisibilityConverter}}" />
<TextBox
    Grid.Row="8"
    Grid.Column="2"
    MinWidth="220"
    Text="{Binding TargetSchedulerLogPath, Mode=TwoWay, UpdateSourceTrigger=LostFocus}"
    Visibility="{Binding IsTargetScheduler, Converter={StaticResource NinaOtel_BooleanToVisibilityConverter}}" />
```

Move the existing status/message row down to the next available grid row.

- [ ] **Step 4: Run tests to verify they pass**

Run the commands from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Add Target Scheduler log path setting"
```

## Task 2: Target Scheduler Log Parser

**Files:**
- Create: `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogEvent.cs`
- Create: `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogParser.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/TargetSchedulerLogParserTests.cs`

- [ ] **Step 1: Write failing parser tests**

Create `TargetSchedulerLogParserTests` with these cases:

```csharp
[Theory]
[InlineData("2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started", TargetSchedulerLogEventKind.PlanningStarted)]
[InlineData("2026-06-18T22:00:05.0000|INFO|Scheduler.cs|Run|20|Target Scheduler: planning run completed", TargetSchedulerLogEventKind.PlanningCompleted)]
[InlineData("2026-06-18T22:00:10.0000|INFO|Scheduler.cs|Select|30|Target Scheduler: selected target M31 filter L", TargetSchedulerLogEventKind.TargetSelected)]
[InlineData("2026-06-18T22:00:20.0000|INFO|Scheduler.cs|Plan|40|Target Scheduler: plan started for M31", TargetSchedulerLogEventKind.PlanStarted)]
[InlineData("2026-06-18T22:00:30.0000|INFO|Scheduler.cs|Plan|50|Target Scheduler: hard stop reached for M31", TargetSchedulerLogEventKind.PlanStopped)]
[InlineData("2026-06-18T22:01:00.0000|INFO|ImageGrader.cs|Grade|60|Target Scheduler: image grade accepted target=M31 score=0.92", TargetSchedulerLogEventKind.ImageGraded)]
[InlineData("2026-06-18T22:02:00.0000|WARNING|Scheduler.cs|Run|70|Target Scheduler: rejected target M42 below horizon", TargetSchedulerLogEventKind.Warning)]
[InlineData("2026-06-18T22:03:00.0000|ERROR|Scheduler.cs|Run|80|Target Scheduler: planning failed timeout", TargetSchedulerLogEventKind.Error)]
internal void TryParse_WhenLineContainsKnownTargetSchedulerEvent_ReturnsExpectedKind(
    string line,
    TargetSchedulerLogEventKind expectedKind)
{
    var parsed = TargetSchedulerLogParser.TryParse(
        line,
        "C:\\NINA\\target-scheduler.log",
        new FixedTimeProvider(DateTimeOffset.UtcNow),
        out var logEvent);

    parsed.Should().BeTrue();
    logEvent.Should().NotBeNull();
    logEvent!.Kind.Should().Be(expectedKind);
}
```

Also cover:

```csharp
[Fact]
public void TryParse_WhenPipeDelimitedTimestampIsPresent_CapturesSourcePathOriginalLineAndTimestamp()
{
    const string line = "2026-06-18T22:00:00.1234|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started";

    var parsed = TargetSchedulerLogParser.TryParse(
        line,
        "C:\\NINA\\target-scheduler.log",
        new FixedTimeProvider(DateTimeOffset.UtcNow),
        out var logEvent);

    parsed.Should().BeTrue();
    logEvent!.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 22, 0, 0, 123, TimeSpan.Zero));
    logEvent.Source.Should().Be("target-scheduler");
    logEvent.SourcePath.Should().Be("C:\\NINA\\target-scheduler.log");
    logEvent.OriginalLine.Should().Be(line);
    logEvent.Level.Should().Be("INFO");
    logEvent.Message.Should().Be("Target Scheduler: planning run started");
}

[Fact]
public void TryParse_WhenLineIsContinuation_AppendsToPreviousMessage()
{
    const string line = "    target=M31 score=0.92 filter=L";

    var parsed = TargetSchedulerLogParser.TryParse(
        line,
        "C:\\NINA\\target-scheduler.log",
        new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 22, 1, 0, TimeSpan.Zero)),
        out var logEvent);

    parsed.Should().BeFalse("continuation lines are buffered by the add-on tailer before parser invocation");
}
```

- [ ] **Step 2: Run parser tests to verify they fail**

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~TargetSchedulerLogParserTests -v:minimal
```

Expected: compile failure because parser types do not exist.

- [ ] **Step 3: Implement parser**

Create `TargetSchedulerLogEvent.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NinaOtel.Core.Tests")]

namespace NinaOtel.Addons.TargetScheduler;

internal enum TargetSchedulerLogEventKind
{
    PlanningStarted,
    PlanningCompleted,
    TargetSelected,
    PlanStarted,
    PlanStopped,
    ImageGraded,
    Warning,
    Error,
}

internal sealed record TargetSchedulerLogEvent(
    TargetSchedulerLogEventKind Kind,
    DateTimeOffset Timestamp,
    string Source,
    string SourcePath,
    string OriginalLine,
    string Level,
    string Message);
```

Create `TargetSchedulerLogParser.cs`:

```csharp
using System.Globalization;

namespace NinaOtel.Addons.TargetScheduler;

internal static class TargetSchedulerLogParser
{
    private const string Source = "target-scheduler";
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fff",
    ];

    internal static bool TryParse(
        string line,
        string sourcePath,
        TimeProvider timeProvider,
        out TargetSchedulerLogEvent? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split('|', 6);
        if (parts.Length < 6)
        {
            return false;
        }

        var message = parts[5].Trim();
        if (!TryGetKind(parts[1], message, out var kind))
        {
            return false;
        }

        timeProvider ??= TimeProvider.System;
        parsed = new TargetSchedulerLogEvent(
            kind,
            ParseTimestamp(parts[0], timeProvider),
            Source,
            sourcePath ?? string.Empty,
            line,
            parts[1].Trim(),
            message);
        return true;
    }

    private static bool TryGetKind(string level, string message, out TargetSchedulerLogEventKind kind)
    {
        if (string.Equals(level.Trim(), "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            kind = TargetSchedulerLogEventKind.Error;
            return Contains(message, "Target Scheduler");
        }

        if (string.Equals(level.Trim(), "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            kind = TargetSchedulerLogEventKind.Warning;
            return Contains(message, "Target Scheduler");
        }

        if (Contains(message, "planning run started"))
        {
            kind = TargetSchedulerLogEventKind.PlanningStarted;
            return true;
        }

        if (Contains(message, "planning run completed"))
        {
            kind = TargetSchedulerLogEventKind.PlanningCompleted;
            return true;
        }

        if (Contains(message, "selected target"))
        {
            kind = TargetSchedulerLogEventKind.TargetSelected;
            return true;
        }

        if (Contains(message, "plan started") || Contains(message, "published target"))
        {
            kind = TargetSchedulerLogEventKind.PlanStarted;
            return true;
        }

        if (Contains(message, "plan stopped") || Contains(message, "min-expire") || Contains(message, "hard stop"))
        {
            kind = TargetSchedulerLogEventKind.PlanStopped;
            return true;
        }

        if (Contains(message, "image grade") || Contains(message, "image grading"))
        {
            kind = TargetSchedulerLogEventKind.ImageGraded;
            return true;
        }

        kind = default;
        return false;
    }

    private static DateTimeOffset ParseTimestamp(string value, TimeProvider timeProvider)
    {
        if (DateTime.TryParseExact(
                value.Trim(),
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return timeProvider.GetUtcNow();
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run parser tests to verify they pass**

Run the command from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogEvent.cs src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogParser.cs tests/NinaOtel.Core.Tests/Addons/TargetSchedulerLogParserTests.cs
git commit -m "Parse useful Target Scheduler log events"
```

## Task 3: Async Target Scheduler Add-On Collection

**Files:**
- Create: `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogTailer.cs`
- Modify: `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerTelemetryAddon.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/TargetSchedulerTelemetryAddonTests.cs`

- [ ] **Step 1: Write failing add-on tests**

Create tests proving:

```csharp
[Fact]
public async Task StartAsync_WhenNoPathIsConfigured_ReportsWaitingForLogPath()
```

Expected health: `addon.id=target-scheduler`, `status=waiting`, message contains `Target Scheduler log path`.

```csharp
[Fact]
public async Task StartAsync_WhenConfiguredFileIsMissing_ReportsWaitingAndKeepsRunning()
```

Create a missing path, start the add-on, assert a waiting health record containing `not found`, then create the file and append a recognized line. Assert `target_scheduler.planning_started` is eventually published.

```csharp
[Fact]
public async Task Tailer_WhenRecognizedLineIsAppended_PublishesNormalizedLogRecord()
```

Use a temp file, start with `rawForwardingEnabled: true`, append:

```text
2026-06-18T22:00:00.0000|INFO|Scheduler.cs|Run|10|Target Scheduler: planning run started
```

Assert:

```csharp
record.Name.Should().Be("target_scheduler.planning_started");
record.Signal.Should().Be(TelemetrySignal.Log);
record.Source.Should().Be("target-scheduler");
record.Severity.Should().Be(TelemetrySeverity.Information);
record.Priority.Should().Be(TelemetryPriority.Normal);
record.Attributes["source.file"].Should().Be(temp.Path);
record.Attributes["event.kind"].Should().Be("planning_started");
record.Attributes["raw.line"].Should().Be(appendedLine);
```

Also test:

```csharp
[Fact]
public async Task Tailer_WhenWarningOrErrorLineIsAppended_PublishesImportantLog()
```

Warning severity maps to `TelemetrySeverity.Warning`, error maps to `TelemetrySeverity.Error`; both use `TelemetryPriority.Important`.

```csharp
[Fact]
public async Task Tailer_WhenPlanningStartAndCompletedAreAppended_PublishesPlanningSpanStartAndStop()
```

`planning run started` publishes `TelemetryRecord.Span("target_scheduler.planning", SpanEventKind.Start, ...)`; `planning run completed` publishes matching `SpanEventKind.Stop` with the same non-empty `SpanId`.

```csharp
[Fact]
public async Task StopAsync_CancelsBackgroundTailWorker()
```

Start, stop, append another line, delay briefly, assert no new record is published.

- [ ] **Step 2: Run add-on tests to verify they fail**

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~TargetSchedulerTelemetryAddonTests -v:minimal
```

Expected: compile failures or failing assertions because the add-on still only reports the shell status.

- [ ] **Step 3: Implement tailer and add-on**

Create `TargetSchedulerLogTailer` by copying the proven shape of `Phd2LogTailer`, changing the class name and namespace to `NinaOtel.Addons.TargetScheduler`.

Modify `TargetSchedulerTelemetryAddon`:

```csharp
private const string LogPathSetting = "LogPath";
private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
private readonly TimeSpan pollInterval;
private readonly TimeSpan stopTimeout;
private TargetSchedulerLogTailer? tailer;
private string? activePlanningSpanId;

public TargetSchedulerTelemetryAddon()
    : this(DefaultPollInterval, DefaultStopTimeout)
{
}

internal TargetSchedulerTelemetryAddon(TimeSpan pollInterval)
    : this(pollInterval, DefaultStopTimeout)
{
}

internal TargetSchedulerTelemetryAddon(TimeSpan pollInterval, TimeSpan stopTimeout)
{
    this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
    this.stopTimeout = stopTimeout > TimeSpan.Zero ? stopTimeout : DefaultStopTimeout;
}
```

Rules:

- `Validate` returns success for empty/missing path.
- `StartAsync` returns promptly.
- If `LogPath` is missing/empty, report `waiting` with `Configure the Target Scheduler log path to collect source telemetry.`
- If configured, stop any existing tailer before starting a new one.
- Create a tailer with a line handler that parses and publishes events.
- Missing file reports `waiting` and keeps retrying.
- Once a worker is configured, report `running`.
- `StopAsync` cancels the tailer and waits up to `stopTimeout`.

Telemetry conversion:

- Log records:
  - `target_scheduler.target_selected`
  - `target_scheduler.plan_started`
  - `target_scheduler.plan_stopped`
  - `target_scheduler.image_graded`
  - `target_scheduler.warning`
  - `target_scheduler.error`
- Planning span:
  - `target_scheduler.planning` start on `PlanningStarted`
  - `target_scheduler.planning` stop on `PlanningCompleted`
- Attributes:
  - `addon.id=target-scheduler`
  - `event.kind`
  - `source.file`
  - `message`
  - `raw.line` only when `RawForwardingEnabled` is true

- [ ] **Step 4: Run add-on tests to verify they pass**

Run the command from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Addons.TargetScheduler/TargetSchedulerLogTailer.cs src/NinaOtel.Addons.TargetScheduler/TargetSchedulerTelemetryAddon.cs tests/NinaOtel.Core.Tests/Addons/TargetSchedulerTelemetryAddonTests.cs
git commit -m "Collect useful Target Scheduler log telemetry"
```

## Task 4: Integration Verification

**Files:**
- May modify tests only if a narrow integration assertion needs an update.

- [ ] **Step 1: Run focused tests**

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~TargetScheduler|FullyQualifiedName~OptionsXaml|FullyQualifiedName~AddonHost" -v:minimal
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "FullyQualifiedName~NinaOtelOptionsViewModelTests|FullyQualifiedName~NinaOtelPluginWiringTests" -v:minimal
```

- [ ] **Step 2: Run package check**

```bash
bash tests/package-plugin-tests.sh
```

- [ ] **Step 3: Run diff hygiene**

```bash
git diff --check
```

- [ ] **Step 4: Commit any integration fixes**

If files changed, inspect them first:

```bash
git status --short
git add src/NinaOtel.Addons.TargetScheduler tests/NinaOtel.Core.Tests/Addons tests/NinaOtel.Plugin.Tests/Options tests/NinaOtel.Core.Tests/Plugin src/NinaOtel.Plugin/Options
git commit -m "Verify Target Scheduler source collection integration"
```

If no files changed, do not create an empty commit.

## Acceptance Criteria

- Target Scheduler add-on no longer reports that source collection is unimplemented when a path is configured.
- Target Scheduler add-on starts without blocking NINA/plugin startup.
- Missing Target Scheduler log files are health state, not plugin failure.
- Recognized Target Scheduler log lines produce normalized telemetry records.
- Planning start/completion lines produce a correlated planning span.
- Warning/error Target Scheduler lines are exported as important logs.
- Raw full lines are only attached when raw forwarding is enabled.
- Target Scheduler log path can be edited in the options UI and is persisted into `NinaOtelOptions.Addons["target-scheduler"].Settings`.
- Existing package check still passes.
