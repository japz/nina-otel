# OTLP Static Headers UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a NINA user configure static OTLP headers from the NinaOtel options screen and have those headers flow into the existing OTLP exporters.

**Architecture:** The core exporter already accepts `OtlpOptions.Headers` and applies them to SDK-backed logs/live metrics plus the custom point-in-time metric and trace exporters. This slice adds a single multiline UI/profile setting in `NinaOtelOptionsViewModel`, parses it into an applied header dictionary, keeps invalid edits visible without replacing the last valid applied headers, and binds it from `Options.xaml`.

**Tech Stack:** C# `net8.0-windows`, WPF XAML bindings, NINA profile plugin settings, xUnit/FluentAssertions.

---

## File Map

- Modify `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Add `StaticHeaders` editable text property.
  - Load/save raw header text under the profile key `StaticHeaders`.
  - Parse non-empty lines as either `Header-Name: value` or `Header-Name=value`.
  - Apply only valid parsed headers to `Options.Otlp.Headers`.
  - Do not echo header values into `Status` or collector debug text.
- Modify `src/NinaOtel.Plugin/Options/Options.xaml`
  - Add a labelled multiline `TextBox` for static headers near collector endpoint/protocol.
- Modify `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Cover defaults, persisted values, valid save/apply, duplicate override, invalid edit behavior, and reload.
- Modify `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`
  - Assert the `StaticHeaders` text box uses `Mode=TwoWay` and `UpdateSourceTrigger=LostFocus`.

## Task 1: View Model Static Header Parsing and Persistence

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Test: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`

- [ ] **Step 1: Write failing tests for default and persisted headers**

Add assertions to `Constructor_LoadsDefaultSettingsWhenStoreIsEmpty`:

```csharp
viewModel.StaticHeaders.Should().BeEmpty();
viewModel.Options.Otlp.Headers.Should().BeEmpty();
```

Add persisted header setup and assertions to `Constructor_LoadsPersistedSettingsFromStore`:

```csharp
settings.SetString("StaticHeaders", "Authorization: Bearer abc\r\nx-scope = nina");
```

```csharp
viewModel.StaticHeaders.Should().Be("Authorization: Bearer abc\r\nx-scope = nina");
viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer abc");
viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "FullyQualifiedName~NinaOtelOptionsViewModelTests" --logger "console;verbosity=normal"
```

Expected: fail because `StaticHeaders` does not exist.

- [ ] **Step 3: Add view-model state and load/apply defaults**

In `NinaOtelOptionsViewModel.cs`, add constants and fields:

```csharp
private const string StaticHeadersKey = nameof(StaticHeaders);
private string staticHeaders = string.Empty;
private IReadOnlyDictionary<string, string> appliedStaticHeaders = new Dictionary<string, string>();
```

In `LoadFromSettings()`, load and parse:

```csharp
staticHeaders = settingsStore.GetString(StaticHeadersKey, string.Empty);
appliedStaticHeaders = TryParseHeaders(staticHeaders, out var parsedHeaders, out _)
    ? parsedHeaders
    : defaults.Otlp.Headers;
```

Add `RaisePropertyChanged(nameof(StaticHeaders));`.

Update `CreateOptions()`:

```csharp
Otlp = defaults.Otlp with
{
    Endpoint = new Uri(appliedCollectorEndpoint),
    Protocol = collectorProtocol,
    Headers = appliedStaticHeaders,
},
```

Add an editable property:

```csharp
public string StaticHeaders
{
    get => staticHeaders;
    set
    {
        if (!TryParseHeaders(value, out var headers, out var failure))
        {
            SetField(ref staticHeaders, value, saveOptions: false);
            Status = failure;
            return;
        }

        if (SetField(ref staticHeaders, value, saveOptions: false))
        {
            appliedStaticHeaders = headers;
            settingsStore.SetString(StaticHeadersKey, value);
            RaisePropertyChanged(nameof(Options));
            Status = "Settings saved";
        }
    }
}
```

- [ ] **Step 4: Add parser helper**

Add a private parser near other helpers:

```csharp
private static bool TryParseHeaders(
    string? value,
    out IReadOnlyDictionary<string, string> headers,
    out string status)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    headers = parsed;
    status = string.Empty;

    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    using var reader = new StringReader(value);
    for (var lineNumber = 1; ; lineNumber++)
    {
        var line = reader.ReadLine();
        if (line is null)
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var separatorIndex = line.IndexOf(':');
        if (separatorIndex < 0)
        {
            separatorIndex = line.IndexOf('=');
        }

        if (separatorIndex <= 0)
        {
            status = $"Static header line {lineNumber} must use 'Name: value'.";
            return false;
        }

        var name = line[..separatorIndex].Trim();
        var headerValue = line[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            status = $"Static header line {lineNumber} must include a header name.";
            return false;
        }

        if (string.IsNullOrEmpty(headerValue))
        {
            status = $"Static header line {lineNumber} must include a header value.";
            return false;
        }

        parsed[name] = headerValue;
    }

    return true;
}
```

- [ ] **Step 5: Run tests and fix compile issues**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "FullyQualifiedName~NinaOtelOptionsViewModelTests" --logger "console;verbosity=normal"
```

