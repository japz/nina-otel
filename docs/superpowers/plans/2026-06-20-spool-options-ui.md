# NinaOtel Spool Options UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the disk-on-failure spool path, max size, and max age editable from the NINA plugin options UI.

**Architecture:** Keep profile persistence in `NinaOtelOptionsViewModel`, because it already owns user-editable plugin settings. Store numeric spool settings as strings in the existing settings store to avoid widening the NINA profile accessor interface in this slice. Validate user text before applying it to `NinaOtelOptions`; invalid text remains visible in the UI but does not replace the last valid applied value.

**Tech Stack:** C# 12, .NET 8, WPF binding, xUnit, FluentAssertions, existing NINA profile settings store abstraction.

---

## Target File Structure

- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Add editable `SpoolPath`, `MaxSpoolSizeGb`, and `MaxSpoolAgeDays` properties.
  - Persist valid values through `INinaOtelSettingsStore.SetString`.
  - Keep last valid applied values for `NinaOtelOptions.Buffer`.
  - Reject invalid numeric values with clear status text.
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
  - Add text inputs for spool path, max size in GB, and max age in days.
  - Use `Mode=TwoWay` and `UpdateSourceTrigger=LostFocus`.
- Modify: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Add tests for defaults, persisted values, valid saves, invalid rejection, and reload.
- Modify: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`
  - Add XAML binding assertions for the new controls.

## Non-Goals For This Slice

- No UI for memory queue capacity.
- No UI for recovery flush rate.
- No protected secret storage.
- No pipeline recreation for memory queue size changes.
- No full degraded/recovery state machine.
- No priority-aware disk eviction.

## Task 1: View Model Spool Settings

**Files:**
- Modify: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`

- [ ] **Step 1: Write failing default-load test assertions**

In `Constructor_LoadsDefaultSettingsWhenStoreIsEmpty`, add these assertions after the existing buffer assertions:

```csharp
viewModel.SpoolPath.Should().Be("%LOCALAPPDATA%\\NINA\\NinaOtel\\spool");
viewModel.MaxSpoolSizeGb.Should().Be("1");
viewModel.MaxSpoolAgeDays.Should().Be("7");
viewModel.Options.Buffer.SpoolPath.Should().Be("%LOCALAPPDATA%\\NINA\\NinaOtel\\spool");
viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(1L * 1024 * 1024 * 1024);
viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(7));
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~NinaOtelOptionsViewModelTests --no-restore -v:minimal
```

Expected locally: the test project may be blocked by `NU1301`. Expected in CI before implementation: compile fails because the properties do not exist.

- [ ] **Step 2: Write persisted-load test assertions**

In `Constructor_LoadsPersistedSettingsFromStore`, add:

```csharp
settings.SetString("SpoolPath", "D:\\NinaOtel\\spool");
settings.SetString("MaxSpoolSizeGb", "2.5");
settings.SetString("MaxSpoolAgeDays", "14");
```

Then assert:

```csharp
viewModel.SpoolPath.Should().Be("D:\\NinaOtel\\spool");
viewModel.MaxSpoolSizeGb.Should().Be("2.5");
viewModel.MaxSpoolAgeDays.Should().Be("14");
viewModel.Options.Buffer.SpoolPath.Should().Be("D:\\NinaOtel\\spool");
viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(2684354560);
viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(14));
```

- [ ] **Step 3: Add valid-save test**

Add this test:

```csharp
[Fact]
public void SpoolSettings_SaveValidValuesImmediately()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);

    viewModel.SpoolPath = "Z:\\Telemetry\\spool";
    viewModel.MaxSpoolSizeGb = "3.25";
    viewModel.MaxSpoolAgeDays = "10";

    settings.GetString("SpoolPath", string.Empty).Should().Be("Z:\\Telemetry\\spool");
    settings.GetString("MaxSpoolSizeGb", string.Empty).Should().Be("3.25");
    settings.GetString("MaxSpoolAgeDays", string.Empty).Should().Be("10");
    viewModel.Options.Buffer.SpoolPath.Should().Be("Z:\\Telemetry\\spool");
    viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(3489660928);
    viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(10));
    viewModel.Status.Should().Be("Settings saved");
}
```

- [ ] **Step 4: Add invalid numeric rejection test**

Add this test:

```csharp
[Fact]
public void SpoolNumericSettings_RejectInvalidValuesWithoutReplacingAppliedOptions()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);
    viewModel.MaxSpoolSizeGb = "2";
    viewModel.MaxSpoolAgeDays = "8";

    viewModel.MaxSpoolSizeGb = "0";

    settings.GetString("MaxSpoolSizeGb", string.Empty).Should().Be("2");
    viewModel.MaxSpoolSizeGb.Should().Be("0");
    viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(2L * 1024 * 1024 * 1024);
    viewModel.Status.Should().Be("Max spool size must be greater than 0 GB.");

    viewModel.MaxSpoolAgeDays = "-1";

    settings.GetString("MaxSpoolAgeDays", string.Empty).Should().Be("8");
    viewModel.MaxSpoolAgeDays.Should().Be("-1");
    viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(8));
    viewModel.Status.Should().Be("Max spool age must be greater than 0 days.");
}
```

- [ ] **Step 5: Implement view-model properties and parsing**

In `NinaOtelOptionsViewModel`, add constants:

```csharp
private const string SpoolPathKey = nameof(SpoolPath);
private const string MaxSpoolSizeGbKey = nameof(MaxSpoolSizeGb);
private const string MaxSpoolAgeDaysKey = nameof(MaxSpoolAgeDays);
private const decimal BytesPerGb = 1024m * 1024m * 1024m;
```

Add fields:

```csharp
private string spoolPath = string.Empty;
private string appliedSpoolPath = string.Empty;
private string maxSpoolSizeGb = string.Empty;
private long appliedMaxSpoolBytes;
private string maxSpoolAgeDays = string.Empty;
private TimeSpan appliedMaxSpoolAge;
```

Add public properties using the existing `SetField` pattern:

```csharp
public string SpoolPath
{
    get => spoolPath;
    set
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SetField(ref spoolPath, value, saveOptions: false);
            Status = "Spool path cannot be empty.";
            return;
        }

        if (SetField(ref spoolPath, value, saveOptions: false))
        {
            appliedSpoolPath = value;
            settingsStore.SetString(SpoolPathKey, value);
            RaisePropertyChanged(nameof(Options));
            Status = "Settings saved";
        }
    }
}

public string MaxSpoolSizeGb
{
    get => maxSpoolSizeGb;
    set
    {
        if (!TryParsePositiveDecimal(value, out var gb))
        {
            SetField(ref maxSpoolSizeGb, value, saveOptions: false);
            Status = "Max spool size must be greater than 0 GB.";
            return;
        }

        if (SetField(ref maxSpoolSizeGb, value, saveOptions: false))
        {
            appliedMaxSpoolBytes = ConvertGbToBytes(gb);
            settingsStore.SetString(MaxSpoolSizeGbKey, value);
            RaisePropertyChanged(nameof(Options));
            Status = "Settings saved";
        }
    }
}

public string MaxSpoolAgeDays
{
    get => maxSpoolAgeDays;
    set
    {
        if (!TryParsePositiveDecimal(value, out var days))
        {
            SetField(ref maxSpoolAgeDays, value, saveOptions: false);
            Status = "Max spool age must be greater than 0 days.";
            return;
        }

        if (SetField(ref maxSpoolAgeDays, value, saveOptions: false))
        {
            appliedMaxSpoolAge = TimeSpan.FromDays((double)days);
            settingsStore.SetString(MaxSpoolAgeDaysKey, value);
            RaisePropertyChanged(nameof(Options));
            Status = "Settings saved";
        }
    }
}
```

Use `System.Globalization` and add helpers:

```csharp
private static bool TryParsePositiveDecimal(string? value, out decimal parsed)
{
    return decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed) &&
        parsed > 0;
}

private static long ConvertGbToBytes(decimal gb)
{
    var bytes = decimal.Round(gb * BytesPerGb, 0, MidpointRounding.AwayFromZero);
    return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
}

private static string FormatGb(long bytes) =>
    (bytes / BytesPerGb).ToString("0.###", CultureInfo.InvariantCulture);

private static string FormatDays(TimeSpan age) =>
    age.TotalDays.ToString("0.###", CultureInfo.InvariantCulture);
```

Update `LoadFromSettings()` to load and notify these properties:

```csharp
spoolPath = settingsStore.GetString(SpoolPathKey, defaults.Buffer.SpoolPath);
appliedSpoolPath = string.IsNullOrWhiteSpace(spoolPath) ? defaults.Buffer.SpoolPath : spoolPath;
maxSpoolSizeGb = settingsStore.GetString(MaxSpoolSizeGbKey, FormatGb(defaults.Buffer.MaxSpoolBytes));
appliedMaxSpoolBytes = TryParsePositiveDecimal(maxSpoolSizeGb, out var gb)
    ? ConvertGbToBytes(gb)
    : defaults.Buffer.MaxSpoolBytes;
maxSpoolAgeDays = settingsStore.GetString(MaxSpoolAgeDaysKey, FormatDays(defaults.Buffer.MaxSpoolAge));
appliedMaxSpoolAge = TryParsePositiveDecimal(maxSpoolAgeDays, out var days)
    ? TimeSpan.FromDays((double)days)
    : defaults.Buffer.MaxSpoolAge;
```

