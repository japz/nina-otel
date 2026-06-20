# OTLP PEM TLS And mTLS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users configure OTLP CA certificate, client certificate, and client private key PEM paths so NinaOtel can connect to collectors using private CA TLS or PEM mTLS.

**Architecture:** Keep certificate path settings in the existing `OtlpAuthOptions` model and NINA options view model. Add a core HTTP client factory helper that creates a `HttpClientHandler` with optional custom-root CA validation and optional client certificate loading, then use that helper for both SDK-backed OTLP logs/live metrics and NinaOtel custom trace/point-in-time metric exporters. Do not add certificate validation bypasses, PFX support, Windows certificate store support, or token-file auth in this slice.

**Tech Stack:** C# 12, .NET 8, WPF XAML, xUnit, FluentAssertions, `System.Security.Cryptography.X509Certificates`, OpenTelemetry OTLP exporter `HttpClientFactory`.

---

## Target File Structure

- Create: `src/NinaOtel.Core/Pipeline/OtlpHttpClientFactory.cs`
  - Builds `HttpClient`/`HttpClientHandler` for OTLP exporters.
  - Applies CA PEM trust when `OtlpOptions.Auth.CaCertificatePemPath` is configured.
  - Applies client certificate PEM/private-key PEM when `ClientCertificatePemPath` is configured.
- Modify: `src/NinaOtel.Core/Pipeline/OtlpTelemetryExporter.cs`
  - Use the factory through `OtlpExporterOptions.HttpClientFactory` for SDK-backed logs/live metrics.
- Modify: `src/NinaOtel.Core/Pipeline/OtlpPointInTimeMetricExporter.cs`
  - Use the factory for production HTTP clients.
- Modify: `src/NinaOtel.Core/Pipeline/OtlpTraceExporter.cs`
  - Use the factory for production HTTP clients.
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
  - Persist and expose `CaCertificatePemPath`, `ClientCertificatePemPath`, and `ClientPrivateKeyPemPath`.
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
  - Add three text inputs under authentication settings.
- Modify tests:
  - `tests/NinaOtel.Core.Tests/Pipeline/OtlpHttpClientFactoryTests.cs`
  - `tests/NinaOtel.Core.Tests/Pipeline/OtlpTelemetryExporterTests.cs`
  - `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
  - `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

## Non-Goals

- No PFX/P12 support.
- No Windows certificate store lookup.
- No certificate validation bypass.
- No OAuth/OIDC or token-file auth.
- No signal-specific certificate settings.
- No automatic cert file browsing button.

## Task 1: Core TLS HTTP Client Factory

**Files:**
- Create: `tests/NinaOtel.Core.Tests/Pipeline/OtlpHttpClientFactoryTests.cs`
- Create: `src/NinaOtel.Core/Pipeline/OtlpHttpClientFactory.cs`

- [ ] **Step 1: Write failing tests for default handler and invalid cert paths**

Create `tests/NinaOtel.Core.Tests/Pipeline/OtlpHttpClientFactoryTests.cs` with tests that assert:

```csharp
using FluentAssertions;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpHttpClientFactoryTests
{
    [Fact]
    public void Create_WhenNoTlsOptionsAreConfigured_ReturnsHttpClient()
    {
        using var client = OtlpHttpClientFactory.Create(new OtlpOptions());

        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Create_WhenCaCertificatePathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                CaCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };

        Action create = () => OtlpHttpClientFactory.Create(options).Dispose();

        create.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Create_WhenClientCertificatePathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                ClientCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };

        Action create = () => OtlpHttpClientFactory.Create(options).Dispose();

        create.Should().Throw<FileNotFoundException>();
    }
}
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OtlpHttpClientFactoryTests --no-restore -v:minimal
```

Expected before implementation: compile fails because `OtlpHttpClientFactory` does not exist.

- [ ] **Step 2: Implement minimal factory**

