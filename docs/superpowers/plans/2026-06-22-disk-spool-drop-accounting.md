# NinaOtel Disk Spool Drop Accounting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Count records dropped by disk spool eviction/expiry and surface that count through collector health and the plugin debug UI.

**Architecture:** Keep drop accounting inside `DiskTelemetrySpool`, because it is the component that deletes queued spool files for max-byte and max-age enforcement. `DurableTelemetryExporter` should treat spool stats as the source of truth and pass the dropped count into `CollectorHealthSnapshot`. The existing options view model should display dropped records even after the queue has drained.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, existing `DiskTelemetrySpool`, `DurableTelemetryExporter`, `CollectorHealthSnapshot`, and `NinaOtelOptionsViewModel`.

---

## File Structure

- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
  - Add an in-process cumulative dropped-record counter.
  - Count readable `.ready` records when files are deleted by max-age pruning or max-byte eviction.
  - Add `DroppedRecords` to `Stats`.
- Modify: `src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs`
  - Pass `Stats.DroppedRecords` to unhealthy, recovering, and healthy collector health snapshots.
  - Clamp the long spool counter to the existing `CollectorHealthSnapshot` int field.
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Display dropped records even when queue counts are zero.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`
  - Cover max-byte eviction, max-age pruning, and oversized newest batch drop counts.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs`
  - Cover health reporting of dropped records when failure spooling evicts existing telemetry.
- Modify: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Cover UI debug text for dropped records with no active queue.

## Task 1: Spool Drop Counter

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [x] **Step 1: Write failing tests for max-byte, max-age, and oversized-newest drops**

Add these tests to `DiskTelemetrySpoolTests` near the existing max-byte and stats tests:

```csharp
[Fact]
public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_CountsDroppedRecordsForEvictedReadyBatches()
{
    var spoolPath = Path.Combine(root, "spool");
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 9_000, maxAge: TimeSpan.FromDays(7));

    await spool.AppendBatchAsync(
        new[]
        {
            CreateLargeRecord("first-a", TelemetryPriority.Routine, payloadSize: 1_200),
            CreateLargeRecord("first-b", TelemetryPriority.Routine, payloadSize: 1_200),
        },
        CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("second") }, CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("third") }, CancellationToken.None);

    var stats = await spool.GetStatsAsync(CancellationToken.None);

    stats.DroppedRecords.Should().Be(2);
    (await spool.ReadBatchesAsync(CancellationToken.None))
        .SelectMany(batch => batch.Records)
        .Select(record => record.Name)
        .Should().Equal("second", "third");
}

[Fact]
public async Task AppendBatchAsync_WhenPruningExpiredReadyFile_CountsDroppedRecords()
{
    var spoolPath = Path.Combine(root, "spool");
    var firstSpool = new DiskTelemetrySpool(spoolPath, maxBytes: 1024 * 1024, maxAge: TimeSpan.FromDays(7));
    await firstSpool.AppendBatchAsync(
        new[]
        {
            CreateHealthRecord("expired-a"),
            CreateHealthRecord("expired-b"),
        },
        CancellationToken.None);
    foreach (var readyFile in Directory.GetFiles(spoolPath, "*.ready"))
    {
        File.SetLastWriteTimeUtc(readyFile, DateTime.UtcNow.AddDays(-8));
    }

    var pruningSpool = new DiskTelemetrySpool(spoolPath, maxBytes: 1024 * 1024, maxAge: TimeSpan.FromDays(7));
    await pruningSpool.AppendBatchAsync(new[] { CreateHealthRecord("new") }, CancellationToken.None);

    var stats = await pruningSpool.GetStatsAsync(CancellationToken.None);

    stats.DroppedRecords.Should().Be(2);
    (await pruningSpool.ReadBatchesAsync(CancellationToken.None))
        .SelectMany(batch => batch.Records)
        .Select(record => record.Name)
        .Should().Equal("new");
}

[Fact]
public async Task AppendBatchAsync_WhenNewBatchAloneExceedsMaxBytes_CountsDroppedRecordsBeforeThrowing()
{
    var spoolPath = Path.Combine(root, "spool");
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 32, maxAge: TimeSpan.FromDays(7));

    Func<Task> append = async () => await spool.AppendBatchAsync(
        new[] { CreateLargeRecord("oversized") },
        CancellationToken.None);

    await append.Should().ThrowAsync<IOException>();
    var stats = await spool.GetStatsAsync(CancellationToken.None);
    stats.DroppedRecords.Should().Be(1);
}
```

