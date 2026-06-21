# NinaOtel Disk Spool Priority Eviction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the disk spool reaches its configured byte limit, evict lower-value batches before higher-value batches so important telemetry survives longer during collector outages.

**Architecture:** Keep the policy inside `DiskTelemetrySpool`, because it owns on-disk batch files and already enforces max bytes. Continue preserving the newest just-failed batch when possible, but rank older eviction candidates by the highest priority record they contain: `Debug`, `Routine`, `Normal`, `Important`, then `Critical`. Within the same priority, evict oldest files first.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, existing `DiskTelemetrySpool`, existing `TelemetryRecord` and `TelemetryPriority`.

---

## Target File Structure

- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
  - Replace oldest-only byte eviction with priority-aware candidate ordering.
  - Read ready batch priorities when ranking ready files.
  - Treat unreadable sidecar files as lowest-priority eviction candidates.
  - Preserve existing behavior that deletes the newest batch and throws if it alone cannot fit.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`
  - Add regression tests for routine-before-important eviction and oldest-first behavior within the same priority.

## Non-Goals For This Slice

- No record-level splitting of mixed-priority batches.
- No gauge coalescing or time bucket aggregation.
- No drop counter export or UI changes.
- No changes to OTLP export or recovery backoff.

## Task 1: Priority-Aware Max-Byte Eviction

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [x] **Step 1: Write a failing test for lower-priority eviction**

Add this test to `DiskTelemetrySpoolTests`:

```csharp
[Fact]
public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_DropsRoutineBatchBeforeOlderImportantBatch()
{
    var spoolPath = Path.Combine(root, "spool");
    var maxBytes = 9_000;
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(7));

    await spool.AppendBatchAsync(new[] { CreateLargeRecord("important-old", TelemetryPriority.Important) }, CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("routine-newer", TelemetryPriority.Routine) }, CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("critical-newest", TelemetryPriority.Critical) }, CancellationToken.None);

    var batches = await spool.ReadBatchesAsync(CancellationToken.None);

    batches.SelectMany(batch => batch.Records).Select(record => record.Name)
        .Should().Equal("important-old", "critical-newest");
    TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(maxBytes);
}
```

Change the existing helper:

```csharp
private static TelemetryRecord CreateLargeRecord(string name, TelemetryPriority priority = TelemetryPriority.Routine) =>
    TelemetryRecord.Health(
        DateTimeOffset.UtcNow,
        "test",
        name,
        priority,
        new Dictionary<string, object?> { ["payload"] = new string('x', 3_000) });
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected before implementation: the new test fails because current eviction deletes the oldest important batch.

- [x] **Step 2: Implement priority-aware eviction candidates**

In `DiskTelemetrySpool.EnforceMaxBytes`, keep `newestReadyPath` protected until no other file can make room. Build candidates from `EnumerateSpoolFiles()` and sort by:

1. Whether the file is the newest protected file; protected newest last.
2. The batch priority rank where lower numeric `TelemetryPriority` is lower value.
3. File name ordinal, preserving oldest-first behavior within equal priority.

For ready files, read the batch and use the maximum `record.Priority` in the batch. For unreadable files and non-ready sidecar files, use `TelemetryPriority.Debug`.

Do not throw while ranking if an older file cannot be read. It is better to evict unreadable/spurious spool files than block new outage persistence.

- [x] **Step 3: Run the focused spool test suite**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected: all `DiskTelemetrySpoolTests` pass.

- [x] **Step 4: Run related pipeline tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~DiskTelemetrySpoolTests|FullyQualifiedName~DurableTelemetryExporterTests|FullyQualifiedName~TelemetryPipelineTests" -v:minimal
```

Expected: related pipeline tests pass. If the known macOS certificate/keychain test appears in a broader run, report it separately and do not conflate it with this slice.

- [x] **Step 5: Commit the slice**

```bash
git add src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs \
  tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs \
  docs/superpowers/plans/2026-06-21-disk-spool-priority-eviction.md
git commit -m "Prioritize disk spool eviction by telemetry value"
```

## Self-Review

- Spec coverage: covers the v1 requirement that spool-full conditions apply a drop policy and retain safety/fault/workflow-failure telemetry longer than routine/debug data.
- Known remaining gaps after this plan: record-level batch splitting, gauge coalescing, explicit drop counters, recovery flush rate, and richer buffer status UI.
- Placeholder scan: no placeholders or deferred implementation steps are present.
- Type consistency: uses existing `TelemetryPriority`, `TelemetryRecord`, and `DiskTelemetrySpool`.
