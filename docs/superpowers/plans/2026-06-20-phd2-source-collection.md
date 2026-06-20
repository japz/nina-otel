# PHD2 Source Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the PHD2 first-party add-on collect real, useful PHD2 log telemetry instead of only reporting that source collection is unimplemented.

**Architecture:** Keep PHD2-specific parsing and file watching inside `NinaOtel.Addons.PHD2`. The add-on reads configured debug and guide log paths from its add-on settings, starts background tail workers, publishes normalized `TelemetryRecord` logs/spans/metrics through `IAddonContext.Sink`, and reports add-on health through `IAddonContext.ReportHealth`. Core pipeline and plugin wiring stay unchanged.

**Tech Stack:** C#/.NET 8, NINA plugin add-on contract, `TimeProvider`, async file IO, xUnit/FluentAssertions, existing `mise`-managed dotnet.

---

## File Structure

- Create `src/NinaOtel.Addons.PHD2/Phd2LogEvent.cs`
  - Small internal parsed-event record plus event kind enum.
- Create `src/NinaOtel.Addons.PHD2/Phd2LogParser.cs`
  - Pure parser for PHD2 debug/guide lines. No file IO.
- Create `src/NinaOtel.Addons.PHD2/Phd2LogTailer.cs`
  - Async append-only file tailer with cancellation and polling interval.
- Modify `src/NinaOtel.Addons.PHD2/Phd2TelemetryAddon.cs`
  - Validate config, start/stop background tail workers, convert parsed events into telemetry records.
- Modify `src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs`
  - Add string settings support for PHD2 debug and guide log paths without affecting other add-ons.
- Modify `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Load/save string add-on settings into `AddonOptions.Settings`.
- Modify `src/NinaOtel.Plugin/Options/Options.xaml`
  - Show PHD2 debug and guide log path text boxes only on the PHD2 add-on row.
- Modify `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Cover PHD2 path load/save and options dictionary output.
- Modify `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`
  - Cover PHD2 path bindings.
- Create `tests/NinaOtel.Core.Tests/Addons/Phd2LogParserTests.cs`
  - Parser red/green tests.
- Create `tests/NinaOtel.Core.Tests/Addons/Phd2TelemetryAddonTests.cs`
  - Add-on lifecycle and non-blocking source collection tests.

## Task 1: PHD2 Path Settings UI and Options

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Test: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
- Test: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing options tests**

Add tests proving:

```csharp
[Fact]
public void Phd2AddonSettings_SaveLogPathsAndExposeOptionsSettings()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);
    var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

    phd2.Phd2DebugLogPath = "C:\\PHD2\\PHD2_DebugLog.txt";
    phd2.Phd2GuideLogPath = "C:\\PHD2\\PHD2_GuideLog.txt";

    settings.GetString("Addon.phd2.DebugLogPath", string.Empty).Should().Be("C:\\PHD2\\PHD2_DebugLog.txt");
    settings.GetString("Addon.phd2.GuideLogPath", string.Empty).Should().Be("C:\\PHD2\\PHD2_GuideLog.txt");
    viewModel.Options.Addons["phd2"].Settings.Should().Contain("DebugLogPath", "C:\\PHD2\\PHD2_DebugLog.txt");
    viewModel.Options.Addons["phd2"].Settings.Should().Contain("GuideLogPath", "C:\\PHD2\\PHD2_GuideLog.txt");
}

[Fact]
public void Constructor_LoadsPersistedPhd2LogPathSettings()
{
    var settings = new InMemoryPluginSettingsStore();
    settings.SetString("Addon.phd2.DebugLogPath", "D:\\Logs\\debug.txt");
    settings.SetString("Addon.phd2.GuideLogPath", "D:\\Logs\\guide.txt");

    var viewModel = new NinaOtelOptionsViewModel(settings);
    var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

    phd2.Phd2DebugLogPath.Should().Be("D:\\Logs\\debug.txt");
    phd2.Phd2GuideLogPath.Should().Be("D:\\Logs\\guide.txt");
    viewModel.Options.Addons["phd2"].Settings.Should().Contain("DebugLogPath", "D:\\Logs\\debug.txt");
    viewModel.Options.Addons["phd2"].Settings.Should().Contain("GuideLogPath", "D:\\Logs\\guide.txt");
}
```