- [x] **Step 2: Run tests and verify they fail for missing `DroppedRecords`**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected: compile failure because `DiskTelemetrySpool.Stats` does not expose `DroppedRecords`.

- [x] **Step 3: Implement spool-side drop accounting**

In `DiskTelemetrySpool`, add:

```csharp
private long droppedRecords;
```

Update `Stats` to:

```csharp
public sealed record Stats(
    int QueuedBatches,
    int QueuedRecords,
    long QueuedBytes,
    DateTimeOffset? OldestQueuedTimestamp,
    long DroppedRecords)
{
    public static Stats Empty { get; } = new(0, 0, 0, null, 0);
}
```

Return `Interlocked.Read(ref droppedRecords)` from `GetStatsAsync`.

Add helper methods:

```csharp
private void TryDeleteDroppedBatch(string path)
{
    var droppedRecordCount = CountReadyRecordsForDrop(path);
    TryDelete(path);
    if (!File.Exists(path) && droppedRecordCount > 0)
    {
        Interlocked.Add(ref droppedRecords, droppedRecordCount);
    }
}

private static int CountReadyRecordsForDrop(string path)
{
    if (!path.EndsWith(".ready", StringComparison.Ordinal))
    {
        return 0;
    }

    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16 * 1024);
        var dto = JsonSerializer.Deserialize<BatchDto>(stream, JsonOptions);
        return dto?.Records.Count ?? 0;
    }
    catch
    {
        return 0;
    }
}
```

Use `TryDeleteDroppedBatch` for `.ready` files deleted by `PruneExpiredFiles`, candidate eviction in `EnforceMaxBytes`, and deletion of `newestReadyPath` before throwing. Keep `TryDelete` for `.tmp`, `.invalid`, `.sent`, and cleanup that does not represent queued telemetry.

- [x] **Step 4: Run DiskTelemetrySpool tests and verify they pass**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected: all `DiskTelemetrySpoolTests` pass.

## Task 2: Health And UI Propagation

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs`
- Modify: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`

- [x] **Step 1: Write failing exporter health test**

Add to `DurableTelemetryExporterTests`:

```csharp
[Fact]
public async Task ExportAsync_WhenSpoolingEvictsRecords_ReportsDroppedRecordsInHealthSnapshot()
{
    var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"), maxBytes: 6_000, maxAge: TimeSpan.FromDays(7));
    var reporter = new RecordingHealthReporter();
    using var exporter = CreateExporter(
        new ThrowingExporter(new InvalidOperationException("collector unavailable")),
        spool,
        reportHealth: reporter.Report);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("queued") }, CancellationToken.None);
    var live = CreateLargeRecord("live");

    await exporter.ExportAsync(new[] { live }, CancellationToken.None);

    reporter.Snapshots.Should().ContainSingle();
    reporter.Snapshots[0].DroppedRecords.Should().Be(1);
    reporter.Snapshots[0].QueuedRecords.Should().Be(1);
}
```

Add this helper to `DurableTelemetryExporterTests`:

```csharp
private static TelemetryRecord CreateLargeRecord(string name, int payloadSize = 3_000) =>
    TelemetryRecord.Health(
        DateTimeOffset.UtcNow,
        "test",
        name,
        TelemetryPriority.Routine,
        new Dictionary<string, object?> { ["payload"] = new string('x', payloadSize) });
```

- [x] **Step 2: Write failing UI debug text test**

