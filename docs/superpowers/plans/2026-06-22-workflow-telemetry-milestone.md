# Workflow Telemetry Milestone Implementation Plan

> **For agentic workers:** Execute each task exactly as written. Use TDD for behavior changes. Do not run GitHub release/build workflows until every local task and review gate is complete. Keep the implementation graceful: telemetry collection failures must never interfere with NINA operation.

## Goal

Ship Alpha 65 with useful workflow-level telemetry without another slow intermediate GitHub build loop. The milestone adds:

- Filtered NINA log/event telemetry for warnings/errors plus useful lifecycle breadcrumbs.
- Core workflow span breadcrumbs, including a session span.
- Bounded add-on enrichment for PHD2, Target Scheduler, and Night Summary.
- Options UI/settings for the new core log collection controls.

This milestone does **not** add deep PHD2 socket/API polling, Target Scheduler artifact/API parsing, Night Summary SQLite/report parsing, expanded OnStepX polling, or new external dependencies.

## Baseline

- Branch/worktree: `/Users/jasper/git/nina-otel/.worktrees/workflow-telemetry`
- Base commit: `1084337`
- Previous release: `v0.1.0-alpha.64`
- Local packaging baseline: `bash tests/package-plugin-tests.sh` passes.
- Broad core test baseline on this macOS host has one unrelated existing failure in `OtlpHttpClientFactoryTests.CreateHandler_WhenClientCertificateAndPrivateKeyPathsAreConfigured_LoadsClientCertificate` due to a missing Apple keychain. Use focused test commands plus package verification for this milestone.

## Architecture

Use the existing internal `TelemetryRecord` model and existing `ITelemetrySink` pipeline. Core and add-on telemetry should remain passive:

- Parse NINA logs from a configured path.
- Emit normalized `TelemetryRecord` instances as logs/spans.
- Handle missing files, locked files, malformed lines, and parse errors without throwing out of plugin startup, UI binding, or collector callbacks.
- Reuse established option view-model and XAML patterns.

Existing workflow-ish telemetry must keep its stable names:

- `nina.exposure`
- `nina.image_save`
- `nina.filter_change`
- `nina.dither_settle`
- `nina.slew`
- `nina.autofocus`
- `phd2.dither`
- `phd2.settle`
- `target_scheduler.planning`
- `night_summary.session`
- `night_summary.report`

## Files

Expected new files:

- `src/NinaOtel.Core/Logs/NinaLogEvent.cs`
- `src/NinaOtel.Core/Logs/NinaLogParser.cs`
- `src/NinaOtel.Plugin/Telemetry/NinaLogTelemetryCollector.cs`
- `src/NinaOtel.Plugin/Telemetry/NinaLogTailer.cs`
- `tests/NinaOtel.Core.Tests/Logs/NinaLogParserTests.cs`
- `tests/NinaOtel.Plugin.Tests/Telemetry/NinaLogTelemetryCollectorTests.cs`
- `tests/NinaOtel.Plugin.Tests/Telemetry/WorkflowTelemetryContractTests.cs`

Expected modified files:

- `src/NinaOtel.Core/Options/NinaOtelOptions.cs`
- `src/NinaOtel.Core/Telemetry/CoreLifecycleTelemetryProducer.cs`
- `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
- `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- `src/NinaOtel.Plugin/Options/Options.xaml`
- `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs`
- Existing PHD2, Target Scheduler, and Night Summary add-on telemetry files and tests as needed.

## Task 1: Core NINA Log Options

Add user-facing options for core NINA log telemetry.

Requirements:

1. Add `CoreTelemetryOptions.NinaLogPath` with an empty-string default.
2. Persist and restore these view-model properties:
   - `NinaLogPath`
   - `FilteredLogsEnabled`
   - `RawCoreLogForwardingEnabled`
3. Map those properties into `NinaOtelOptionsViewModel.CreateOptions()`.
4. Add a compact "Core telemetry" section to `Options.xaml` for:
   - Filtered NINA logs on/off.
   - Raw NINA log forwarding on/off.
   - NINA log path text input.
5. Any disabled or getter-only checkbox binding in the touched XAML must explicitly use `Mode=OneWay`.
6. Add/extend focused tests in `NinaOtelOptionsViewModelTests` so the new settings fail before implementation and pass after.

Verification:

- Focused view-model tests pass.
- XAML/source tests that cover option bindings pass.
- No changes to exporter behavior in this task.

Commit message:

`Add core NINA log options`

## Task 2: NINA Log Parser And Tail Collector

Implement filtered NINA log telemetry from the configured NINA log file.

Requirements:

1. Add `NinaLogEvent` and `NinaLogEventKind` in core.
2. Add `NinaLogParser` that parses NINA log rows in this format:
   `DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE`
3. Parsing must split only the first five `|` separators so message text can contain `|`.
4. Add multiline handling for exception continuations and stack traces. Continuation lines attach to the previous parsed event message.
5. Classify at least:
   - `Warning`
   - `Error`
   - `Fatal`
   - `ApplicationStarted`
   - `ApplicationClosing`
   - `PluginLoaded`
   - `PluginLoadFailed`
   - `EquipmentConnected`
   - `EquipmentDisconnected`
   - `SequenceStarted`
   - `SequenceFinished`
   - `AutofocusStarted`
   - `AutofocusFinished`
   - `MeridianFlipStarted`
   - `MeridianFlipFinished`
   - `SafetyUnsafe`
   - `SafetySafe`
