# OTLP Auth Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add user-editable OTLP Bearer token and Basic auth helpers that store secrets protected at rest and generate the effective `Authorization` header without exposing secrets in status text.

**Architecture:** Keep static headers as the generic path, then layer an explicit auth helper over them. The options view model owns profile persistence, decrypts protected secrets only in memory, and builds effective OTLP headers. A tiny `ISecretProtector` seam isolates Windows DPAPI for production and allows deterministic unit tests.

**Tech Stack:** C# 12, .NET 8, NINA WPF plugin resource dictionary, Windows DPAPI via `System.Security.Cryptography.ProtectedData`, xUnit/FluentAssertions.

---

## File Structure

- Modify `src/NinaOtel.Core/Options/NinaOtelOptions.cs`
  - Add `OtlpAuthenticationMode` and an auth mode field to `OtlpAuthOptions`.
  - Keep `ToString()` redacted.
- Create `src/NinaOtel.Plugin/Options/ISecretProtector.cs`
  - Small test seam for protect/unprotect.
- Create `src/NinaOtel.Plugin/Options/DpapiSecretProtector.cs`
  - Production DPAPI implementation using UTF-8 and `DataProtectionScope.CurrentUser`.
- Modify `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Add auth mode, Bearer token, Basic username/password storage, generated header merge, and protected storage handling.
- Modify `src/NinaOtel.Plugin/Options/Options.xaml`
  - Add auth mode selector, Bearer `PasswordBox`, Basic username `TextBox`, and Basic password `PasswordBox`.
- Modify `src/NinaOtel.Plugin/Options/Options.xaml.cs`
  - Add `PasswordBox` load/lost-focus handlers that resolve `NinaOtelPlugin.NinaOtelOptionsViewModel`.
- Modify `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - Add fake protector tests for protected storage, generated headers, precedence, clearing, and decrypt failure.
- Modify `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`
  - Assert auth UI bindings and that password boxes are event-driven, not password-bound.

---

### Task 1: Core Auth Mode Shape

**Files:**
- Modify: `src/NinaOtel.Core/Options/NinaOtelOptions.cs`
- Test: `tests/NinaOtel.Core.Tests/Options/NinaOtelOptionsTests.cs`

- [ ] **Step 1: Write failing test for redacted auth mode output**

Add this test to `NinaOtelOptionsTests`:

```csharp
[Fact]
public void AuthOptions_ToString_RedactsModeAndSecretPresence()
{
    var options = new OtlpAuthOptions
    {
        Mode = OtlpAuthenticationMode.Basic,
        BearerToken = "bearer-secret",
        BasicUsername = "jasper",
        BasicPasswordProtected = "basic-ciphertext",
        ClientCertificatePfxPasswordProtected = "pfx-ciphertext",
    };

    var text = options.ToString();

    text.Should().Contain("Mode = Basic");
    text.Should().Contain("BearerTokenConfigured = True");
    text.Should().Contain("BasicUsernameConfigured = True");
    text.Should().Contain("BasicPasswordConfigured = True");
    text.Should().NotContain("bearer-secret");
    text.Should().NotContain("basic-ciphertext");
    text.Should().NotContain("pfx-ciphertext");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter AuthOptions_ToString_RedactsModeAndSecretPresence
```

Expected: FAIL because `OtlpAuthenticationMode` and `Mode` do not exist.

- [ ] **Step 3: Implement minimal core auth mode**

In `NinaOtelOptions.cs`, add near `OtlpProtocol`:

```csharp
public enum OtlpAuthenticationMode
{
    None,
    BearerToken,
    Basic,
}
```

Add to `OtlpAuthOptions`:

```csharp
public OtlpAuthenticationMode Mode { get; init; } = OtlpAuthenticationMode.None;
```

Update `ToString()` so it includes:

```csharp
$"{nameof(Mode)} = {Mode}, " +
```

and still only reports configured booleans for secrets.

- [ ] **Step 4: Run test to verify it passes**

Run the same filtered test. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NinaOtel.Core/Options/NinaOtelOptions.cs tests/NinaOtel.Core.Tests/Options/NinaOtelOptionsTests.cs
git commit -m "Add OTLP auth mode options"
```

---

### Task 2: Protected Auth Storage And Generated Headers

**Files:**
- Create: `src/NinaOtel.Plugin/Options/ISecretProtector.cs`
- Create: `src/NinaOtel.Plugin/Options/DpapiSecretProtector.cs`
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Test: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`