Add to `NinaOtelOptionsViewModelTests`:

```csharp
[Fact]
public void UpdateCollectorHealth_WhenDroppedRecordsHaveNoQueue_ShowsDroppedDebugInfo()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);

    viewModel.UpdateCollectorHealth(CollectorHealthSnapshot.Healthy(
        new Uri("http://collector.local:4317/"),
        OtlpProtocol.Grpc,
        exportedRecords: 0,
        checkedAt: new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero),
        droppedRecords: 3));

    viewModel.CollectorHealthDebugInfo.Should().Contain("Dropped: 3 record(s)");
}
```

- [x] **Step 3: Run tests and verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter FullyQualifiedName~DurableTelemetryExporterTests -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter FullyQualifiedName~NinaOtelOptionsViewModelTests -v:minimal
```

Expected: exporter test fails with `DroppedRecords` still zero; UI test fails because `Dropped:` is omitted when queue is empty.

- [x] **Step 4: Propagate dropped records into collector health snapshots**

In `DurableTelemetryExporter`, add:

```csharp
private static int ToSnapshotDroppedRecords(long droppedRecords) =>
    droppedRecords > int.MaxValue ? int.MaxValue : (int)droppedRecords;
```

Pass `droppedRecords: ToSnapshotDroppedRecords(stats.DroppedRecords)` to:

- `CollectorHealthSnapshot.Healthy` in `ReportAfterReplaySuccessAsync`.
- `CollectorHealthSnapshot.Unhealthy` for `Recovering` in `ReportAfterReplaySuccessAsync`.
- `CollectorHealthSnapshot.Unhealthy` in `ReportUnhealthyAsync`.

- [x] **Step 5: Fix UI debug formatting**

Change `FormatQueueDebugInfo` so it only returns empty when `QueuedRecords`, `QueuedBytes`, `OldestQueuedTimestamp`, and `DroppedRecords` are all empty. Build the queue text conditionally so a dropped-only snapshot produces `Dropped: N record(s);`.

- [x] **Step 6: Run focused core and plugin tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~DiskTelemetrySpoolTests|FullyQualifiedName~DurableTelemetryExporterTests" -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter FullyQualifiedName~NinaOtelOptionsViewModelTests -v:minimal
```

Expected: all selected tests pass.

## Task 3: Final Verification

**Files:**
- Verify changed files only; no new source files.

- [x] **Step 1: Run related pipeline test set**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~DiskTelemetrySpoolTests|FullyQualifiedName~DurableTelemetryExporterTests|FullyQualifiedName~TelemetryPipelineTests" -v:minimal
```

Expected: all selected tests pass.

- [x] **Step 2: Run plugin options test set**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter FullyQualifiedName~NinaOtelOptionsViewModelTests -v:minimal
```

Expected: all selected tests pass.

- [x] **Step 3: Check formatting whitespace**

Run:

```bash
git diff --check
```

Expected: no output and exit code 0.

- [x] **Step 4: Commit**

Run:

```bash
git add src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs \
  src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs \
  src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs \
  tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs \
  tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs \
  tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs \
  docs/superpowers/plans/2026-06-22-disk-spool-drop-accounting.md
git commit -m "Track disk spool dropped records"
```

Expected: one commit containing the implementation, tests, and plan.

Local verification note: the prescribed `dotnet test --no-restore` commands returned exit code 0 with no test output in this worktree, so they were executed but did not provide useful pass/fail evidence. A diagnostic restore/build attempt failed with `NU1301` against `https://api.nuget.org/v3/index.json`; `git diff --check` passed.

## Self-Review

- Spec coverage: The plan implements “Spool full: apply drop policy and emit drop counters” and status-view dropped-record visibility.
- Placeholder scan: No TBD/TODO/fill-in steps remain.
- Type consistency: `DiskTelemetrySpool.Stats.DroppedRecords` is `long`; `CollectorHealthSnapshot.DroppedRecords` remains `int` with explicit clamping in `DurableTelemetryExporter`.