Create `src/NinaOtel.Core/Pipeline/OtlpHttpClientFactory.cs`:

```csharp
using NinaOtel.Core.Options;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpHttpClientFactory
{
    public static HttpClient Create(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = CreateHandler(options);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = NormalizeTimeout(options.Timeout),
        };
        return client;
    }

    internal static HttpClientHandler CreateHandler(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler();
        var auth = options.Auth;
        if (!string.IsNullOrWhiteSpace(auth.CaCertificatePemPath))
        {
            var caCertificate = LoadCertificate(auth.CaCertificatePemPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && ValidateCertificateWithCustomRoot(certificate, caCertificate);
        }

        if (!string.IsNullOrWhiteSpace(auth.ClientCertificatePemPath))
        {
            var clientCertificate = string.IsNullOrWhiteSpace(auth.ClientPrivateKeyPemPath)
                ? LoadCertificate(auth.ClientCertificatePemPath)
                : X509Certificate2.CreateFromPemFile(auth.ClientCertificatePemPath, auth.ClientPrivateKeyPemPath);
            handler.ClientCertificates.Add(clientCertificate);
        }

        return handler;
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Certificate file was not found.", path);
        }

        return X509Certificate2.CreateFromPemFile(path);
    }

    private static bool ValidateCertificateWithCustomRoot(X509Certificate certificate, X509Certificate2 caCertificate)
    {
        using var serverCertificate = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(serverCertificate);
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
}
```

Run the focused test command again. Expected in CI: tests pass.

- [ ] **Step 3: Add client certificate loading test**

Extend `OtlpHttpClientFactoryTests` with a temporary self-signed client certificate helper using `CertificateRequest`, export the certificate and key to PEM files, then assert:

```csharp
var handler = OtlpHttpClientFactory.CreateHandler(options);
handler.ClientCertificates.Should().ContainSingle();
handler.ClientCertificates[0].HasPrivateKey.Should().BeTrue();
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OtlpHttpClientFactoryTests --no-restore -v:minimal
```

Expected in CI: test passes.

Commit:

```bash
git add src/NinaOtel.Core/Pipeline/OtlpHttpClientFactory.cs tests/NinaOtel.Core.Tests/Pipeline/OtlpHttpClientFactoryTests.cs
git commit -m "Add OTLP PEM TLS HTTP client factory"
```

## Task 2: Wire TLS Factory Into All OTLP Exporters

**Files:**
- Modify: `src/NinaOtel.Core/Pipeline/OtlpTelemetryExporter.cs`
- Modify: `src/NinaOtel.Core/Pipeline/OtlpPointInTimeMetricExporter.cs`
- Modify: `src/NinaOtel.Core/Pipeline/OtlpTraceExporter.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/OtlpTelemetryExporterTests.cs`

- [ ] **Step 1: Write failing SDK exporter option test**

In `OtlpTelemetryExporterTests`, add a test around a new internal helper if needed:

```csharp
[Fact]
public void CreateExporterOptions_WhenTlsPathsAreConfigured_UsesHttpClientFactory()
{
    var options = new OtlpOptions
    {
        Auth = new OtlpAuthOptions
        {
            CaCertificatePemPath = "ca.pem",
        },
    };

    var exporterOptions = OtlpTelemetryExporter.CreateExporterOptions(options, "v1/logs");

    exporterOptions.HttpClientFactory.Should().NotBeNull();
}
```

This may require changing `CreateExporterOptions` from `private` to `internal`. Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OtlpTelemetryExporterTests --no-restore -v:minimal
```

Expected before implementation: compile fails or assertion fails because `HttpClientFactory` is not set.

- [ ] **Step 2: Wire SDK-backed logs and live metrics**

In `OtlpTelemetryExporter.CreateExporterOptions`, set:

```csharp
if (HasTlsConfiguration(options))
{
    exporterOptions.HttpClientFactory = () => OtlpHttpClientFactory.Create(options);
}
```

Add:

```csharp
private static bool HasTlsConfiguration(OtlpOptions options) =>
    !string.IsNullOrWhiteSpace(options.Auth.CaCertificatePemPath) ||
    !string.IsNullOrWhiteSpace(options.Auth.ClientCertificatePemPath) ||
    !string.IsNullOrWhiteSpace(options.Auth.ClientPrivateKeyPemPath);
