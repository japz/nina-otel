# NinaOtel Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first working foundation slice: solution scaffold, shared telemetry contracts, non-blocking in-memory pipeline, add-on host, minimal NINA plugin shell, options skeleton, and tests.

**Architecture:** Create small focused projects: `NinaOtel.Abstractions` for add-on/publish contracts, `NinaOtel.Core` for pipeline/options/add-on hosting, and `NinaOtel.Plugin` for NINA lifecycle/UI integration. This plan does not implement real NINA collectors, OTLP export, disk spool, auth, or first-party add-ons; it creates the stable boundaries those slices will use.

**Tech Stack:** C# 12, .NET 8, `net8.0-windows`, NINA.Plugin 3.2.0.9001, WPF resource dictionary, xUnit, FluentAssertions.

---

## Scope Split

This spec is intentionally split into independently testable plans:

- Plan 1: Foundation core, contracts, non-blocking add-on host, plugin shell. This document.
- Plan 2: OTLP exporter, auth, memory-first/disk-on-failure buffering.
- Plan 3: Core NINA collectors for equipment, image, workflow, and NINA log signal.
- Plan 4: PHD2, Target Scheduler, and Night Summary add-ons.
- Plan 5: OnStepX/JTW Trident add-on.

Plan 1 must leave the repository buildable and testable.

## Target File Structure

- `NinaOtel.sln`
  - Solution file.
- `Directory.Build.props`
  - Shared nullable/implicit usings/lang version settings.
- `src/NinaOtel.Abstractions/NinaOtel.Abstractions.csproj`
  - Shared contracts that add-ons can reference without pulling in NINA or OTel SDK.
- `src/NinaOtel.Abstractions/Telemetry/TelemetryRecord.cs`
  - Normalized telemetry records and priority/signal enums.
- `src/NinaOtel.Abstractions/Telemetry/ITelemetrySink.cs`
  - Non-blocking publish contract.
- `src/NinaOtel.Abstractions/Addons/AddonContracts.cs`
  - Add-on metadata, context, and lifecycle interfaces.
- `src/NinaOtel.Core/NinaOtel.Core.csproj`
  - Core pipeline, options models, add-on host.
- `src/NinaOtel.Core/Options/NinaOtelOptions.cs`
  - Core, OTLP, buffer, and add-on option models.
- `src/NinaOtel.Core/Pipeline/TelemetryPipeline.cs`
  - Bounded in-memory pipeline implementing `ITelemetrySink`.
- `src/NinaOtel.Core/Pipeline/ITelemetryExporter.cs`
  - Internal exporter abstraction used by tests and future OTLP implementation.
- `src/NinaOtel.Core/Addons/AddonHost.cs`
  - Non-blocking add-on lifecycle manager.
- `src/NinaOtel.Core/Addons/AddonContext.cs`
  - Runtime context passed to add-ons.
- `src/NinaOtel.Plugin/NinaOtel.Plugin.csproj`
  - NINA plugin assembly.
- `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
  - Minimal MEF-exported NINA plugin shell.
- `src/NinaOtel.Plugin/Options/Options.xaml`
  - NINA options resource dictionary.
- `src/NinaOtel.Plugin/Options/Options.xaml.cs`
  - Exported WPF resource dictionary type.
- `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Minimal options/status view model.
- `tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj`
  - Core test project.
- `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`
  - Bounded non-blocking pipeline tests.
- `tests/NinaOtel.Core.Tests/Addons/AddonHostTests.cs`
  - Add-on startup/timeout/isolation tests.

## Task 1: Create Solution And Projects

**Files:**
- Create: `NinaOtel.sln`
- Create: `Directory.Build.props`
- Create: `src/NinaOtel.Abstractions/NinaOtel.Abstractions.csproj`
- Create: `src/NinaOtel.Core/NinaOtel.Core.csproj`
- Create: `src/NinaOtel.Plugin/NinaOtel.Plugin.csproj`
- Create: `tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj`

- [ ] **Step 1: Create the solution and project directories**

Run:

```bash
mkdir -p src/NinaOtel.Abstractions src/NinaOtel.Core src/NinaOtel.Plugin tests/NinaOtel.Core.Tests
dotnet new sln -n NinaOtel
dotnet new classlib -n NinaOtel.Abstractions -o src/NinaOtel.Abstractions
dotnet new classlib -n NinaOtel.Core -o src/NinaOtel.Core
dotnet new classlib -n NinaOtel.Plugin -o src/NinaOtel.Plugin
dotnet new xunit -n NinaOtel.Core.Tests -o tests/NinaOtel.Core.Tests
```

Expected: each command exits `0` and creates the listed projects.

- [ ] **Step 2: Replace `Directory.Build.props`**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Replace `src/NinaOtel.Abstractions/NinaOtel.Abstractions.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Replace `src/NinaOtel.Core/NinaOtel.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\NinaOtel.Abstractions\NinaOtel.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Replace `src/NinaOtel.Plugin/NinaOtel.Plugin.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <OutputType>Library</OutputType>
    <UseWPF>true</UseWPF>
    <ImportWindowsDesktopTargets>true</ImportWindowsDesktopTargets>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <RootNamespace>NinaOtel.Plugin</RootNamespace>
    <AssemblyName>NinaOtel.Plugin</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\NinaOtel.Abstractions\NinaOtel.Abstractions.csproj" />
    <ProjectReference Include="..\NinaOtel.Core\NinaOtel.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="NINA.Plugin" Version="3.2.0.9001" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Replace `tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\NinaOtel.Abstractions\NinaOtel.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\NinaOtel.Core\NinaOtel.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: Add projects to solution**

Run:

```bash
dotnet sln NinaOtel.sln add src/NinaOtel.Abstractions/NinaOtel.Abstractions.csproj
dotnet sln NinaOtel.sln add src/NinaOtel.Core/NinaOtel.Core.csproj
dotnet sln NinaOtel.sln add src/NinaOtel.Plugin/NinaOtel.Plugin.csproj
dotnet sln NinaOtel.sln add tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj
```

Expected: each command reports that the project was added.

- [ ] **Step 8: Remove template classes**

Run:

```bash
rm -f src/NinaOtel.Abstractions/Class1.cs src/NinaOtel.Core/Class1.cs src/NinaOtel.Plugin/Class1.cs tests/NinaOtel.Core.Tests/UnitTest1.cs
```

Expected: command exits `0`.

- [ ] **Step 9: Restore and build**

Run:

```bash
dotnet restore NinaOtel.sln
dotnet build NinaOtel.sln --no-restore
```

Expected: restore succeeds and build succeeds.

- [ ] **Step 10: Commit scaffold**

```bash
git add NinaOtel.sln Directory.Build.props src tests
git commit -m "chore: scaffold NinaOtel solution"
```

## Task 2: Define Shared Telemetry Contracts

**Files:**
- Create: `src/NinaOtel.Abstractions/Telemetry/TelemetryRecord.cs`
- Create: `src/NinaOtel.Abstractions/Telemetry/ITelemetrySink.cs`
- Create: `src/NinaOtel.Abstractions/Addons/AddonContracts.cs`
- Test: `tests/NinaOtel.Core.Tests/Contracts/TelemetryContractTests.cs`

- [ ] **Step 1: Write contract tests**

Create `tests/NinaOtel.Core.Tests/Contracts/TelemetryContractTests.cs`:

```csharp
using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Tests.Contracts;

public sealed class TelemetryContractTests
{
    [Fact]
    public void LogRecord_CarriesSourcePriorityAndAttributes()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.Parse("2026-06-14T01:02:03Z"),
            "nina",
            TelemetrySeverity.Error,
            "camera failed",
            TelemetryPriority.Critical,
            new Dictionary<string, object?>
            {
                ["source.file"] = "CameraVM.cs",
                ["source.line"] = 149,
            });

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Source.Should().Be("nina");
        record.Priority.Should().Be(TelemetryPriority.Critical);
        record.Attributes["source.file"].Should().Be("CameraVM.cs");
        record.Attributes["source.line"].Should().Be(149);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter TelemetryContractTests
```

Expected: FAIL with missing namespace/type errors for `NinaOtel.Abstractions.Telemetry`.

- [ ] **Step 3: Add telemetry records**