Add property notifications:

```csharp
RaisePropertyChanged(nameof(SpoolPath));
RaisePropertyChanged(nameof(MaxSpoolSizeGb));
RaisePropertyChanged(nameof(MaxSpoolAgeDays));
```

Update `CreateOptions()`:

```csharp
Buffer = defaults.Buffer with
{
    DiskOnFailureEnabled = diskOnFailureEnabled,
    SpoolPath = appliedSpoolPath,
    MaxSpoolBytes = appliedMaxSpoolBytes,
    MaxSpoolAge = appliedMaxSpoolAge,
},
```

- [ ] **Step 6: Run focused view-model tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~NinaOtelOptionsViewModelTests --no-restore -v:minimal
```

Expected locally: report `NU1301` if NuGet blocks. Expected in GitHub CI: tests pass.

## Task 2: Options XAML Bindings

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Modify: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Add failing XAML tests**

Add this helper to `OptionsXamlTests`:

```csharp
private static XElement SingleTextBoxBoundTo(XDocument document, string propertyName)
{
    return document
        .Descendants(PresentationNamespace + "TextBox")
        .Single(element => element.Attribute("Text")?.Value.Contains(propertyName, StringComparison.Ordinal) == true);
}
```

Add this test:

```csharp
[Theory]
[InlineData("SpoolPath")]
[InlineData("MaxSpoolSizeGb")]
[InlineData("MaxSpoolAgeDays")]
public void OptionsTemplate_SpoolTextBoxesUseTwoWayLostFocusBindings(string propertyName)
{
    var document = XDocument.Load(FindOptionsXamlPath());

    var textbox = SingleTextBoxBoundTo(document, propertyName);
    var binding = textbox.Attribute("Text")?.Value;

    binding.Should().Contain("Mode=TwoWay");
    binding.Should().Contain("UpdateSourceTrigger=LostFocus");
}
```

- [ ] **Step 2: Add XAML controls**

In `Options.xaml`, add three rows after the disk-spool checkbox:

```xml
<Grid Margin="0,8,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="16" />
        <ColumnDefinition Width="320" />
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="8" />
        <RowDefinition Height="Auto" />
        <RowDefinition Height="8" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>

    <TextBlock Grid.Row="0" Grid.Column="0" Text="Spool path:" FontWeight="SemiBold" VerticalAlignment="Center" />
    <TextBox
        Grid.Row="0"
        Grid.Column="2"
        MinWidth="320"
        Text="{Binding NinaOtelOptionsViewModel.SpoolPath, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />

    <TextBlock Grid.Row="2" Grid.Column="0" Text="Max spool GB:" FontWeight="SemiBold" VerticalAlignment="Center" />
    <TextBox
        Grid.Row="2"
        Grid.Column="2"
        Width="120"
        HorizontalAlignment="Left"
        Text="{Binding NinaOtelOptionsViewModel.MaxSpoolSizeGb, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />

    <TextBlock Grid.Row="4" Grid.Column="0" Text="Max spool days:" FontWeight="SemiBold" VerticalAlignment="Center" />
    <TextBox
        Grid.Row="4"
        Grid.Column="2"
        Width="120"
        HorizontalAlignment="Left"
        Text="{Binding NinaOtelOptionsViewModel.MaxSpoolAgeDays, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />
</Grid>
```

- [ ] **Step 3: Run XAML tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OptionsXamlTests --no-restore -v:minimal
```

Expected locally: report `NU1301` if NuGet blocks. Expected in GitHub CI: tests pass.

## Task 3: Verification And Commit

**Files:**
- Modified files from Tasks 1 and 2.

- [ ] **Step 1: Run formatting/diff check**

Run:

```bash
git diff --check
```

Expected: exit code `0`.

- [ ] **Step 2: Run local build checks**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore -v:minimal
```

Expected locally: core may build; plugin/test builds may report `NU1301`. Do not claim local success unless the command exits `0`.

- [ ] **Step 3: Commit behavior**

Run:

```bash
git add src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Expose disk spool settings in options"
```

## Self-Review Checklist

- Spec coverage: the plan exposes the spool path, max bytes through GB, and max age through days.
- Placeholder scan: no TODO/TBD placeholders.
- Type consistency: public property names match XAML binding names and test names.
- Scope control: no memory queue, recovery rate, auth, or add-on UI work in this slice.
