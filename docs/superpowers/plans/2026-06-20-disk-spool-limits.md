# NinaOtel Disk Spool Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bound the disk-on-failure spool by max age and max bytes so collector outages cannot grow disk usage without limit.

**Architecture:** Keep limits inside `DiskTelemetrySpool`, because that is the only component that owns on-disk batch files. Apply max-age pruning before each append, write the new batch atomically, then apply max-byte eviction oldest-first across spool sidecar files. Preserve the newest batch when possible, but if the new batch alone exceeds the configured max byte limit, delete it and throw so the pipeline records an explicit drop.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, existing `BufferOptions`, existing `DiskTelemetrySpool` and `DurableTelemetryExporter`.

---

## Target File Structure

- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
  - Add constructor parameters for `maxBytes` and `maxAge`.
  - Add max-age pruning for `*.ready`, `*.invalid`, and `*.sent` spool files.
  - Add max-byte eviction for the same file set.
  - Keep constructor lazy: no directory creation or enumeration in constructor.
- Modify: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
  - Pass `options.Buffer.MaxSpoolBytes` and `options.Buffer.MaxSpoolAge` into `DiskTelemetrySpool`.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`
  - Add limit tests using small temporary spool directories.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs`
  - Update constructor calls only if required by the production constructor change.
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`
  - Update constructor calls only if required by the production constructor change.

## Non-Goals For This Slice

- No priority-aware disk eviction.
- No metric coalescing.
- No UI for max bytes or max age.
- No background recovery loop.
- No current buffer-mode status UI.
- No changes to the OTLP exporter.

## Task 1: Spool Limit Constructor And Defaults

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [ ] **Step 1: Write failing constructor validation tests**

Add tests to `DiskTelemetrySpoolTests`:

```csharp
[Fact]
public void Constructor_WithLimits_DoesNotCreateSpoolDirectory()
{
    var spoolPath = Path.Combine(root, "spool");

    _ = new DiskTelemetrySpool(spoolPath, maxBytes: 1024, maxAge: TimeSpan.FromDays(1));

    Directory.Exists(spoolPath).Should().BeFalse();
}

[Theory]
[InlineData(0)]
[InlineData(-1)]
public void Constructor_WhenMaxBytesIsNotPositive_ThrowsArgumentOutOfRangeException(long maxBytes)
{
    var spoolPath = Path.Combine(root, "spool");

    Action create = () => _ = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(1));

    create.Should().Throw<ArgumentOutOfRangeException>()
        .WithParameterName("maxBytes");
}

[Fact]
public void Constructor_WhenMaxAgeIsNotPositive_ThrowsArgumentOutOfRangeException()
{
    var spoolPath = Path.Combine(root, "spool");

    Action create = () => _ = new DiskTelemetrySpool(spoolPath, maxBytes: 1024, maxAge: TimeSpan.Zero);

    create.Should().Throw<ArgumentOutOfRangeException>()
        .WithParameterName("maxAge");
}
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DiskTelemetrySpoolTests --no-restore -v:normal
```

Expected before implementation: compile fails because the constructor overload does not exist.

- [ ] **Step 2: Implement constructor overload and defaults**

Change `DiskTelemetrySpool` so existing test and production call sites can still use the default constructor:

```csharp
private const long DefaultMaxBytes = 1L * 1024 * 1024 * 1024;
private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

private readonly long maxBytes;
private readonly TimeSpan maxAge;

public DiskTelemetrySpool(string spoolPath)
    : this(spoolPath, DefaultMaxBytes, DefaultMaxAge)
{
}

public DiskTelemetrySpool(string spoolPath, long maxBytes, TimeSpan maxAge)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(spoolPath);
    if (maxBytes <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(maxBytes), "Max bytes must be positive.");
    }

    if (maxAge <= TimeSpan.Zero)
    {
        throw new ArgumentOutOfRangeException(nameof(maxAge), "Max age must be positive.");
    }

    this.spoolPath = ExpandLocalAppData(spoolPath);
    this.maxBytes = maxBytes;
    this.maxAge = maxAge;
}
```

- [ ] **Step 3: Run core build**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
```

Expected locally: build succeeds, or report the exact restore/build failure if NuGet blocks it.

## Task 2: Max-Age Pruning

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [ ] **Step 1: Write failing max-age test**

Add:

```csharp
[Fact]
public async Task AppendBatchAsync_PrunesFilesOlderThanMaxAge()
{
    var spoolPath = Path.Combine(root, "spool");
    Directory.CreateDirectory(spoolPath);
    var expiredReady = Path.Combine(spoolPath, "0000000000000000001-old.ready");
    var expiredInvalid = Path.Combine(spoolPath, "0000000000000000002-old.invalid");
    var recentReady = Path.Combine(spoolPath, "0000000000000000003-recent.ready");
    await File.WriteAllTextAsync(expiredReady, "{}");
    await File.WriteAllTextAsync(expiredInvalid, "{}");
    await File.WriteAllTextAsync(recentReady, "{}");
    File.SetLastWriteTimeUtc(expiredReady, DateTime.UtcNow.AddDays(-8));
    File.SetLastWriteTimeUtc(expiredInvalid, DateTime.UtcNow.AddDays(-8));
    File.SetLastWriteTimeUtc(recentReady, DateTime.UtcNow);
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 1024 * 1024, maxAge: TimeSpan.FromDays(7));

    await spool.AppendBatchAsync(new[] { CreateHealthRecord("new") }, CancellationToken.None);

    File.Exists(expiredReady).Should().BeFalse();
    File.Exists(expiredInvalid).Should().BeFalse();
    File.Exists(recentReady).Should().BeTrue();
    Directory.GetFiles(spoolPath, "*.ready").Should().HaveCount(2);
}
```