```

Run the focused `OtlpTelemetryExporterTests` command again.

- [ ] **Step 3: Wire custom exporters**

Change production constructors:

```csharp
public OtlpPointInTimeMetricExporter(OtlpOptions options)
    : this(options, OtlpHttpClientFactory.Create(options), ownsHttpClient: true)
{
}

public OtlpTraceExporter(OtlpOptions options)
    : this(options, OtlpHttpClientFactory.Create(options), ownsHttpClient: true)
{
}
```

Keep the existing internal handler constructors unchanged for tests.

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
```

Expected locally: build may fail with `NU1301` in fresh worktrees; if so, record the exact failure. Expected in CI: build passes.

Commit:

```bash
git add src/NinaOtel.Core/Pipeline/OtlpTelemetryExporter.cs src/NinaOtel.Core/Pipeline/OtlpPointInTimeMetricExporter.cs src/NinaOtel.Core/Pipeline/OtlpTraceExporter.cs tests/NinaOtel.Core.Tests/Pipeline/OtlpTelemetryExporterTests.cs
git commit -m "Wire OTLP exporters to PEM TLS client factory"
```

## Task 3: Expose PEM TLS Paths In Options UI

**Files:**
- Modify: `src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs`
- Modify: `src/NinaOtel.Plugin/Options/Options.xaml`
- Modify: `tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs`
- Modify: `tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs`

- [ ] **Step 1: Write failing view model tests**

In `Constructor_LoadsDefaultSettingsWhenStoreIsEmpty`, assert the three path properties are empty and `Options.Otlp.Auth` has null path values.

In `Constructor_LoadsPersistedSettingsFromStore`, set:

```csharp
settings.SetString("CaCertificatePemPath", "C:\\certs\\ca.pem");
settings.SetString("ClientCertificatePemPath", "C:\\certs\\client.pem");
settings.SetString("ClientPrivateKeyPemPath", "C:\\certs\\client-key.pem");
```

Assert the view model properties and `Options.Otlp.Auth` carry those exact values.

Add a save test:

```csharp
[Fact]
public void TlsCertificatePaths_SaveImmediately()
{
    var settings = new InMemoryPluginSettingsStore();
    var viewModel = new NinaOtelOptionsViewModel(settings);

    viewModel.CaCertificatePemPath = "C:\\certs\\ca.pem";
    viewModel.ClientCertificatePemPath = "C:\\certs\\client.pem";
    viewModel.ClientPrivateKeyPemPath = "C:\\certs\\client-key.pem";

    settings.GetString("CaCertificatePemPath", string.Empty).Should().Be("C:\\certs\\ca.pem");
    settings.GetString("ClientCertificatePemPath", string.Empty).Should().Be("C:\\certs\\client.pem");
    settings.GetString("ClientPrivateKeyPemPath", string.Empty).Should().Be("C:\\certs\\client-key.pem");
    viewModel.Options.Otlp.Auth.CaCertificatePemPath.Should().Be("C:\\certs\\ca.pem");
    viewModel.Options.Otlp.Auth.ClientCertificatePemPath.Should().Be("C:\\certs\\client.pem");
    viewModel.Options.Otlp.Auth.ClientPrivateKeyPemPath.Should().Be("C:\\certs\\client-key.pem");
    viewModel.Status.Should().Be("Settings saved");
}
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~NinaOtelOptionsViewModelTests --no-restore -v:minimal
```

Expected before implementation: compile fails.

- [ ] **Step 2: Implement view model properties**