6. Add `NinaLogTelemetryCollector` in plugin:
   - Reads the configured path when `Core.FilteredLogsEnabled` or `Core.RawForwardingEnabled` is true.
   - Emits warning/error/fatal log records when filtered logs are enabled.
   - Emits raw parsed NINA log records when raw forwarding is enabled.
   - Emits lifecycle breadcrumb records for classified non-error events when filtered logs are enabled.
   - Adds attributes: `nina.log.level`, `nina.log.source`, `nina.log.member`, `nina.log.line`, `nina.log.kind`, and parsed timestamp where available.
   - Fails open on missing file, malformed rows, IO errors, and collector disposal.
7. Add `NinaLogTailer` that can start from end-of-file in runtime mode, with a testable read-existing mode for tests.
8. Wire the collector into `NinaOtelPlugin` start/dispose lifecycle.

Verification:

- Parser tests cover header skipping, message pipes, malformed rows, multiline stack traces, and classification.
- Collector tests cover filtered warnings/errors, lifecycle breadcrumbs, raw forwarding, missing file graceful behavior, and disposal.
- Existing plugin wiring tests cover creation/start/dispose of the new collector.

Commit message:

`Add filtered NINA log telemetry`

## Task 3: Session And Workflow Breadcrumb Spans

Add core session/workflow breadcrumbs while preserving existing workflow names.

Requirements:

1. Extend `CoreLifecycleTelemetryProducer` to emit:
   - `nina.session` span start when the plugin starts.
   - `nina.session` span stop when the plugin stops/disposes.
2. Session attributes should include at least `service.name`, `service.version` if available, and `ninaotel.component=core`.
3. NINA log lifecycle breadcrumbs from Task 2 should use stable names:
   - `nina.application.start`
   - `nina.application.stop`
   - `nina.plugin.loaded`
   - `nina.plugin.load_failed`
   - `nina.equipment.connected`
   - `nina.equipment.disconnected`
   - `nina.sequence.start`
   - `nina.sequence.stop`
   - `nina.autofocus.start`
   - `nina.autofocus.stop`
   - `nina.meridian_flip.start`
   - `nina.meridian_flip.stop`
   - `nina.safety.unsafe`
   - `nina.safety.safe`
4. Add `WorkflowTelemetryContractTests` that assert stable record names for all existing workflow-ish spans/logs listed in Architecture plus the new names above.
5. Do not invent parent span IDs unless the code has deterministic parent context already available.

Verification:

- `CoreLifecycleTelemetryProducerTests` cover session start/stop records.
- `WorkflowTelemetryContractTests` pass.

Commit message:

`Add core workflow breadcrumb telemetry`

## Task 4: Bounded Add-On Breadcrumb Enrichment

Enrich existing add-on telemetry without adding new data sources.

Requirements:

1. PHD2 telemetry:
   - Preserve existing dither, settle, guide summary, guide pulse, guiding started/stopped, and capture error records.
   - Add `workflow.kind=dither` to PHD2 dither and settle records.
   - Add `workflow.kind=guiding` to PHD2 guide summary, guide pulse, guiding started/stopped, and capture error records.
   - Add stable breadcrumb log names for guiding state changes if missing.
2. Target Scheduler telemetry:
   - Preserve existing planning, selected target, plan start/stop, image grading, warning/error records.
   - Add `workflow.kind=scheduling` to planning, selected target, warning, and error records.
   - Add `workflow.kind=imaging_plan` to plan start/stop and image grading records.
   - Preserve target/filter/image grading attributes already emitted.
3. Night Summary telemetry:
   - Preserve existing session/report/autofocus/meridian flip records.
   - Add `workflow.kind=session_summary` to session and report records.
   - Add `workflow.kind=autofocus` to autofocus records.
   - Add `workflow.kind=meridian_flip` to meridian flip records.
4. Add focused tests for any changed attributes/names.
5. Do not add API polling, database reads, report parsing, or new dependencies.

Verification:

- Focused add-on telemetry tests pass.
- Contract tests still pass.

Commit message:

`Enrich add-on workflow breadcrumbs`

## Task 5: Local Milestone Verification And Release Prep

Prepare Alpha 65 for the single GitHub build/release loop.

Requirements:

1. Run focused tests for:
   - Core log parser.
   - NINA log collector.
   - Options view model/XAML.
   - Lifecycle producer.
   - Workflow contract.
   - Changed add-on telemetry tests.
2. Run `bash tests/package-plugin-tests.sh`.
3. Run a final code review pass over the full diff.
4. Bump plugin version:
   - `AssemblyVersion("0.1.0.65")`
   - `AssemblyFileVersion("0.1.0.65")`
   - `AssemblyInformationalVersion("0.1.0-alpha.65")`
5. Merge to `main`, tag `v0.1.0-alpha.65`, push `main` and tag once, and watch GitHub Build/Release.
6. Verify release assets exist and report the final asset hashes.

Commit message:

`Release v0.1.0-alpha.65`