Expected: the two updated constructor tests pass.

- [ ] **Step 6: Add tests for valid save, duplicates, and invalid edit behavior**

Add a new test:

```csharp
[Fact]
public void StaticHeaders_SaveValidValuesAndUseLastDuplicateCaseInsensitiveHeader()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);

    viewModel.StaticHeaders = "Authorization: Bearer first\r\nauthorization = Bearer second\r\nx-scope: nina";

    settings.GetString("StaticHeaders", string.Empty).Should().Be(
        "Authorization: Bearer first\r\nauthorization = Bearer second\r\nx-scope: nina");
    viewModel.Options.Otlp.Headers.Should().HaveCount(2);
    viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer second");
    viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
    viewModel.Status.Should().Be("Settings saved");
}
```

Add another test:

```csharp
[Fact]
public void StaticHeaders_RejectsMalformedLineWithoutReplacingAppliedOptionsOrSavingSecret()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);
    viewModel.StaticHeaders = "Authorization: Bearer initial";

    viewModel.StaticHeaders = "Authorization: Bearer edited\r\ninvalid";

    settings.GetString("StaticHeaders", string.Empty).Should().Be("Authorization: Bearer initial");
    viewModel.StaticHeaders.Should().Be("Authorization: Bearer edited\r\ninvalid");
    viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer initial");
    viewModel.Status.Should().Be("Static header line 2 must use 'Name: value'.");
    viewModel.Status.Should().NotContain("Bearer edited");
    viewModel.Status.Should().NotContain("Bearer initial");
}
```

- [ ] **Step 7: Run tests to verify red/green behavior**

Run before parser changes if needed to observe failure, then after implementation:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "FullyQualifiedName~NinaOtelOptionsViewModelTests" --logger "console;verbosity=normal"
```

Expected after implementation: all `NinaOtelOptionsViewModelTests` pass.

## Task 2: Options XAML Binding

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Test: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing XAML binding test**

Update the existing theory in `OptionsXamlTests.cs`:

```csharp
[InlineData("StaticHeaders")]
[InlineData("SpoolPath")]
[InlineData("MaxSpoolSizeGb")]
[InlineData("MaxSpoolAgeDays")]
public void OptionsTemplate_TextBoxesUseTwoWayLostFocusBindings(string propertyName)
```

Rename the test method from `OptionsTemplate_SpoolTextBoxesUseTwoWayLostFocusBindings` to `OptionsTemplate_TextBoxesUseTwoWayLostFocusBindings`.

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~OptionsXamlTests" --logger "console;verbosity=normal"
```

Expected: fail because no `StaticHeaders` text box exists.

- [ ] **Step 3: Add multiline static headers editor to XAML**

In `Options.xaml`, extend the first collector settings grid to include a third row:

```xml
<RowDefinition Height="8" />
<RowDefinition Height="Auto" />
```

Add the label and text box:

```xml
<TextBlock Grid.Row="4" Grid.Column="0" Text="Static headers:" FontWeight="SemiBold" VerticalAlignment="Top" />
<TextBox
    Grid.Row="4"
    Grid.Column="2"
    MinWidth="320"
    MinHeight="56"
    AcceptsReturn="True"
    TextWrapping="NoWrap"
    VerticalScrollBarVisibility="Auto"
    Text="{Binding NinaOtelOptionsViewModel.StaticHeaders, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />
```

- [ ] **Step 4: Run XAML test**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~OptionsXamlTests" --logger "console;verbosity=normal"
```

Expected: `OptionsXamlTests` pass.

## Task 3: Verification and Commit

**Files:**
- Verify all modified files.

- [ ] **Step 1: Run targeted tests**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "FullyQualifiedName~NinaOtelOptionsViewModelTests" --logger "console;verbosity=normal"
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~OptionsXamlTests" --logger "console;verbosity=normal"
```

Expected: targeted tests pass. If local NuGet availability causes `NU1301`, report that explicitly and run any available no-restore build/test command that does not contact NuGet.

- [ ] **Step 2: Run formatting/whitespace check**

Run:

```bash
git diff --check
```

Expected: no whitespace errors.

- [ ] **Step 3: Review the diff**

Run:

```bash
git diff -- src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
```

Check:

- Valid headers are saved and applied to `Options.Otlp.Headers`.
- Invalid header edits stay visible but do not replace last valid applied headers.
- Status messages never include header values.
- XAML uses `Mode=TwoWay` and `UpdateSourceTrigger=LostFocus`.

- [ ] **Step 4: Commit**

Run:

```bash
git add src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Expose OTLP static headers in options"
```

Expected: one implementation commit.

## Self-Review

- Spec coverage: Implements the v1 requirement for static OTLP headers in the NINA options UI. Bearer helper, Basic helper, token file reload, protected storage, and mTLS are explicitly left for later slices.
- Placeholder scan: No forbidden placeholder terms.
- Type consistency: Plan uses existing `NinaOtelOptionsViewModel`, `StaticHeaders`, `OtlpOptions.Headers`, `Options.xaml`, and existing test projects.
