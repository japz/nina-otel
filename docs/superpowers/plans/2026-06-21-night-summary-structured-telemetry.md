# Night Summary Structured Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the existing Night Summary breadcrumb add-on output into useful structured session/report telemetry without adding SQLite or report parsing.

**Architecture:** Keep all Night Summary-specific behavior inside `NinaOtel.Addons.NightSummary`. Reuse the existing parsed `NightSummaryLogEvent` records, emit the current normalized log record for every recognized breadcrumb, and add span/metric records for session lifecycle and report lifecycle breadcrumbs. Use deterministic span ids based on source file plus session id when available, otherwise source file plus event timestamp/message.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, existing `TelemetryRecord` and `IAddonContext.Sink`.

---

## Target File Structure

- Modify: `src/NinaOtel.Addons.NightSummary/NightSummaryTelemetryAddon.cs`
  - Publish the existing `night_summary.log_event` record unchanged.
  - Add `night_summary.session` span start/stop records for session start/end.
  - Add `night_summary.report` span start/stop records for report generation and terminal report delivery/failure.
  - Add deferred point-in-time metrics for recognized counts.
- Modify: `src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs`
  - Add Night Summary deferred metric definitions and stable attributes.
- Modify: `tests/NinaOtel.Core.Tests/Addons/NightSummaryTelemetryAddonTests.cs`
  - Add focused tests for session spans, report spans, report failure severity, and count metrics.
- Modify: `tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs`
  - Add catalog assertions for Night Summary deferred metrics.

## Non-Goals

- No Night Summary SQLite parsing.
- No HTML/report parsing.
- No new options UI.
- No changes to existing breadcrumb parser recognition.
- No raw forwarding behavior changes.
- No correlation with Target Scheduler beyond attributes already present in parsed breadcrumbs.

## Task 1: Night Summary Spans And Metrics

**Files:**
- Modify: `tests/NinaOtel.Core.Tests/Addons/NightSummaryTelemetryAddonTests.cs`
- Modify: `tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs`
- Modify: `src/NinaOtel.Addons.NightSummary/NightSummaryTelemetryAddon.cs`
- Modify: `src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs`

- [x] **Step 1: Write failing add-on tests for session/report structured telemetry**

Add tests to `tests/NinaOtel.Core.Tests/Addons/NightSummaryTelemetryAddonTests.cs` that append recognized Night Summary lines and assert:

```csharp
var sessionStart = sink.Records.Single(record =>
    record.Signal == TelemetrySignal.Span &&
    record.Name == "night_summary.session" &&
    record.SpanKind == SpanEventKind.Start);
var sessionStop = sink.Records.Single(record =>
    record.Signal == TelemetrySignal.Span &&
    record.Name == "night_summary.session" &&
    record.SpanKind == SpanEventKind.Stop);
sessionStart.SpanId.Should().Be(sessionStop.SpanId);
sessionStart.Attributes["session.id"].Should().Be("abc-123");
sessionStop.Attributes["session.id"].Should().Be("abc-123");

sink.Records.Should().ContainSingle(record =>
    record.Signal == TelemetrySignal.Metric &&
    record.Name == "night_summary_session_started_count" &&
    record.NumericValue == 1);
sink.Records.Should().ContainSingle(record =>
    record.Signal == TelemetrySignal.Metric &&
    record.Name == "night_summary_session_ended_count" &&
    record.NumericValue == 1);
```

Add report tests that assert:

```csharp
var reportStart = sink.Records.Single(record =>
    record.Signal == TelemetrySignal.Span &&
    record.Name == "night_summary.report" &&
    record.SpanKind == SpanEventKind.Start);
var reportStop = sink.Records.Single(record =>
    record.Signal == TelemetrySignal.Span &&
    record.Name == "night_summary.report" &&
    record.SpanKind == SpanEventKind.Stop);
reportStart.SpanId.Should().Be(reportStop.SpanId);
reportStart.Attributes["session.id"].Should().Be("abc-123");
reportStop.Attributes["session.id"].Should().Be("abc-123");
reportStop.Attributes["event.kind"].Should().Be("report_delivered");

sink.Records.Should().ContainSingle(record =>
    record.Signal == TelemetrySignal.Metric &&
    record.Name == "night_summary_report_started_count" &&
    record.NumericValue == 1);
sink.Records.Should().ContainSingle(record =>
    record.Signal == TelemetrySignal.Metric &&
    record.Name == "night_summary_report_delivered_count" &&
    record.NumericValue == 1);
```