Create `src/NinaOtel.Abstractions/Telemetry/TelemetryRecord.cs`:

```csharp
namespace NinaOtel.Abstractions.Telemetry;

public enum TelemetrySignal
{
    Metric,
    Log,
    Span,
    Health,
}

public enum TelemetryPriority
{
    Debug = 0,
    Routine = 1,
    Normal = 2,
    Important = 3,
    Critical = 4,
}

public enum TelemetrySeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

public enum SpanEventKind
{
    Start,
    Event,
    Stop,
}

public sealed record TelemetryRecord(
    TelemetrySignal Signal,
    DateTimeOffset Timestamp,
    string Source,
    string Name,
    TelemetryPriority Priority,
    IReadOnlyDictionary<string, object?> Attributes,
    double? NumericValue = null,
    string? Body = null,
    TelemetrySeverity? Severity = null,
    SpanEventKind? SpanKind = null,
    string? SpanId = null,
    string? ParentSpanId = null)
{
    public static TelemetryRecord Metric(
        DateTimeOffset timestamp,
        string source,
        string name,
        double value,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Metric,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes,
            NumericValue: value);

    public static TelemetryRecord Log(
        DateTimeOffset timestamp,
        string source,
        TelemetrySeverity severity,
        string body,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Log,
            timestamp,
            source,
            "log",
            priority,
            attributes ?? EmptyAttributes,
            Body: body,
            Severity: severity);

    public static TelemetryRecord Health(
        DateTimeOffset timestamp,
        string source,
        string name,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Health,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes);

    public static TelemetryRecord Span(
        DateTimeOffset timestamp,
        string source,
        string name,
        SpanEventKind kind,
        string spanId,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string? parentSpanId = null)
        => new(
            TelemetrySignal.Span,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes,
            SpanKind: kind,
            SpanId: spanId,
            ParentSpanId: parentSpanId);

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes =
        new Dictionary<string, object?>();
}
```

- [ ] **Step 4: Add sink interface**

Create `src/NinaOtel.Abstractions/Telemetry/ITelemetrySink.cs`:

```csharp
namespace NinaOtel.Abstractions.Telemetry;

public interface ITelemetrySink
{
    bool TryPublish(TelemetryRecord record);
}
```

- [ ] **Step 5: Add add-on contracts**

Create `src/NinaOtel.Abstractions/Addons/AddonContracts.cs`:

```csharp
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Abstractions.Addons;

public sealed record AddonMetadata(
    string Id,
    string DisplayName,
    Version Version,
    string SourceType);

public sealed record AddonValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static AddonValidationResult Success { get; } = new(true, Array.Empty<string>());

    public static AddonValidationResult Failure(params string[] errors)
        => new(false, errors);
}

public interface IAddonContext
{
    ITelemetrySink Sink { get; }
    TimeProvider TimeProvider { get; }
    CancellationToken ShutdownToken { get; }
    void ReportHealth(string addonId, string status, string message, TelemetryPriority priority);
}

public interface ITelemetryAddon
{
    AddonMetadata Metadata { get; }
    AddonValidationResult Validate();
    Task StartAsync(IAddonContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 6: Run contract tests**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter TelemetryContractTests
```

Expected: PASS.

- [ ] **Step 7: Commit contracts**

```bash
git add src/NinaOtel.Abstractions tests/NinaOtel.Core.Tests/Contracts
git commit -m "feat: add telemetry and addon contracts"
```

## Task 3: Add Core Options Models

**Files:**
- Create: `src/NinaOtel.Core/Options/NinaOtelOptions.cs`
- Test: `tests/NinaOtel.Core.Tests/Options/NinaOtelOptionsTests.cs`

- [ ] **Step 1: Write options tests**

Create `tests/NinaOtel.Core.Tests/Options/NinaOtelOptionsTests.cs`:

```csharp
using FluentAssertions;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Tests.Options;

public sealed class NinaOtelOptionsTests
{
    [Fact]
    public void CreateDefault_UsesMemoryFirstDiskOnFailureDefaults()
    {
        var options = NinaOtelOptions.CreateDefault();

        options.Buffer.DiskOnFailureEnabled.Should().BeTrue();
        options.Buffer.SpoolsDuringHealthyExport.Should().BeFalse();
        options.Buffer.MaxSpoolBytes.Should().Be(1L * 1024 * 1024 * 1024);
        options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(7));
        options.Otlp.Protocol.Should().Be(OtlpProtocol.Grpc);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter NinaOtelOptionsTests
```