If there is no helper yet, add:

```csharp
private static TelemetryRecord CreateHealthRecord(string name) =>
    TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", name, TelemetryPriority.Routine);
```

- [ ] **Step 2: Implement max-age pruning**

In `AppendBatchAsync`, before creating the new temporary file, call:

```csharp
PruneExpiredFiles(DateTimeOffset.UtcNow);
```

Add:

```csharp
private void PruneExpiredFiles(DateTimeOffset now)
{
    if (!Directory.Exists(spoolPath))
    {
        return;
    }

    var cutoff = now.UtcDateTime - maxAge;
    foreach (var file in EnumerateSpoolFiles())
    {
        if (File.GetLastWriteTimeUtc(file) < cutoff)
        {
            TryDelete(file);
        }
    }
}

private IEnumerable<string> EnumerateSpoolFiles()
{
    if (!Directory.Exists(spoolPath))
    {
        return Array.Empty<string>();
    }

    return Directory.EnumerateFiles(spoolPath, "*.ready")
        .Concat(Directory.EnumerateFiles(spoolPath, "*.invalid"))
        .Concat(Directory.EnumerateFiles(spoolPath, "*.sent"));
}
```

- [ ] **Step 3: Run focused tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected in GitHub CI: tests pass. Locally, document `NU1301` if restore fails.

## Task 3: Max-Byte Eviction

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [ ] **Step 1: Write failing oldest-first byte eviction test**

Add:

```csharp
[Fact]
public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_DeletesOldestFilesFirst()
{
    var spoolPath = Path.Combine(root, "spool");
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 900, maxAge: TimeSpan.FromDays(7));
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("first") }, CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("second") }, CancellationToken.None);
    await spool.AppendBatchAsync(new[] { CreateLargeRecord("third") }, CancellationToken.None);

    var batches = await spool.ReadBatchesAsync(CancellationToken.None);

    batches.SelectMany(batch => batch.Records).Select(record => record.Name)
        .Should().Equal("second", "third");
}
```

Add helper:

```csharp
private static TelemetryRecord CreateLargeRecord(string name) =>
    TelemetryRecord.Log(
        DateTimeOffset.UtcNow,
        "test",
        TelemetrySeverity.Information,
        new string('x', 250),
        TelemetryPriority.Routine,
        new Dictionary<string, object?> { ["name"] = name });
```

- [ ] **Step 2: Write failing too-large-new-batch test**

Add:

```csharp
[Fact]
public async Task AppendBatchAsync_WhenNewBatchAloneExceedsMaxBytes_DeletesItAndThrowsIOException()
{
    var spoolPath = Path.Combine(root, "spool");
    var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 32, maxAge: TimeSpan.FromDays(7));

    Func<Task> append = async () => await spool.AppendBatchAsync(new[] { CreateLargeRecord("oversized") }, CancellationToken.None);

    await append.Should().ThrowAsync<IOException>();
    Directory.GetFiles(spoolPath, "*.ready").Should().BeEmpty();
}
```

- [ ] **Step 3: Implement max-byte enforcement**

After `File.Move(temporaryPath, readyPath);`, call:

```csharp
EnforceMaxBytes(readyPath);
```

Add:

```csharp
private void EnforceMaxBytes(string newestReadyPath)
{
    var files = EnumerateSpoolFiles()
        .Select(path => new FileInfo(path))
        .Where(file => file.Exists)
        .OrderBy(file => file.FullName, StringComparer.Ordinal)
        .ToList();

    var totalBytes = files.Sum(file => file.Length);
    foreach (var file in files)
    {
        if (totalBytes <= maxBytes)
        {
            return;
        }

        if (string.Equals(file.FullName, newestReadyPath, StringComparison.Ordinal))
        {
            continue;
        }

        var length = file.Length;
        TryDelete(file.FullName);
        if (!File.Exists(file.FullName))
        {
            totalBytes -= length;
        }
    }

    if (totalBytes > maxBytes)
    {
        TryDelete(newestReadyPath);
        throw new IOException($"Telemetry spool batch '{newestReadyPath}' exceeds max spool bytes '{maxBytes}'.");
    }
}
```

- [ ] **Step 4: Run focused tests and core build**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
```

Expected in GitHub CI: tests pass. Locally, document `NU1301` if restore fails.

## Task 4: Plugin Wiring

**Files:**
- Modify: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`

- [ ] **Step 1: Wire buffer limits into the spool**

Change the plugin construction from:

```csharp
new DiskTelemetrySpool(options.Buffer.SpoolPath)
```

to:

```csharp
new DiskTelemetrySpool(
    options.Buffer.SpoolPath,
    options.Buffer.MaxSpoolBytes,
    options.Buffer.MaxSpoolAge)
```

- [ ] **Step 2: Run plugin build**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj --no-restore -v:minimal
```

Expected in GitHub CI: build passes. Locally, document `NU1301` if restore fails.

## Verification

Run before commit:

```bash
git diff --check
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore -v:minimal
```

Expected:
- `git diff --check`: exit 0.
- Core build: exit 0, allowing existing NuGet warnings.
- Test build: GitHub CI must pass; local may fail with `NU1301`.

## Self-Review

- Spec coverage: covers max spool bytes and max spool age from the v1 buffering requirements.
- Known remaining gaps after this plan: priority-aware drop policy, gauge coalescing, recovery flush rate, buffer status UI, and background recovery.
- Placeholder scan: no TODO/TBD placeholders.
- Type consistency: uses existing `BufferOptions.MaxSpoolBytes`, `BufferOptions.MaxSpoolAge`, `TelemetryRecord`, and `DiskTelemetrySpool`.