Add an XAML test that finds text boxes bound to `Phd2DebugLogPath` and `Phd2GuideLogPath`, requiring `Mode=TwoWay` and `UpdateSourceTrigger=LostFocus`.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter FullyQualifiedName~NinaOtelOptionsViewModelTests -v:minimal
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~OptionsXamlTests -v:minimal
```

Expected: failures because `AddonOptionViewModel` has no PHD2 path properties and XAML has no bindings.

- [ ] **Step 3: Implement minimal settings support**

In `AddonOptionViewModel`, add:

- `Phd2DebugLogPath`
- `Phd2GuideLogPath`
- `IsPhd2`
- internal `Load(..., IReadOnlyDictionary<string, string> settings)`
- internal `CreateOptions()` that includes non-empty PHD2 paths in `Settings`.

Keep other add-ons producing empty `Settings`.

In `NinaOtelOptionsViewModel`, load and save string settings with keys:

- `Addon.phd2.DebugLogPath`
- `Addon.phd2.GuideLogPath`

Use a general string setting callback, not duplicated boolean-only logic.

In `Options.xaml`, add two PHD2-only rows under the add-on controls. Bind `Visibility` to `IsPhd2` with the existing `BooleanToVisibilityCollapsedConverter` if available in NINA resources, or leave the controls visible only when `IsEnabled` is not required. The binding must be two-way/lost-focus.

- [ ] **Step 4: Run tests to verify they pass**

Run the two commands from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Plugin/Options/AddonOptionViewModel.cs src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Add PHD2 add-on log path settings"
```

## Task 2: PHD2 Log Parser

**Files:**
- Create: `src/NinaOtel.Addons.PHD2/Phd2LogEvent.cs`
- Create: `src/NinaOtel.Addons.PHD2/Phd2LogParser.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/Phd2LogParserTests.cs`

- [ ] **Step 1: Write failing parser tests**

Cover at least these cases:

- Debug line containing `Guiding Begins` parses as `GuidingStarted`.
- Debug line containing `Guiding Stopped` parses as `GuidingStopped`.
- Debug line containing `Dither` parses as `Dither`.
- Debug line containing `Settling started` parses as `SettleStarted`.
- Debug line containing `Settle complete` parses as `SettleCompleted`.
- Debug line containing `capture failed` or `camera error` parses as `CaptureError`.
- Unrecognized info line returns `false`.
- Recognized lines include source `phd2`, the source file path, original line, and a timestamp when the line begins with an ISO-like timestamp.

Use sample lines with predictable timestamps:

```text
2026-06-18 22:00:00.125 Guiding Begins
2026-06-18 22:00:10.000 Dither: started
2026-06-18 22:00:20.500 Settle complete
2026-06-18 22:00:30.000 capture failed: star lost
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Phd2LogParserTests -v:minimal
```

Expected: compile failure because parser types do not exist.

- [ ] **Step 3: Implement parser**

Implement `internal enum Phd2LogEventKind` and `internal sealed record Phd2LogEvent`.

Implement:

```csharp
internal static class Phd2LogParser
{
    public static bool TryParseDebugLine(
        string line,
        string sourcePath,
        TimeProvider timeProvider,
        out Phd2LogEvent parsed)
}
```

Rules:

- Return `false` for null/empty/whitespace.
- Match keywords case-insensitively.
- Prefer specific capture-error matching before broad state matching.
- Timestamp parsing accepts `yyyy-MM-dd HH:mm:ss.fff` and `yyyy-MM-ddTHH:mm:ss.fff` prefixes; otherwise use `timeProvider.GetUtcNow()`.
- Do not throw on malformed lines.

- [ ] **Step 4: Run parser tests**