Expected: FAIL with missing `NinaOtel.Core.Options`.

- [ ] **Step 3: Add options models**

Create `src/NinaOtel.Core/Options/NinaOtelOptions.cs`:

```csharp
namespace NinaOtel.Core.Options;

public enum OtlpProtocol
{
    Grpc,
    HttpProtobuf,
}

public sealed record NinaOtelOptions
{
    public OtlpOptions Otlp { get; init; } = new();
    public BufferOptions Buffer { get; init; } = new();
    public CoreTelemetryOptions CoreTelemetry { get; init; } = new();
    public IReadOnlyDictionary<string, AddonOptions> Addons { get; init; } =
        new Dictionary<string, AddonOptions>();

    public static NinaOtelOptions CreateDefault() => new();
}

public sealed record OtlpOptions
{
    public Uri Endpoint { get; init; } = new("http://localhost:4317");
    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.Grpc;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();
    public OtlpAuthOptions Auth { get; init; } = new();
}

public sealed record OtlpAuthOptions
{
    public string? BearerToken { get; init; }
    public string? BearerTokenFile { get; init; }
    public string? BasicUsername { get; init; }
    public string? BasicPasswordProtected { get; init; }
    public string? CaCertificatePemPath { get; init; }
    public string? ClientCertificatePemPath { get; init; }
    public string? ClientPrivateKeyPemPath { get; init; }
    public string? ClientCertificatePfxPath { get; init; }
    public string? ClientCertificatePfxPasswordProtected { get; init; }
    public string? WindowsCertificateFingerprint { get; init; }
}

public sealed record BufferOptions
{
    public int MemoryQueueCapacity { get; init; } = 10_000;
    public bool DiskOnFailureEnabled { get; init; } = true;
    public bool SpoolsDuringHealthyExport { get; init; } = false;
    public string SpoolPath { get; init; } = "%LOCALAPPDATA%\\NINA\\NinaOtel\\spool";
    public long MaxSpoolBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public TimeSpan MaxSpoolAge { get; init; } = TimeSpan.FromDays(7);
    public int RecoveryFlushRecordsPerSecond { get; init; } = 500;
}

public sealed record CoreTelemetryOptions
{
    public bool EquipmentEnabled { get; init; } = true;
    public bool ImageStatsEnabled { get; init; } = true;
    public bool WorkflowTracesEnabled { get; init; } = true;
    public bool FilteredLogsEnabled { get; init; } = true;
    public bool RawForwardingEnabled { get; init; } = false;
}

public sealed record AddonOptions
{
    public bool Enabled { get; init; } = false;
    public bool RawForwardingEnabled { get; init; } = false;
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>();
}
```