- [ ] **Step 1: Write failing tests for Bearer helper**

Add a fake protector to the bottom of `NinaOtelOptionsViewModelTests`:

```csharp
private sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string secret) => $"protected:{secret}";

    public bool TryUnprotect(string protectedSecret, out string secret)
    {
        if (protectedSecret.StartsWith("protected:", StringComparison.Ordinal))
        {
            secret = protectedSecret["protected:".Length..];
            return true;
        }

        secret = string.Empty;
        return false;
    }
}
```

Add this test:

```csharp
[Fact]
public void BearerAuth_SavesProtectedTokenAndGeneratesAuthorizationHeader()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());

    viewModel.AuthenticationMode = OtlpAuthenticationMode.BearerToken;
    viewModel.SetBearerToken("secret-token");

    settings.GetString("BearerTokenProtected", string.Empty).Should().Be("protected:secret-token");
    settings.GetString("BearerTokenProtected", string.Empty).Should().NotBe("secret-token");
    viewModel.GetBearerToken().Should().Be("secret-token");
    viewModel.Options.Otlp.Auth.Mode.Should().Be(OtlpAuthenticationMode.BearerToken);
    viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer secret-token");
    viewModel.Status.Should().Be("Settings saved");
    viewModel.Status.Should().NotContain("secret-token");
}
```

- [ ] **Step 2: Write failing tests for Basic helper and precedence**

Add:

```csharp
[Fact]
public void BasicAuth_SavesProtectedPasswordAndOverridesStaticAuthorizationHeader()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
    viewModel.StaticHeaders = "Authorization: Bearer static\r\nx-scope: nina";

    viewModel.AuthenticationMode = OtlpAuthenticationMode.Basic;
    viewModel.BasicUsername = "jasper";
    viewModel.SetBasicPassword("plain-password");

    settings.GetString("BasicUsername", string.Empty).Should().Be("jasper");
    settings.GetString("BasicPasswordProtected", string.Empty).Should().Be("protected:plain-password");
    viewModel.GetBasicPassword().Should().Be("plain-password");
    viewModel.Options.Otlp.Auth.Mode.Should().Be(OtlpAuthenticationMode.Basic);
    viewModel.Options.Otlp.Auth.BasicUsername.Should().Be("jasper");
    viewModel.Options.Otlp.Auth.BasicPasswordProtected.Should().Be("protected:plain-password");
    viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
    viewModel.Options.Otlp.Headers.Should().Contain(
        "Authorization",
        "Basic amFzcGVyOnBsYWluLXBhc3N3b3Jk");
}

[Fact]
public void AuthModeNone_PreservesStaticAuthorizationHeader()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
    viewModel.StaticHeaders = "Authorization: Bearer static";

    viewModel.AuthenticationMode = OtlpAuthenticationMode.None;

    viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer static");
}
```

- [ ] **Step 3: Write failing tests for clearing and decrypt failure**

Add:

```csharp
[Fact]
public void ClearingBearerToken_RemovesGeneratedAuthorizationHeaderAndProtectedValue()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
    viewModel.AuthenticationMode = OtlpAuthenticationMode.BearerToken;
    viewModel.SetBearerToken("secret-token");

    viewModel.SetBearerToken(string.Empty);

    settings.GetString("BearerTokenProtected", "fallback").Should().BeEmpty();
    viewModel.GetBearerToken().Should().BeEmpty();
    viewModel.Options.Otlp.Headers.Should().NotContainKey("Authorization");
}

[Fact]
public void Constructor_WhenProtectedBearerCannotDecrypt_DoesNotGenerateAuthorizationHeader()
{
    var settings = new InMemoryPluginSettingsStore();
    settings.SetString("AuthenticationMode", OtlpAuthenticationMode.BearerToken.ToString());
    settings.SetString("BearerTokenProtected", "unreadable-ciphertext");

    var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());

    viewModel.GetBearerToken().Should().BeEmpty();
    viewModel.Options.Otlp.Headers.Should().NotContainKey("Authorization");
    viewModel.Status.Should().Be("Bearer token could not be decrypted; re-enter it.");
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter "BearerAuth|BasicAuth|AuthModeNone|ClearingBearerToken|Constructor_WhenProtectedBearerCannotDecrypt"
```