Run the command from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Addons.PHD2/Phd2LogEvent.cs src/NinaOtel.Addons.PHD2/Phd2LogParser.cs tests/NinaOtel.Core.Tests/Addons/Phd2LogParserTests.cs
git commit -m "Parse useful PHD2 debug log events"
```

## Task 3: Async PHD2 Add-On Collection

**Files:**
- Create: `src/NinaOtel.Addons.PHD2/Phd2LogTailer.cs`
- Modify: `src/NinaOtel.Addons.PHD2/Phd2TelemetryAddon.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/Phd2TelemetryAddonTests.cs`

- [ ] **Step 1: Write failing add-on tests**

Cover:

- `StartAsync` returns promptly when paths are configured.
- No configured paths reports health status `waiting` with a message that asks for PHD2 log paths.
- Missing configured file reports `waiting` and keeps running rather than throwing.
- Appending a recognized debug line publishes a normalized log record named `phd2.guiding_started`.
- Appending a dither or settle line publishes a completed span named `phd2.dither` or `phd2.settle`.
- Capture error publishes an important log named `phd2.capture_error`.
- `StopAsync` cancels background tail workers.

Use temp files and a small polling interval by adding an internal constructor overload on `Phd2TelemetryAddon` that accepts `TimeSpan pollInterval`.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Phd2TelemetryAddonTests -v:minimal
```

Expected: failures because the add-on does not tail or publish source events.

- [ ] **Step 3: Implement tailer and add-on wiring**

Implement `Phd2LogTailer` as an internal disposable worker:

- Constructor accepts path, poll interval, line handler, and logger-free cancellation.
- `StartAsync` schedules a background task and returns immediately.
- If file does not exist, report through the caller-provided callback and retry on each poll.
- When file exists, open with `FileShare.ReadWrite | FileShare.Delete`.
- Start from the current end of file by default to avoid replaying historical nights.
- Read appended lines until cancellation.
- Never throw outward from background work.

Modify `Phd2TelemetryAddon`:

- Settings keys:
  - `DebugLogPath`
  - `GuideLogPath`
- `Validate` accepts empty paths and does not require files to exist.
- `StartAsync` returns promptly after creating workers.
- If no paths are configured, report `waiting`.
- For debug log events, publish:
  - `TelemetryRecord.Log` for guiding start/stop/capture error.
  - `TelemetryRecord.Span` stop records for dither and settle events.
  - Attributes: `addon.id=phd2`, `source=phd2`, `source.file`, `event.kind`, `raw.line`.
  - Raw line only when `RawForwardingEnabled` is true; otherwise include a bounded message but not the full raw line.
- Report `running` once at least one configured worker is started.
- `StopAsync` cancels workers and waits up to a small bounded timeout.

- [ ] **Step 4: Run add-on tests**

Run the command from Step 2. Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Addons.PHD2/Phd2LogTailer.cs src/NinaOtel.Addons.PHD2/Phd2TelemetryAddon.cs tests/NinaOtel.Core.Tests/Addons/Phd2TelemetryAddonTests.cs
git commit -m "Collect useful PHD2 debug log telemetry"
```

## Task 4: Integration Verification

**Files:**
- May modify tests only if an integration assertion needs a narrow update.

- [ ] **Step 1: Run focused tests**

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Phd2|FullyQualifiedName~OptionsXaml|FullyQualifiedName~AddonHost" -v:minimal
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

If any narrow fixes were needed:

```bash
git add <changed-files>
git commit -m "Verify PHD2 source collection integration"
```

If no files changed, do not create an empty commit.

## Acceptance Criteria

- PHD2 add-on no longer reports that source collection is unimplemented when a path is configured.
- PHD2 add-on starts without blocking NINA/plugin startup.
- Missing PHD2 files are health state, not plugin failure.
- Recognized PHD2 debug lines produce normalized telemetry records.
- Raw full lines are only attached when raw forwarding is enabled.
- PHD2 debug/guide log paths can be edited in the options UI and are persisted into `NinaOtelOptions.Addons["phd2"].Settings`.
- Existing package check still passes.