Add setting keys/fields/properties:

```csharp
private const string CaCertificatePemPathKey = nameof(CaCertificatePemPath);
private const string ClientCertificatePemPathKey = nameof(ClientCertificatePemPath);
private const string ClientPrivateKeyPemPathKey = nameof(ClientPrivateKeyPemPath);
private string caCertificatePemPath = string.Empty;
private string clientCertificatePemPath = string.Empty;
private string clientPrivateKeyPemPath = string.Empty;
```

Properties should save trimmed strings immediately and raise `Options`:

```csharp
public string CaCertificatePemPath
{
    get => caCertificatePemPath;
    set => SetPathSetting(ref caCertificatePemPath, value, CaCertificatePemPathKey);
}
```

Use a shared helper:

```csharp
private void SetPathSetting(ref string field, string? value, string settingsKey, [CallerMemberName] string propertyName = "")
{
    var normalized = value?.Trim() ?? string.Empty;
    if (SetField(ref field, normalized))
    {
        settingsStore.SetString(settingsKey, normalized);
        Status = "Settings saved";
    }
}
```

Load values in `LoadFromSettings`, raise property changes on reload, and include them in `CreateOptions().Otlp.Auth`.

Run the focused view model test command again. Expected in CI: pass.

- [ ] **Step 3: Add XAML bindings**

Add three rows below Basic password:

```xml
<TextBlock Grid.Row="14" Grid.Column="0" Text="CA PEM:" FontWeight="SemiBold" VerticalAlignment="Center" />
<TextBox Grid.Row="14" Grid.Column="2" MinWidth="320" Text="{Binding NinaOtelOptionsViewModel.CaCertificatePemPath, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />

<TextBlock Grid.Row="16" Grid.Column="0" Text="Client cert PEM:" FontWeight="SemiBold" VerticalAlignment="Center" />
<TextBox Grid.Row="16" Grid.Column="2" MinWidth="320" Text="{Binding NinaOtelOptionsViewModel.ClientCertificatePemPath, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />

<TextBlock Grid.Row="18" Grid.Column="0" Text="Client key PEM:" FontWeight="SemiBold" VerticalAlignment="Center" />
<TextBox Grid.Row="18" Grid.Column="2" MinWidth="320" Text="{Binding NinaOtelOptionsViewModel.ClientPrivateKeyPemPath, Mode=TwoWay, UpdateSourceTrigger=LostFocus}" />
```

Remember to add matching `RowDefinition` spacer/content rows.

Extend `OptionsXamlTests` inline data to include all three property names.

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~OptionsXamlTests --no-restore -v:minimal
```

Expected in CI: pass.

Commit:

```bash
git add src/NinaOtel.Plugin/Options/NinaOtelOptionsViewModel.cs src/NinaOtel.Plugin/Options/Options.xaml tests/NinaOtel.Plugin.Tests/Options/NinaOtelOptionsViewModelTests.cs tests/NinaOtel.Core.Tests/Plugin/OptionsXamlTests.cs
git commit -m "Expose OTLP PEM TLS settings in options"
```

## Task 4: Verification

Run:

```bash
git diff --check
bash tests/package-plugin-tests.sh
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj --no-restore -v:minimal
```

Expected:
- `git diff --check`: exit `0`.
- package script: exit `0`.
- local dotnet commands may fail with `NU1301` or missing `project.assets.json` if restore is blocked. Record exact output; GitHub Windows CI is the release gate.

## Self-Review Checklist

- Spec coverage: PEM CA trust and PEM client cert/key mTLS are represented in options, UI, SDK exporters, and custom exporters.
- Scope control: no PFX, Windows cert store, token-file auth, or validation bypass.
- Safety: configured bad certificate paths fail exporter creation/export visibly; they do not affect NINA outside the telemetry path.
- Secrets: private key path is not a secret value; do not log file contents.
- Placeholder scan: no unfinished placeholder markers.