Expected: FAIL because the auth properties, methods, and protector seam do not exist.

- [ ] **Step 5: Implement the secret protector seam**

Create `ISecretProtector.cs`:

```csharp
namespace NinaOtel.Plugin.Options;

public interface ISecretProtector
{
    string Protect(string secret);
    bool TryUnprotect(string protectedSecret, out string secret);
}
```

Create `DpapiSecretProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Plugin.Options;

internal sealed class DpapiSecretProtector : ISecretProtector
{
    public static DpapiSecretProtector Instance { get; } = new();

    private DpapiSecretProtector()
    {
    }

    public string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(secretBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public bool TryUnprotect(string protectedSecret, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return true;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedSecret);
            var secretBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            secret = Encoding.UTF8.GetString(secretBytes);
            return true;
        }
        catch
        {
            secret = string.Empty;
            return false;
        }
    }
}
```

- [ ] **Step 6: Implement view model auth settings**

In `NinaOtelOptionsViewModel`:

Add keys:

```csharp
private const string AuthenticationModeKey = nameof(AuthenticationMode);
private const string BearerTokenProtectedKey = nameof(BearerTokenProtected);
private const string BasicUsernameKey = nameof(BasicUsername);
private const string BasicPasswordProtectedKey = nameof(BasicPasswordProtected);
```

Add fields:

```csharp
private readonly ISecretProtector secretProtector;
private OtlpAuthenticationMode authenticationMode;
private string bearerToken = string.Empty;
private string bearerTokenProtected = string.Empty;
private string basicUsername = string.Empty;
private string basicPassword = string.Empty;
private string basicPasswordProtected = string.Empty;
```

Change the constructor to:

```csharp
public NinaOtelOptionsViewModel(INinaOtelSettingsStore settingsStore)
    : this(settingsStore, DpapiSecretProtector.Instance)
{
}

internal NinaOtelOptionsViewModel(INinaOtelSettingsStore settingsStore, ISecretProtector secretProtector)
{
    this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    this.secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    LoadFromSettings();
}
```

Add public members:

```csharp
public IReadOnlyList<OtlpAuthenticationMode> AvailableAuthenticationModes { get; } =
    Enum.GetValues<OtlpAuthenticationMode>();

public OtlpAuthenticationMode AuthenticationMode
{
    get => authenticationMode;
    set
    {
        if (SetField(ref authenticationMode, value))
        {
            settingsStore.SetString(AuthenticationModeKey, value.ToString());
            Status = "Settings saved";
        }
    }
}

public string BasicUsername
{
    get => basicUsername;
    set
    {
        if (SetField(ref basicUsername, value ?? string.Empty))
        {
            settingsStore.SetString(BasicUsernameKey, basicUsername);
            Status = "Settings saved";
        }
    }
}

public string GetBearerToken() => bearerToken;

public void SetBearerToken(string? token)
{
    SetProtectedSecret(
        token,
        ref bearerToken,
        ref bearerTokenProtected,
        BearerTokenProtectedKey,
        nameof(GetBearerToken));
}

public string GetBasicPassword() => basicPassword;

public void SetBasicPassword(string? password)
{
    SetProtectedSecret(
        password,
        ref basicPassword,
        ref basicPasswordProtected,
        BasicPasswordProtectedKey,
        nameof(GetBasicPassword));
}
```

Load persisted values in `LoadFromSettings()`:

```csharp
authenticationMode = LoadAuthenticationMode();
basicUsername = settingsStore.GetString(BasicUsernameKey, string.Empty);
bearerTokenProtected = settingsStore.GetString(BearerTokenProtectedKey, string.Empty);
basicPasswordProtected = settingsStore.GetString(BasicPasswordProtectedKey, string.Empty);
bearerToken = UnprotectOrWarn(bearerTokenProtected, "Bearer token");
basicPassword = UnprotectOrWarn(basicPasswordProtected, "Basic password");
```

Raise changed properties:

```csharp
RaisePropertyChanged(nameof(AuthenticationMode));
RaisePropertyChanged(nameof(BasicUsername));
RaisePropertyChanged(nameof(AvailableAuthenticationModes));
```

Add helpers:

```csharp
private OtlpAuthenticationMode LoadAuthenticationMode()
{
    var configured = settingsStore.GetString(
        AuthenticationModeKey,
        OtlpAuthenticationMode.None.ToString());

    return Enum.TryParse<OtlpAuthenticationMode>(configured, ignoreCase: true, out var mode)
        ? mode
        : OtlpAuthenticationMode.None;
}

private string UnprotectOrWarn(string protectedSecret, string label)
{
    if (string.IsNullOrEmpty(protectedSecret))
    {
        return string.Empty;
    }

    if (secretProtector.TryUnprotect(protectedSecret, out var secret))
    {
        return secret;
    }

    Status = $"{label} could not be decrypted; re-enter it.";
    return string.Empty;
}

private void SetProtectedSecret(
    string? value,
    ref string secretField,
    ref string protectedField,
    string settingsKey,
    string propertyName)
{
    var normalized = value ?? string.Empty;
    if (secretField == normalized)
    {
        return;
    }

    secretField = normalized;
    protectedField = string.IsNullOrEmpty(normalized)
        ? string.Empty
        : secretProtector.Protect(normalized);
    settingsStore.SetString(settingsKey, protectedField);
    RaisePropertyChanged(propertyName);
    RaisePropertyChanged(nameof(Options));
    Status = "Settings saved";
}
```

Update `CreateOptions()` to call a new effective-header helper:

```csharp
Headers = CreateEffectiveHeaders(),
Auth = new OtlpAuthOptions
{
    Mode = authenticationMode,
    BearerToken = authenticationMode == OtlpAuthenticationMode.BearerToken && !string.IsNullOrEmpty(bearerToken)
        ? bearerToken
        : null,
    BasicUsername = authenticationMode == OtlpAuthenticationMode.Basic && !string.IsNullOrEmpty(basicUsername)
        ? basicUsername
        : null,
    BasicPasswordProtected = authenticationMode == OtlpAuthenticationMode.Basic && !string.IsNullOrEmpty(basicPasswordProtected)
        ? basicPasswordProtected
        : null,
},
```

Add:

```csharp
private IReadOnlyDictionary<string, string> CreateEffectiveHeaders()
{
    var headers = new Dictionary<string, string>(appliedStaticHeaders, StringComparer.OrdinalIgnoreCase);
    switch (authenticationMode)
    {
        case OtlpAuthenticationMode.BearerToken when !string.IsNullOrEmpty(bearerToken):
            headers["Authorization"] = $"Bearer {bearerToken}";
            break;
        case OtlpAuthenticationMode.Basic
            when !string.IsNullOrEmpty(basicUsername) && !string.IsNullOrEmpty(basicPassword):
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{basicUsername}:{basicPassword}"));
            headers["Authorization"] = $"Basic {credentials}";
            break;
    }

    return headers;
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run the filtered plugin test command from Step 4. Expected: PASS.

- [ ] **Step 8: Run broader options tests**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter NinaOtelOptionsViewModelTests
```

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/NinaOtel.Core/Options/NinaOtelOptions.cs src/NinaOtel.Plugin/Options/ISecretProtector.cs src/NinaOtel.Plugin/Options/DpapiSecretProtector.cs src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs tests/NinaOtel.Core.Tests/Options/NinaOtelOptionsTests.cs tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs
git commit -m "Add protected OTLP auth helpers"
```

---

### Task 3: Auth Helper Options UI

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml.cs`
- Test: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing XAML tests**

Add tests to `OptionsXamlTests`:

```csharp
[Fact]
public void OptionsTemplate_AuthenticationModeComboBoxUsesTwoWayBinding()
{
    var document = XDocument.Load(FindOptionsXamlPath());

    var comboBox = document
        .Descendants(PresentationNamespace + "ComboBox")
        .Single(element => element.Attribute("SelectedItem")?.Value.Contains("AuthenticationMode", StringComparison.Ordinal) == true);

    comboBox.Attribute("ItemsSource")?.Value.Should().Contain("AvailableAuthenticationModes");
    comboBox.Attribute("SelectedItem")?.Value.Should().Contain("Mode=TwoWay");
}

[Fact]
public void OptionsTemplate_BasicUsernameUsesTwoWayLostFocusBinding()
{
    var document = XDocument.Load(FindOptionsXamlPath());

    var textbox = SingleTextBoxBoundTo(document, "BasicUsername");
    textbox.Attribute("Text")?.Value.Should().Contain("Mode=TwoWay");
    textbox.Attribute("Text")?.Value.Should().Contain("UpdateSourceTrigger=LostFocus");
}

[Theory]
[InlineData("BearerTokenPasswordBox_Loaded", "BearerTokenPasswordBox_LostFocus")]
[InlineData("BasicPasswordBox_Loaded", "BasicPasswordBox_LostFocus")]
public void OptionsTemplate_SecretsUsePasswordBoxesWithoutPasswordBinding(string loadedHandler, string lostFocusHandler)
{
    var document = XDocument.Load(FindOptionsXamlPath());

    var passwordBox = document
        .Descendants(PresentationNamespace + "PasswordBox")
        .Single(element =>
            element.Attribute("Loaded")?.Value == loadedHandler &&
            element.Attribute("LostFocus")?.Value == lostFocusHandler);

    passwordBox.Attributes().Should().NotContain(attribute => attribute.Name.LocalName == "Password");
}
```

- [ ] **Step 2: Run XAML tests to verify they fail**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter OptionsTemplate
```

Expected: FAIL because the auth UI controls do not exist.

- [ ] **Step 3: Add auth UI controls**

In `Options.xaml`, extend the first settings `Grid` row definitions to include rows for auth mode, Bearer token, Basic username, and Basic password. Add controls after the static headers row:

```xml
<TextBlock Grid.Row="6" Grid.Column="0" Text="Authentication:" FontWeight="SemiBold" VerticalAlignment="Center" />
<ComboBox
    Grid.Row="6"
    Grid.Column="2"
    Width="160"
    HorizontalAlignment="Left"
    ItemsSource="{Binding NinaOtelOptionsViewModel.AvailableAuthenticationModes}"
    SelectedItem="{Binding NinaOtelOptionsViewModel.AuthenticationMode, Mode=TwoWay}" />

<TextBlock Grid.Row="8" Grid.Column="0" Text="Bearer token:" FontWeight="SemiBold" VerticalAlignment="Center" />
<PasswordBox
    Grid.Row="8"
    Grid.Column="2"
    MinWidth="320"
    Loaded="BearerTokenPasswordBox_Loaded"
    LostFocus="BearerTokenPasswordBox_LostFocus" />

<TextBlock Grid.Row="10" Grid.Column="0" Text="Basic username:" FontWeight="SemiBold" VerticalAlignment="Center" />
<TextBox
    Grid.Row="10"
    Grid.Column="2"
    MinWidth="320"
    Text="{Binding NinaOtelOptionsViewModel.BasicUsername, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />

<TextBlock Grid.Row="12" Grid.Column="0" Text="Basic password:" FontWeight="SemiBold" VerticalAlignment="Center" />
<PasswordBox
    Grid.Row="12"
    Grid.Column="2"
    MinWidth="320"
    Loaded="BasicPasswordBox_Loaded"
    LostFocus="BasicPasswordBox_LostFocus" />
```

Add corresponding `RowDefinition Height="8"` separators before each new row. Keep the existing static headers control intact.

- [ ] **Step 4: Add code-behind password handlers**

Inside the `#if NINAOTEL_WPF` block in `Options.xaml.cs`, add `using System.Windows.Controls;` and these methods:

```csharp
private void BearerTokenPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
    LoadPassword(sender, static viewModel => viewModel.GetBearerToken());

private void BearerTokenPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
    SavePassword(sender, static (viewModel, password) => viewModel.SetBearerToken(password));

private void BasicPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
    LoadPassword(sender, static viewModel => viewModel.GetBasicPassword());

private void BasicPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
    SavePassword(sender, static (viewModel, password) => viewModel.SetBasicPassword(password));

private static void LoadPassword(object sender, Func<NinaOtelOptionsViewModel, string> getPassword)
{
    if (sender is PasswordBox passwordBox && TryGetViewModel(passwordBox, out var viewModel))
    {
        passwordBox.Password = getPassword(viewModel);
    }
}

private static void SavePassword(
    object sender,
    Action<NinaOtelOptionsViewModel, string> setPassword)
{
    if (sender is PasswordBox passwordBox && TryGetViewModel(passwordBox, out var viewModel))
    {
        setPassword(viewModel, passwordBox.Password);
    }
}

private static bool TryGetViewModel(FrameworkElement element, out NinaOtelOptionsViewModel viewModel)
{
    switch (element.DataContext)
    {
        case NinaOtelOptionsViewModel direct:
            viewModel = direct;
            return true;
        case NinaOtelPlugin plugin:
            viewModel = plugin.NinaOtelOptionsViewModel;
            return true;
        default:
            viewModel = null!;
            return false;
    }
}
```

- [ ] **Step 5: Run XAML tests to verify they pass**

Run the filtered `OptionsTemplate` command from Step 2. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/NinaOtel.Plugin/Options/Options.xaml src/NinaOtel.Plugin/Options/Options.xaml.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Expose OTLP auth helpers in options UI"
```

---

### Task 4: Verification And Release Bump

**Files:**
- Modify: `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs`

- [ ] **Step 1: Run static diff check**

Run:

```bash
git diff --check
```

Expected: exit 0.

- [ ] **Step 2: Run local build checks**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore
"$DOTNET" build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj --no-restore
```

Expected: Core build exits 0. Plugin build may fail locally with `NU1301`; if it does, record that local NuGet is the blocker and rely on GitHub Windows CI after push.

- [ ] **Step 3: Run targeted tests**

Run:

```bash
DOTNET=$(mise which dotnet)
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore --filter "AuthOptions|OptionsTemplate"
"$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore --filter NinaOtelOptionsViewModelTests
```

Expected: Tests should produce real pass/fail output. If local `dotnet test --no-restore` returns exit 0 with no test output, do not count it as evidence.

- [ ] **Step 4: Bump version to alpha 41**

In `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs`, change:

```csharp
[assembly: AssemblyVersion("0.1.0.40")]
[assembly: AssemblyFileVersion("0.1.0.40")]
[assembly: AssemblyInformationalVersion("0.1.0-alpha.41")]
```

- [ ] **Step 5: Commit bump**

```bash
git add src/NinaOtel.Plugin/Properties/AssemblyInfo.cs
git commit -m "Bump NinaOtel to alpha 41"
```

- [ ] **Step 6: Tag and push**

```bash
git tag v0.1.0-alpha.41
TOKEN=$(gh auth token --user japz)
BASIC=$(printf 'x-access-token:%s' "$TOKEN" | base64 | tr -d '\n')
git -c credential.helper= -c http.https://github.com/.extraheader="AUTHORIZATION: basic $BASIC" push origin main v0.1.0-alpha.41
```

- [ ] **Step 7: Watch GitHub workflows**

Run:

```bash
GH_TOKEN=$(gh auth token --user japz) gh run list --repo japz/nina-otel --limit 6
```

Then watch the new Build and Release runs until both are completed:

```bash
GH_TOKEN=$(gh auth token --user japz) gh run watch <run-id> --repo japz/nina-otel --exit-status
```

Expected: both workflows succeed.

- [ ] **Step 8: Verify release assets**

Run:

```bash
GH_TOKEN=$(gh auth token --user japz) gh release view v0.1.0-alpha.41 --repo japz/nina-otel --json tagName,url,assets
```

Expected: release has `manifest.json` and `NinaOtel.Plugin.zip`.

---

## Self-Review

- Spec coverage: This plan implements the approved design’s static-header-adjacent auth helpers for Bearer and Basic auth, masks secrets in UI via `PasswordBox`, and stores secrets protected at rest through DPAPI. Token-file reload and mTLS are intentionally left for later slices.
- Placeholder scan: no placeholder markers.
- Type consistency: `OtlpAuthenticationMode`, `ISecretProtector`, `GetBearerToken`, `SetBearerToken`, `GetBasicPassword`, and `SetBasicPassword` are introduced before use.
- Scope check: This is one bounded, independently releasable slice.