- [ ] **Step 4: Run options tests**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter NinaOtelOptionsTests
```

Expected: PASS.

- [ ] **Step 5: Commit options**

```bash
git add src/NinaOtel.Core/Options tests/NinaOtel.Core.Tests/Options
git commit -m "feat: add core options models"
```

## Task 4: Implement Non-Blocking In-Memory Pipeline

**Files:**
- Create: `src/NinaOtel.Core/Pipeline/ITelemetryExporter.cs`
- Create: `src/NinaOtel.Core/Pipeline/TelemetryPipeline.cs`
- Test: `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`

- [ ] **Step 1: Write pipeline tests**

Create `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`:

```csharp
using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class TelemetryPipelineTests
{
    [Fact]
    public async Task TryPublish_DoesNotBlockWhenQueueIsFull()
    {
        var exporter = new RecordingExporter();
        await using var pipeline = new TelemetryPipeline(exporter, capacity: 1);
        var first = TelemetryRecord.Log(DateTimeOffset.UtcNow, "test", TelemetrySeverity.Information, "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Log(DateTimeOffset.UtcNow, "test", TelemetrySeverity.Information, "second", TelemetryPriority.Routine);

        pipeline.TryPublish(first).Should().BeTrue();
        pipeline.TryPublish(second).Should().BeFalse();
        pipeline.DroppedRecords.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ExportsQueuedRecords()
    {
        var exporter = new RecordingExporter();
        await using var pipeline = new TelemetryPipeline(exporter, capacity: 10);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Important)).Should().BeTrue();

        await exporter.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        exporter.Records.Should().ContainSingle(r => r.Name == "ready");
    }

    private sealed class RecordingExporter : ITelemetryExporter
    {
        private readonly TaskCompletionSource exported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int targetCount = 1;

        public List<TelemetryRecord> Records { get; } = [];

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            Records.AddRange(records);
            if (Records.Count >= targetCount)
            {
                exported.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            targetCount = count;
            using var cts = new CancellationTokenSource(timeout);
            await exported.Task.WaitAsync(cts.Token);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter TelemetryPipelineTests
```

Expected: FAIL with missing `NinaOtel.Core.Pipeline`.

- [ ] **Step 3: Add exporter abstraction**

Create `src/NinaOtel.Core/Pipeline/ITelemetryExporter.cs`:

```csharp
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public interface ITelemetryExporter
{
    Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add telemetry pipeline**

Create `src/NinaOtel.Core/Pipeline/TelemetryPipeline.cs`:

```csharp
using System.Threading.Channels;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryPipeline : ITelemetrySink, IAsyncDisposable
{
    private readonly Channel<TelemetryRecord> channel;
    private readonly ITelemetryExporter exporter;
    private readonly CancellationTokenSource stopCts = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    private long droppedRecords;

    public TelemetryPipeline(ITelemetryExporter exporter, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        this.exporter = exporter;
        channel = Channel.CreateBounded<TelemetryRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (worker != null)
        {
            return started.Task.WaitAsync(cancellationToken);
        }

        worker = Task.Run(() => RunAsync(stopCts.Token), CancellationToken.None);
        started.TrySetResult();
        return started.Task.WaitAsync(cancellationToken);
    }

    public bool TryPublish(TelemetryRecord record)
    {
        if (channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref droppedRecords);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        await stopCts.CancelAsync();

        if (worker != null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopCts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<TelemetryRecord>(128);

        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
            {
                batch.Add(record);
                while (batch.Count < 128 && channel.Reader.TryRead(out var next))
                {
                    batch.Add(next);
                }

                await exporter.ExportAsync(batch, cancellationToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

- [ ] **Step 5: Run pipeline tests**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter TelemetryPipelineTests
```

Expected: PASS.

- [ ] **Step 6: Commit pipeline**

```bash
git add src/NinaOtel.Core/Pipeline tests/NinaOtel.Core.Tests/Pipeline
git commit -m "feat: add nonblocking telemetry pipeline"
```

## Task 5: Implement Non-Blocking Add-On Host

**Files:**
- Create: `src/NinaOtel.Core/Addons/AddonContext.cs`
- Create: `src/NinaOtel.Core/Addons/AddonHost.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/AddonHostTests.cs`

- [ ] **Step 1: Write add-on host tests**

Create `tests/NinaOtel.Core.Tests/Addons/AddonHostTests.cs`:

```csharp
using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Addons;

namespace NinaOtel.Core.Tests.Addons;

public sealed class AddonHostTests
{
    [Fact]
    public async Task StartAsync_ReturnsWithoutWaitingForHangingAddon()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        var addon = new HangingAddon();

        var startTask = host.StartAsync([addon], CancellationToken.None);

        await startTask.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(
            () => sink.Records.Any(r =>
                r.Signal == TelemetrySignal.Health &&
                r.Source == "addon.hanging" &&
                r.Attributes.TryGetValue("status", out var status) &&
                Equals(status, "start_timeout")),
            TimeSpan.FromSeconds(1));

        sink.Records.Should().Contain(r =>
            r.Signal == TelemetrySignal.Health &&
            r.Source == "addon.hanging" &&
            r.Attributes.TryGetValue("status", out var status) &&
            Equals(status, "start_timeout"));
    }

    [Fact]
    public async Task StopAsync_ReportsStopTimeoutAndReturns()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        var addon = new HangingStopAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        sink.Records.Should().Contain(r =>
            r.Signal == TelemetrySignal.Health &&
            r.Source == "addon.hanging-stop" &&
            r.Attributes.TryGetValue("status", out var status) &&
            Equals(status, "stop_timeout"));
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public bool TryPublish(TelemetryRecord record)
        {
            Records.Add(record);
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private sealed class HangingAddon : ITelemetryAddon
    {
        public AddonMetadata Metadata { get; } = new("hanging", "Hanging", new Version(1, 0, 0), "test");
        public AddonValidationResult Validate() => AddonValidationResult.Success;
        public Task StartAsync(IAddonContext context, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HangingStopAddon : ITelemetryAddon
    {
        public AddonMetadata Metadata { get; } = new("hanging-stop", "Hanging Stop", new Version(1, 0, 0), "test");
        public AddonValidationResult Validate() => AddonValidationResult.Success;
        public Task StartAsync(IAddonContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter AddonHostTests
```

Expected: FAIL with missing `NinaOtel.Core.Addons`.

- [ ] **Step 3: Add add-on context**

Create `src/NinaOtel.Core/Addons/AddonContext.cs`:

```csharp
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Addons;

public sealed class AddonContext : IAddonContext
{
    public AddonContext(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        CancellationToken shutdownToken)
    {
        Sink = sink;
        TimeProvider = timeProvider;
        ShutdownToken = shutdownToken;
    }

    public ITelemetrySink Sink { get; }
    public TimeProvider TimeProvider { get; }
    public CancellationToken ShutdownToken { get; }

    public void ReportHealth(string addonId, string status, string message, TelemetryPriority priority)
    {
        Sink.TryPublish(TelemetryRecord.Health(
            TimeProvider.GetUtcNow(),
            $"addon.{addonId}",
            "ninaotel.addon.health",
            priority,
            new Dictionary<string, object?>
            {
                ["addon.id"] = addonId,
                ["status"] = status,
                ["message"] = message,
            }));
    }
}
```

- [ ] **Step 4: Add add-on host**

Create `src/NinaOtel.Core/Addons/AddonHost.cs`:

```csharp
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Addons;

public sealed class AddonHost
{
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan startTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly List<ITelemetryAddon> runningAddons = [];

    public AddonHost(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        TimeSpan startTimeout,
        TimeSpan stopTimeout)
    {
        this.sink = sink;
        this.timeProvider = timeProvider;
        this.startTimeout = startTimeout;
        this.stopTimeout = stopTimeout;
    }

    public Task StartAsync(IEnumerable<ITelemetryAddon> addons, CancellationToken cancellationToken)
    {
        foreach (var addon in addons)
        {
            var validation = addon.Validate();
            if (!validation.IsValid)
            {
                PublishHealth(addon, "validation_failed", string.Join("; ", validation.Errors), TelemetryPriority.Important);
                continue;
            }

            runningAddons.Add(addon);
            _ = Task.Run(() => StartOneAsync(addon), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await shutdownCts.CancelAsync();

        foreach (var addon in runningAddons)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(stopTimeout);

            try
            {
                await addon.StopAsync(timeoutCts.Token).WaitAsync(stopTimeout, cancellationToken);
                PublishHealth(addon, "stopped", "Add-on stopped.", TelemetryPriority.Routine);
            }
            catch (TimeoutException)
            {
                PublishHealth(addon, "stop_timeout", "Add-on stop timed out.", TelemetryPriority.Important);
            }
            catch (OperationCanceledException)
            {
                PublishHealth(addon, "stop_timeout", "Add-on stop was canceled.", TelemetryPriority.Important);
            }
            catch (Exception ex)
            {
                PublishHealth(addon, "stop_error", ex.Message, TelemetryPriority.Important);
            }
        }
    }

    private async Task StartOneAsync(ITelemetryAddon addon)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        timeoutCts.CancelAfter(startTimeout);

        try
        {
            var context = new AddonContext(sink, timeProvider, shutdownCts.Token);
            await addon.StartAsync(context, timeoutCts.Token).WaitAsync(startTimeout, shutdownCts.Token);
            PublishHealth(addon, "started", "Add-on started.", TelemetryPriority.Routine);
        }
        catch (TimeoutException)
        {
            PublishHealth(addon, "start_timeout", "Add-on start timed out.", TelemetryPriority.Important);
        }
        catch (OperationCanceledException)
        {
            PublishHealth(addon, "start_timeout", "Add-on start was canceled.", TelemetryPriority.Important);
        }
        catch (Exception ex)
        {
            PublishHealth(addon, "start_error", ex.Message, TelemetryPriority.Important);
        }
    }

    private void PublishHealth(ITelemetryAddon addon, string status, string message, TelemetryPriority priority)
    {
        sink.TryPublish(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            $"addon.{addon.Metadata.Id}",
            "ninaotel.addon.health",
            priority,
            new Dictionary<string, object?>
            {
                ["addon.id"] = addon.Metadata.Id,
                ["addon.name"] = addon.Metadata.DisplayName,
                ["status"] = status,
                ["message"] = message,
            }));
    }
}
```

- [ ] **Step 5: Run add-on host tests**

Run:

```bash
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter AddonHostTests
```

Expected: PASS.

- [ ] **Step 6: Commit add-on host**

```bash
git add src/NinaOtel.Core/Addons tests/NinaOtel.Core.Tests/Addons
git commit -m "feat: add nonblocking addon host"
```

## Task 6: Add Minimal NINA Plugin Shell And Options Resource

**Files:**
- Create: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
- Create: `src/NinaOtel.Plugin/Options/Options.xaml`
- Create: `src/NinaOtel.Plugin/Options/Options.xaml.cs`
- Create: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`

- [ ] **Step 1: Create options view model**

Create `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`:

```csharp
using NinaOtel.Core.Options;

namespace NinaOtel.Plugin.Options;

public sealed class NinaOtelOptionsViewModel
{
    public NinaOtelOptionsViewModel(NinaOtelOptions options)
    {
        Options = options;
    }

    public NinaOtelOptions Options { get; }
    public string CollectorEndpoint => Options.Otlp.Endpoint.ToString();
    public string CollectorProtocol => Options.Otlp.Protocol.ToString();
    public bool DiskOnFailureEnabled => Options.Buffer.DiskOnFailureEnabled;
    public string Status => "NinaOtel foundation loaded";
}
```

- [ ] **Step 2: Create options resource dictionary**

Create `src/NinaOtel.Plugin/Options/Options.xaml`:

```xml
<ResourceDictionary
    x:Class="NinaOtel.Plugin.Options.Options"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <DataTemplate x:Key="NinaOtel_Options">
        <StackPanel Orientation="Vertical" Margin="8">
            <TextBlock Text="NinaOtel" FontWeight="Bold" FontSize="16" />
            <TextBlock Text="{Binding NinaOtelOptionsViewModel.Status}" Margin="0,8,0,0" />
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <TextBlock Text="Collector:" FontWeight="SemiBold" />
                <TextBlock Text="{Binding NinaOtelOptionsViewModel.CollectorEndpoint}" Margin="8,0,0,0" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                <TextBlock Text="Protocol:" FontWeight="SemiBold" />
                <TextBlock Text="{Binding NinaOtelOptionsViewModel.CollectorProtocol}" Margin="8,0,0,0" />
            </StackPanel>
            <CheckBox
                Content="Disk spool on collector failure"
                IsChecked="{Binding NinaOtelOptionsViewModel.DiskOnFailureEnabled}"
                IsEnabled="False"
                Margin="0,8,0,0" />
        </StackPanel>
    </DataTemplate>
</ResourceDictionary>
```

- [ ] **Step 3: Create options resource code-behind**

Create `src/NinaOtel.Plugin/Options/Options.xaml.cs`:

```csharp
using System.ComponentModel.Composition;
using System.Windows;

namespace NinaOtel.Plugin.Options;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: Create plugin shell**

Create `src/NinaOtel.Plugin/NinaOtelPlugin.cs`:

```csharp
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using NinaOtel.Plugin.Options;

namespace NinaOtel.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class NinaOtelPlugin : PluginBase
{
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly TelemetryPipeline pipeline;
    private readonly AddonHost addonHost;

    [ImportingConstructor]
    public NinaOtelPlugin(IProfileService profileService)
    {
        var options = NinaOtelOptions.CreateDefault();
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(options);
        pipeline = new TelemetryPipeline(new NopTelemetryExporter(), options.Buffer.MemoryQueueCapacity);
        addonHost = new AddonHost(
            pipeline,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public NinaOtelOptionsViewModel NinaOtelOptionsViewModel { get; }

    public override async Task Initialize()
    {
        await pipeline.StartAsync(shutdownCts.Token);
        await addonHost.StartAsync(Array.Empty<NinaOtel.Abstractions.Addons.ITelemetryAddon>(), shutdownCts.Token);
        Logger.Info("NinaOtel foundation initialized.");
    }

    public override async Task Teardown()
    {
        await shutdownCts.CancelAsync();
        await addonHost.StopAsync(CancellationToken.None);
        await pipeline.DisposeAsync();
        shutdownCts.Dispose();
        await base.Teardown();
    }

    private sealed class NopTelemetryExporter : ITelemetryExporter
    {
        public Task ExportAsync(
            IReadOnlyList<NinaOtel.Abstractions.Telemetry.TelemetryRecord> records,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Build plugin project**

Run:

```bash
dotnet build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj
```

Expected: build succeeds. If the NINA plugin API requires a different options template key, inspect `/tmp/nina-otel-scout-influx/Options.xaml` and align the key with NINA's plugin naming pattern before committing.

- [ ] **Step 6: Commit plugin shell**

```bash
git add src/NinaOtel.Plugin
git commit -m "feat: add NinaOtel plugin shell"
```

## Task 7: Add Foundation Build Script And Verification

**Files:**
- Create: `scripts/verify.sh`
- Modify: `.gitignore`

- [ ] **Step 1: Create `.gitignore`**

Create `.gitignore`:

```gitignore
bin/
obj/
.vs/
.vscode/
TestResults/
*.user
*.suo
```

- [ ] **Step 2: Create verify script**

Create `scripts/verify.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

dotnet restore NinaOtel.sln
dotnet build NinaOtel.sln --no-restore
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-build
```

- [ ] **Step 3: Make script executable**

Run:

```bash
chmod +x scripts/verify.sh
```

- [ ] **Step 4: Run verification**

Run:

```bash
./scripts/verify.sh
```

Expected:

```text
Build succeeded.
Passed!
```

The exact test runner summary can include timing and counts; there must be zero failed tests.

- [ ] **Step 5: Commit verification script**

```bash
git add .gitignore scripts/verify.sh
git commit -m "chore: add foundation verification script"
```

## Task 8: Final Foundation Review

**Files:**
- Read: `docs/superpowers/specs/2026-06-14-nina-otel-design.md`
- Read: all files changed by Tasks 1-7.

- [ ] **Step 1: Run full verification**

Run:

```bash
./scripts/verify.sh
```

Expected: build and tests pass.

- [ ] **Step 2: Confirm git status**

Run:

```bash
git status --short
```

Expected: no output.

- [ ] **Step 3: Record implementation notes**

Add a short final response with:

```text
Implemented foundation slice:
- solution and project scaffold
- shared telemetry/add-on contracts
- non-blocking in-memory telemetry pipeline
- non-blocking add-on host
- minimal NINA plugin shell and options resource

Verification:
- ./scripts/verify.sh
```

## Plan Self-Review

Spec coverage in this plan:

- Covered: solution scaffold, shared contracts, non-blocking add-on lifecycle, non-blocking memory pipeline, core/plugin boundary, minimal options/status surface.
- Covered by named follow-on plans: real OTLP exporter, mTLS/auth, disk-on-failure spool, core NINA collectors, PHD2 add-on, Target Scheduler add-on, Night Summary add-on, OnStepX/JTW add-on.

Placeholder scan:

- This plan contains no `TBD`, no `TODO`, and no unspecified code steps.

Type consistency:

- `TelemetryRecord`, `ITelemetrySink`, `ITelemetryAddon`, `AddonHost`, `TelemetryPipeline`, `ITelemetryExporter`, and `NinaOtelOptions` names are consistent across tests and implementation steps.
