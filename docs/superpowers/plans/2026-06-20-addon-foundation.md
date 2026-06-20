# Add-On Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the v1 first-party add-on foundation so PHD2, Target Scheduler, Night Summary, and OnStepX are visible, configurable, fail-open, and independently enableable without implementing their source parsers yet.

**Architecture:** Keep `NinaOtel.Core` as the generic add-on host and health publisher. Put first-party shell add-ons in separate `NinaOtel.Addons.*` assemblies that depend only on `NinaOtel.Abstractions`. Keep NINA plugin UI/config orchestration in `NinaOtel.Plugin`.

**Tech Stack:** C#/.NET 8, WPF options XAML, xUnit/FluentAssertions, NINA plugin packaging script.

---

### Task 1: Host Honors Disabled Add-Ons

**Files:**
- Modify: `src/NinaOtel.Core/Addons/AddonHost.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/AddonHostTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test near `StartAsync_PassesConfiguredAddonConfigurationToValidateAndContext`:

```csharp
[Fact]
public async Task StartAsync_WhenAddonConfigurationIsDisabled_ReportsDisabledWithoutValidatingOrStarting()
{
    var sink = new RecordingSink();
    var host = new AddonHost(
        sink,
        TimeProvider.System,
        LifecycleTimeout,
        LifecycleTimeout,
        new Dictionary<string, AddonConfiguration>
        {
            ["configured"] = new AddonConfiguration(enabled: false),
        });
    var addon = new ConfigurationRecordingAddon();

    await host.StartAsync([addon], CancellationToken.None);

    var record = await WaitForHealthRecordAsync(sink, "configured", "disabled");
    record.Priority.Should().Be(TelemetryPriority.Routine);
    addon.ValidateCalls.Should().Be(0);
    addon.StartCalls.Should().Be(0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~AddonHostTests.StartAsync_WhenAddonConfigurationIsDisabled -v:minimal
```

Expected local result: either a real failing assertion because disabled add-ons still start, or local `NU1301` restore failure. If restore fails, continue with package/script checks locally and rely on GitHub CI for full execution.

- [ ] **Step 3: Implement minimal behavior**

In `AddonHost.InvokeStartCoreAsync`, after resolving configuration and before validation, add:

```csharp
if (!configuration.Enabled)
{
    PublishHealth(runtime, "disabled", "Add-on disabled.", TelemetryPriority.Routine);
    return StartLifecycleResult.Skipped;
}
```

- [ ] **Step 4: Run verification**

Run the focused command from Step 2 and `bash tests/package-plugin-tests.sh`.

---

### Task 2: Add First-Party Shell Add-On Assemblies

**Files:**
- Create: `src/NinaOtel.Addons.PHD2/NinaOtel.Addons.PHD2.csproj`
- Create: `src/NinaOtel.Addons.PHD2/Phd2TelemetryAddon.cs`
- Create: `src/NinaOtel.Addons.TargetScheduler/NinaOtel.Addons.TargetScheduler.csproj`
- Create: `src/NinaOtel.Addons.TargetScheduler/TargetSchedulerTelemetryAddon.cs`
- Create: `src/NinaOtel.Addons.NightSummary/NinaOtel.Addons.NightSummary.csproj`
- Create: `src/NinaOtel.Addons.NightSummary/NightSummaryTelemetryAddon.cs`
- Create: `src/NinaOtel.Addons.OnStepX/NinaOtel.Addons.OnStepX.csproj`
- Create: `src/NinaOtel.Addons.OnStepX/OnStepXTelemetryAddon.cs`
- Modify: `NinaOtel.sln`
- Modify: `src/NinaOtel.Plugin/NinaOtel.Plugin.csproj`
- Test: `tests/NinaOtel.Core.Tests/Contracts/TelemetryContractTests.cs`

- [ ] **Step 1: Write failing contract/catalog tests**

Add a test that references the four add-on classes and asserts metadata:

```csharp
[Fact]
public void FirstPartyAddons_ExposeStableMetadata()
{
    ITelemetryAddon[] addons =
    [
        new NinaOtel.Addons.PHD2.Phd2TelemetryAddon(),
        new NinaOtel.Addons.TargetScheduler.TargetSchedulerTelemetryAddon(),
        new NinaOtel.Addons.NightSummary.NightSummaryTelemetryAddon(),
        new NinaOtel.Addons.OnStepX.OnStepXTelemetryAddon(),
    ];

    addons.Select(addon => addon.Metadata.Id).Should().BeEquivalentTo(
    [
        "phd2",
        "target-scheduler",
        "night-summary",
        "onstepx",
    ]);

    foreach (var addon in addons)
    {
        addon.Validate(AddonConfiguration.Default).IsValid.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~FirstPartyAddons_ExposeStableMetadata -v:minimal
```

Expected: compile failure because the assemblies/classes do not exist, or local `NU1301`.

- [ ] **Step 3: Create minimal add-on projects**

Each project targets `net8.0`, is not packable, and references `..\NinaOtel.Abstractions\NinaOtel.Abstractions.csproj`.

Each class implements `ITelemetryAddon`, exposes stable metadata, validates successfully, reports a routine health breadcrumb on start, and returns promptly:

```csharp
public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(context);
    cancellationToken.ThrowIfCancellationRequested();
    context.ReportHealth(Metadata.Id, "waiting", "Add-on shell loaded; source collection is not implemented yet.", TelemetryPriority.Routine);
    return Task.CompletedTask;
}
```

`StopAsync` returns `Task.CompletedTask`.

- [ ] **Step 4: Wire projects**

Add the four add-on projects to `NinaOtel.sln`. Add project references from `NinaOtel.Plugin.csproj` to each add-on project so they are copied to the plugin output/package.

- [ ] **Step 5: Run verification**

Run the focused test command and `bash tests/package-plugin-tests.sh`.

---

### Task 3: Add Add-On Catalog, Settings, and Options View Models

**Files:**
- Create: `src/NinaOtel.Plugin/Addons/FirstPartyAddonCatalog.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Test: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
- Test: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing options tests**

Add tests proving:

```csharp
viewModel.Addons.Select(addon => addon.Id).Should().Equal("phd2", "target-scheduler", "night-summary", "onstepx");
viewModel.Addons.Should().OnlyContain(addon => addon.IsEnabled == false);
viewModel.Options.Addons["phd2"].Enabled.Should().BeFalse();
```

Add a persistence test:

```csharp
var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");
phd2.IsEnabled = true;
phd2.RawForwardingEnabled = true;

settings.GetBoolean("Addon.phd2.Enabled", false).Should().BeTrue();
settings.GetBoolean("Addon.phd2.RawForwardingEnabled", false).Should().BeTrue();
viewModel.Options.Addons["phd2"].Enabled.Should().BeTrue();
viewModel.Options.Addons["phd2"].RawForwardingEnabled.Should().BeTrue();
```

Add a health update test:

```csharp
viewModel.UpdateAddonHealth("phd2", "started", "Add-on started.");
viewModel.Addons.Single(addon => addon.Id == "phd2").Status.Should().Be("started");
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~NinaOtelOptionsViewModelTests -v:minimal
```

Expected: compile failures for missing add-on view model surface, or local `NU1301`.

- [ ] **Step 3: Implement catalog and view models**

Create `FirstPartyAddonCatalog` with four descriptors: id, display name, source type, and factory. Add `AddonOptionViewModel` inside `NinaOtelOptionsViewModel.cs` or a new focused file under `Options/` if the file becomes unwieldy.

Persist booleans with keys:

```text
Addon.<id>.Enabled
Addon.<id>.RawForwardingEnabled
```

Default both to `false`. `NinaOtelOptionsViewModel.Options` must populate `NinaOtelOptions.Addons` for all four add-ons.

- [ ] **Step 4: Update XAML**

Add an add-on section after collector status and before exporter settings. Bind an `ItemsControl` to `NinaOtelOptionsViewModel.Addons`, with checkbox columns for enabled/raw forwarding and text fields for display name/source/status/message. Keep the layout simple and scroll-friendly.

- [ ] **Step 5: Run verification**

Run the focused tests, `DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OptionsXamlTests -v:minimal`, and `bash tests/package-plugin-tests.sh`.

---

### Task 4: Wire Enabled Add-Ons Into Plugin Startup and Health UI

**Files:**
- Modify: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
- Modify: `src/NinaOtel.Core/Addons/AddonHost.cs`
- Test: `tests/NinaOtel.Plugin.Tests/Plugin/NinaOtelPluginWiringTests.cs`
- Test: `tests/NinaOtel.Core.Tests/Addons/AddonHostTests.cs`

- [ ] **Step 1: Write failing wiring tests**

Add text-based plugin wiring tests asserting:

```csharp
source.Should().Contain("FirstPartyAddonCatalog.CreateAll()");
source.Should().NotContain("Array.Empty<ITelemetryAddon>()");
source.Should().Contain("NinaOtelOptionsViewModel.UpdateAddonHealth");
```

Add an `AddonHost` test if a status callback is introduced, proving start/disabled/error health is reported both to telemetry and callback.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~NinaOtelPluginWiringTests -v:minimal
```

Expected: failure because the plugin still starts an empty add-on array.

- [ ] **Step 3: Implement plugin wiring**

Construct `AddonHost` with `options.Addons` and an add-on health callback, then call:

```csharp
await addonHost.StartAsync(FirstPartyAddonCatalog.CreateAll(), shutdownCts.Token).ConfigureAwait(false);
```

If options change, restart add-ons only if this can be done safely in a small patch; otherwise document that add-on enablement changes take effect after plugin reload and set `Status` accordingly. Do not block NINA startup on add-on work.

- [ ] **Step 4: Run verification**

Run focused wiring tests and package test.

---

### Task 5: Packaging and Release Prep

**Files:**
- Modify: `tests/package-plugin-tests.sh`
- Modify: `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs` only when releasing

- [ ] **Step 1: Write packaging expectation update**

Update `tests/package-plugin-tests.sh` fake build output and expected entries to include:

```text
NinaOtel.Addons.NightSummary.dll
NinaOtel.Addons.OnStepX.dll
NinaOtel.Addons.PHD2.dll
NinaOtel.Addons.TargetScheduler.dll
```

- [ ] **Step 2: Run package verification**

Run:

```bash
bash tests/package-plugin-tests.sh
```

Expected: package contains plugin, core, abstractions, four add-on assemblies, and OTel dependencies; NINA assemblies remain excluded.

- [ ] **Step 3: Full local verification**

Run:

```bash
git diff --check
bash tests/package-plugin-tests.sh
DOTNET=$(mise which dotnet); "$DOTNET" test NinaOtel.sln -v:minimal
```

If local `dotnet test` fails with `NU1301`, record that exact restore failure and use GitHub Actions after push as the full test gate.

- [ ] **Step 4: Commit**

Commit with:

```bash
git add .
git commit -m "Add first-party add-on foundation"
```