Add a report failure test that appends `Generating report for session abc-123 ...` and `Failed to generate/send report. SMTP failed`, then asserts the stop span uses the same `night_summary.report` span id, has `SpanKind.Stop`, includes `event.kind = report_failed`, and publishes `night_summary_report_failed_count = 1`.

- [x] **Step 2: Write failing metric catalog test**

In `tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs`, add a test that asserts these metrics exist, are category `night_summary`, use `NinaMetricExportKind.DeferredPointInTime`, and include `profile_name`, `host_name`, `addon.id`, `source`, `source.file`, `event.kind`, and `session.id` attributes:

```csharp
string[] metricNames =
[
    "night_summary_session_started_count",
    "night_summary_session_ended_count",
    "night_summary_report_started_count",
    "night_summary_report_delivered_count",
    "night_summary_report_failed_count",
    "night_summary_autofocus_completed_count",
    "night_summary_meridian_flip_count",
];
```

- [x] **Step 3: Run tests to verify RED**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~NightSummary|FullyQualifiedName~NinaMetricCatalogTests" -v:minimal
```

Expected before implementation: the new assertions fail because Night Summary only emits log records and the catalog does not yet include Night Summary metrics.

- [x] **Step 4: Implement minimal structured telemetry**

In `src/NinaOtel.Addons.NightSummary/NightSummaryTelemetryAddon.cs`:

- Make `PublishLine` publish the log record and then publish zero or more structured records derived from the same `NightSummaryLogEvent`.
- Add a `CreateStructuredRecords` helper returning `IEnumerable<TelemetryRecord>`.
- Emit `night_summary.session` start for `SessionStarted` and stop for `SessionEnded`.
- Emit `night_summary.report` start for `ReportGenerating` and stop for `ReportDelivered` or `ReportFailed`.
- Use a stable span id:
  - `night_summary.session|{sourcePath}|{sessionId}` when `SessionId` is present.
  - `night_summary.report|{sourcePath}|{sessionId}` when `SessionId` is present.
  - Fall back to a deterministic hash of source path, event kind, timestamp, and message.
- Add count metrics with numeric value `1` for session start/end, report start/delivered/failed, autofocus completed, and meridian flip.
- Reuse the existing attribute set and add `addon.id`, `source`, `source.file`, `event.kind`, `message`, and `session.id` where available.
- Keep all publish calls fail-open: exceptions from `TryPublish` must not throw out of the add-on callback.

In `src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs`:

- Add `NightSummaryMetric` helper mirroring the PHD2/TargetScheduler helper style.
- Add metric definitions for the seven metric names listed in Step 2.

- [x] **Step 5: Run focused GREEN verification**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~NightSummary|FullyQualifiedName~NinaMetricCatalogTests" -v:minimal
```

Expected after implementation: all focused tests pass.

- [x] **Step 6: Run package smoke test**

Run:

```bash
bash tests/package-plugin-tests.sh
```

Expected: package test succeeds and includes the Night Summary add-on assembly.

- [x] **Step 7: Commit**

Run:

```bash
git add src/NinaOtel.Addons.NightSummary/NightSummaryTelemetryAddon.cs src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs tests/NinaOtel.Core.Tests/Addons/NightSummaryTelemetryAddonTests.cs tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs docs/superpowers/plans/2026-06-21-night-summary-structured-telemetry.md
git commit -m "Add Night Summary structured telemetry"
```

## Self-Review Notes

- Spec coverage: Implements the design requirement that Night Summary exports session/report/autofocus/meridian breadcrumbs as useful structured signal. SQLite/report parsing remains explicitly out of scope.
- Placeholder scan: This plan contains no TODO/TBD placeholders.
- Type consistency: Uses existing `TelemetryRecord`, `TelemetrySignal`, `TelemetryPriority`, `SpanEventKind`, and `NinaMetricCatalog` patterns already present in this repository.
