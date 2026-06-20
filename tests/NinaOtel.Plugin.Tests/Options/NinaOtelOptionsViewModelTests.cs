using FluentAssertions;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;
using NinaOtel.Plugin.Options;
using Xunit;

namespace NinaOtel.Plugin.Tests.Options;

public sealed class NinaOtelOptionsViewModelTests
{
    [Fact]
    public void Constructor_LoadsDefaultSettingsWhenStoreIsEmpty()
    {
        var settings = new InMemoryPluginSettingsStore();

        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorEndpoint.Should().Be("http://localhost:4317/");
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.Grpc);
        viewModel.DiskOnFailureEnabled.Should().BeTrue();
        viewModel.Options.Otlp.Endpoint.Should().Be(new Uri("http://localhost:4317/"));
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.Grpc);
        viewModel.Options.Buffer.DiskOnFailureEnabled.Should().BeTrue();
        viewModel.SpoolPath.Should().Be("%LOCALAPPDATA%\\NINA\\NinaOtel\\spool");
        viewModel.MaxSpoolSizeGb.Should().Be("1");
        viewModel.MaxSpoolAgeDays.Should().Be("7");
        viewModel.Options.Buffer.SpoolPath.Should().Be("%LOCALAPPDATA%\\NINA\\NinaOtel\\spool");
        viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(1L * 1024 * 1024 * 1024);
        viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(7));
        viewModel.StaticHeaders.Should().BeEmpty();
        viewModel.Options.Otlp.Headers.Should().BeEmpty();
        viewModel.CaCertificatePemPath.Should().BeEmpty();
        viewModel.ClientCertificatePemPath.Should().BeEmpty();
        viewModel.ClientPrivateKeyPemPath.Should().BeEmpty();
        viewModel.Options.Otlp.Auth.CaCertificatePemPath.Should().BeNull();
        viewModel.Options.Otlp.Auth.ClientCertificatePemPath.Should().BeNull();
        viewModel.Options.Otlp.Auth.ClientPrivateKeyPemPath.Should().BeNull();
        viewModel.CollectorHealthState.Should().Be(CollectorHealthState.Unknown);
        viewModel.CollectorHealthBrush.Should().Be("#808080");
        viewModel.CollectorHealthSummary.Should().Be("Collector not checked yet");
    }

    [Fact]
    public void Constructor_LoadsFirstPartyAddonsDisabledByDefault()
    {
        var settings = new InMemoryPluginSettingsStore();

        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.Addons.Select(addon => addon.Id)
            .Should()
            .Equal("phd2", "target-scheduler", "night-summary", "onstepx");
        viewModel.Addons.Should().OnlyContain(addon => !addon.IsEnabled);
        viewModel.Addons.Should().OnlyContain(addon => !addon.RawForwardingEnabled);
        viewModel.Options.Addons.Keys
            .Should()
            .Equal("phd2", "target-scheduler", "night-summary", "onstepx");
        viewModel.Options.Addons.Values.Should().OnlyContain(addon => !addon.Enabled);
        viewModel.Options.Addons.Values.Should().OnlyContain(addon => !addon.RawForwardingEnabled);
    }

    [Fact]
    public void AddonSettings_SaveEnabledRawForwardingAndUpdateOptions()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

        phd2.IsEnabled = true;
        phd2.RawForwardingEnabled = true;

        settings.GetBoolean("Addon.phd2.Enabled", false).Should().BeTrue();
        settings.GetBoolean("Addon.phd2.RawForwardingEnabled", false).Should().BeTrue();
        viewModel.Options.Addons["phd2"].Enabled.Should().BeTrue();
        viewModel.Options.Addons["phd2"].RawForwardingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Constructor_LoadsPersistedAddonSettings()
    {
        var settings = new InMemoryPluginSettingsStore();
        settings.SetBoolean("Addon.phd2.Enabled", true);
        settings.SetBoolean("Addon.phd2.RawForwardingEnabled", true);

        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

        phd2.IsEnabled.Should().BeTrue();
        phd2.RawForwardingEnabled.Should().BeTrue();
        viewModel.Options.Addons["phd2"].Enabled.Should().BeTrue();
        viewModel.Options.Addons["phd2"].RawForwardingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Phd2AddonSettings_SaveLogPathsAndExposeOptionsSettings()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

        phd2.Phd2DebugLogPath = "C:\\PHD2\\PHD2_DebugLog.txt";
        phd2.Phd2GuideLogPath = "C:\\PHD2\\PHD2_GuideLog.txt";

        settings.GetString("Addon.phd2.DebugLogPath", string.Empty).Should().Be("C:\\PHD2\\PHD2_DebugLog.txt");
        settings.GetString("Addon.phd2.GuideLogPath", string.Empty).Should().Be("C:\\PHD2\\PHD2_GuideLog.txt");
        viewModel.Options.Addons["phd2"].Settings.Should().Contain("DebugLogPath", "C:\\PHD2\\PHD2_DebugLog.txt");
        viewModel.Options.Addons["phd2"].Settings.Should().Contain("GuideLogPath", "C:\\PHD2\\PHD2_GuideLog.txt");
    }

    [Fact]
    public void Constructor_LoadsPersistedPhd2LogPathSettings()
    {
        var settings = new InMemoryPluginSettingsStore();
        settings.SetString("Addon.phd2.DebugLogPath", "D:\\Logs\\debug.txt");
        settings.SetString("Addon.phd2.GuideLogPath", "D:\\Logs\\guide.txt");

        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

        phd2.Phd2DebugLogPath.Should().Be("D:\\Logs\\debug.txt");
        phd2.Phd2GuideLogPath.Should().Be("D:\\Logs\\guide.txt");
        viewModel.Options.Addons["phd2"].Settings.Should().Contain("DebugLogPath", "D:\\Logs\\debug.txt");
        viewModel.Options.Addons["phd2"].Settings.Should().Contain("GuideLogPath", "D:\\Logs\\guide.txt");
    }

    [Fact]
    public void UpdateAddonHealth_UpdatesMatchingAddonStatusAndMessage()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");

        viewModel.UpdateAddonHealth("phd2", "started", "Add-on started.");

        phd2.Status.Should().Be("started");
        phd2.Message.Should().Be("Add-on started.");
    }

    [Fact]
    public void UpdateAddonHealth_DoesNotOverwriteWaitingShellStatusWithGenericStartedStatus()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        var phd2 = viewModel.Addons.Single(addon => addon.Id == "phd2");
        const string shellMessage = "Add-on shell loaded; source collection is not implemented yet.";

        viewModel.UpdateAddonHealth("phd2", "waiting", shellMessage);
        viewModel.UpdateAddonHealth("phd2", "started", "Add-on started.");

        phd2.Status.Should().Be("waiting");
        phd2.Message.Should().Be(shellMessage);
    }

    [Fact]
    public void Constructor_LoadsPersistedSettingsFromStore()
    {
        var settings = new InMemoryPluginSettingsStore();
        settings.SetString("CollectorEndpoint", "http://collector.local:4318/");
        settings.SetString("CollectorProtocol", OtlpProtocol.HttpProtobuf.ToString());
        settings.SetBoolean("DiskOnFailureEnabled", false);
        settings.SetString("SpoolPath", "D:\\NinaOtel\\spool");
        settings.SetString("MaxSpoolSizeGb", "2.5");
        settings.SetString("MaxSpoolAgeDays", "14");
        settings.SetString("StaticHeaders", "Authorization: Bearer abc\r\nx-scope = nina");
        settings.SetString("CaCertificatePemPath", "C:\\certs\\ca.pem");
        settings.SetString("ClientCertificatePemPath", "C:\\certs\\client.pem");
        settings.SetString("ClientPrivateKeyPemPath", "C:\\certs\\client-key.pem");

        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorEndpoint.Should().Be("http://collector.local:4318/");
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.DiskOnFailureEnabled.Should().BeFalse();
        viewModel.Options.Otlp.Endpoint.Should().Be(new Uri("http://collector.local:4318/"));
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Buffer.DiskOnFailureEnabled.Should().BeFalse();
        viewModel.SpoolPath.Should().Be("D:\\NinaOtel\\spool");
        viewModel.MaxSpoolSizeGb.Should().Be("2.5");
        viewModel.MaxSpoolAgeDays.Should().Be("14");
        viewModel.Options.Buffer.SpoolPath.Should().Be("D:\\NinaOtel\\spool");
        viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(2684354560);
        viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(14));
        viewModel.StaticHeaders.Should().Be("Authorization: Bearer abc\r\nx-scope = nina");
        viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer abc");
        viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
        viewModel.CaCertificatePemPath.Should().Be("C:\\certs\\ca.pem");
        viewModel.ClientCertificatePemPath.Should().Be("C:\\certs\\client.pem");
        viewModel.ClientPrivateKeyPemPath.Should().Be("C:\\certs\\client-key.pem");
        viewModel.Options.Otlp.Auth.CaCertificatePemPath.Should().Be("C:\\certs\\ca.pem");
        viewModel.Options.Otlp.Auth.ClientCertificatePemPath.Should().Be("C:\\certs\\client.pem");
        viewModel.Options.Otlp.Auth.ClientPrivateKeyPemPath.Should().Be("C:\\certs\\client-key.pem");
    }

    [Fact]
    public void CollectorEndpoint_SavesValidAbsoluteUri()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorEndpoint = "http://collector.local:4318";

        settings.GetString("CollectorEndpoint", string.Empty).Should().Be("http://collector.local:4318/");
        viewModel.CollectorEndpoint.Should().Be("http://collector.local:4318/");
        viewModel.Options.Otlp.Endpoint.Should().Be(new Uri("http://collector.local:4318/"));
        viewModel.Status.Should().Be("Settings saved");
    }

    [Fact]
    public void CollectorEndpoint_RejectsInvalidUriWithoutSaving()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        viewModel.CollectorEndpoint = "http://collector.local:4318/";

        viewModel.CollectorEndpoint = "not a uri";

        settings.GetString("CollectorEndpoint", string.Empty).Should().Be("http://collector.local:4318/");
        viewModel.CollectorEndpoint.Should().Be("not a uri");
        viewModel.Options.Otlp.Endpoint.Should().Be(new Uri("http://collector.local:4318/"));
        viewModel.Status.Should().Be("Collector endpoint must be an absolute URI.");
    }

    [Fact]
    public void CollectorProtocol_AndDiskSpool_SaveImmediately()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorProtocol = OtlpProtocol.HttpProtobuf;
        viewModel.DiskOnFailureEnabled = false;

        settings.GetString("CollectorProtocol", string.Empty).Should().Be("HttpProtobuf");
        settings.GetBoolean("DiskOnFailureEnabled", true).Should().BeFalse();
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Buffer.DiskOnFailureEnabled.Should().BeFalse();
    }

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

    [Fact]
    public void SpoolNumericSettings_RejectValuesOutsideConvertedOptionRanges()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        viewModel.MaxSpoolSizeGb = "2";
        viewModel.MaxSpoolAgeDays = "8";

        viewModel.MaxSpoolSizeGb = "0.0000000005";

        settings.GetString("MaxSpoolSizeGb", string.Empty).Should().Be("2");
        viewModel.MaxSpoolSizeGb.Should().Be("0.0000000005");
        viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(2L * 1024 * 1024 * 1024);
        viewModel.Status.Should().Be("Max spool size must be at least 1 byte.");

        viewModel.MaxSpoolSizeGb = "9999999999";

        settings.GetString("MaxSpoolSizeGb", string.Empty).Should().Be("2");
        viewModel.MaxSpoolSizeGb.Should().Be("9999999999");
        viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(2L * 1024 * 1024 * 1024);
        viewModel.Status.Should().Be("Max spool size is too large.");

        viewModel.MaxSpoolAgeDays = "0.0000000000006";

        settings.GetString("MaxSpoolAgeDays", string.Empty).Should().Be("8");
        viewModel.MaxSpoolAgeDays.Should().Be("0.0000000000006");
        viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(8));
        viewModel.Status.Should().Be("Max spool age must be at least 1 tick.");

        viewModel.MaxSpoolAgeDays = "20000000";

        settings.GetString("MaxSpoolAgeDays", string.Empty).Should().Be("8");
        viewModel.MaxSpoolAgeDays.Should().Be("20000000");
        viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(8));
        viewModel.Status.Should().Be("Max spool age is too large.");
    }

    [Fact]
    public void StaticHeaders_SaveValidValuesAndUseLastDuplicateCaseInsensitiveHeader()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.StaticHeaders =
            "Authorization: Bearer first\r\nauthorization = Bearer second\r\nx-scope: nina\r\nx-url = http://collector:4318";

        settings.GetString("StaticHeaders", string.Empty).Should().Be(
            "Authorization: Bearer first\r\nauthorization = Bearer second\r\nx-scope: nina\r\nx-url = http://collector:4318");
        viewModel.Options.Otlp.Headers.Should().HaveCount(3);
        viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer second");
        viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
        viewModel.Options.Otlp.Headers.Should().Contain("x-url", "http://collector:4318");
        viewModel.Status.Should().Be("Settings saved");
    }

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
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        settings.GetString("CollectorProtocol", string.Empty).Should().Be("HttpProtobuf");
        viewModel.Status.Should().Be("PEM TLS uses HTTP/protobuf; settings saved.");
    }

    [Fact]
    public void TlsCertificatePaths_WhenPersistedProtocolIsGrpc_ForceHttpProtobufOnLoad()
    {
        var settings = new InMemoryPluginSettingsStore();
        settings.SetString("CollectorProtocol", OtlpProtocol.Grpc.ToString());
        settings.SetString("CaCertificatePemPath", "C:\\certs\\ca.pem");

        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        settings.GetString("CollectorProtocol", string.Empty).Should().Be("HttpProtobuf");
        viewModel.Status.Should().Be("PEM TLS uses HTTP/protobuf; protocol changed.");
    }

    [Fact]
    public void CollectorProtocol_WhenTlsPathConfigured_RejectsGrpc()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        viewModel.CaCertificatePemPath = "C:\\certs\\ca.pem";

        viewModel.CollectorProtocol = OtlpProtocol.Grpc;

        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        settings.GetString("CollectorProtocol", string.Empty).Should().Be("HttpProtobuf");
        viewModel.Status.Should().Be("PEM TLS requires HTTP/protobuf; clear certificate paths before using gRPC.");
    }

    [Fact]
    public void TlsCertificatePaths_RaiseOptionsChangedOnlyAfterProtocolIsHttpProtobuf()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        var protocolSnapshots = new List<OtlpProtocol>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NinaOtelOptionsViewModel.Options))
            {
                protocolSnapshots.Add(viewModel.Options.Otlp.Protocol);
            }
        };

        viewModel.CaCertificatePemPath = "C:\\certs\\ca.pem";

        protocolSnapshots.Should().NotBeEmpty();
        protocolSnapshots.Should().OnlyContain(protocol => protocol == OtlpProtocol.HttpProtobuf);
    }

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
    public void ClearingBearerToken_WithStaticAuthorization_SuppressesAuthorizationHeader()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
        viewModel.StaticHeaders = "Authorization: Bearer static\r\nx-scope: nina";
        viewModel.AuthenticationMode = OtlpAuthenticationMode.BearerToken;
        viewModel.SetBearerToken("secret-token");

        viewModel.SetBearerToken(string.Empty);

        viewModel.Options.Otlp.Headers.Should().NotContainKey("Authorization");
        viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
    }

    [Theory]
    [InlineData("jasper", "")]
    [InlineData("", "plain-password")]
    public void BasicAuth_WithIncompleteCredentials_SuppressesStaticAuthorizationHeader(
        string username,
        string password)
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
        viewModel.StaticHeaders = "Authorization: Bearer static\r\nx-scope: nina";

        viewModel.AuthenticationMode = OtlpAuthenticationMode.Basic;
        viewModel.BasicUsername = username;
        viewModel.SetBasicPassword(password);

        viewModel.Options.Otlp.Headers.Should().NotContainKey("Authorization");
        viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
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

    [Fact]
    public void Constructor_WhenProtectedBearerCannotDecrypt_SuppressesStaticAuthorizationHeader()
    {
        var settings = new InMemoryPluginSettingsStore();
        settings.SetString("AuthenticationMode", OtlpAuthenticationMode.BearerToken.ToString());
        settings.SetString("StaticHeaders", "Authorization: Bearer static\r\nx-scope: nina");
        settings.SetString("BearerTokenProtected", "unreadable-ciphertext");

        var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());

        viewModel.Options.Otlp.Headers.Should().NotContainKey("Authorization");
        viewModel.Options.Otlp.Headers.Should().Contain("x-scope", "nina");
        viewModel.Status.Should().Be("Bearer token could not be decrypted; re-enter it.");
    }

    [Fact]
    public void Reload_WhenProtectedBearerCannotDecrypt_PreservesDecryptWarningStatus()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
        settings.SetString("AuthenticationMode", OtlpAuthenticationMode.BearerToken.ToString());
        settings.SetString("BearerTokenProtected", "unreadable-ciphertext");

        viewModel.Reload();

        viewModel.Status.Should().Be("Bearer token could not be decrypted; re-enter it.");
    }

    [Fact]
    public void Reload_IncrementsSecretRevisionAndRaisesChangeNotification()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings, new FakeSecretProtector());
        var initialRevision = viewModel.SecretRevision;
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        viewModel.Reload();

        viewModel.SecretRevision.Should().Be(initialRevision + 1);
        changedProperties.Should().Contain(nameof(NinaOtelOptionsViewModel.SecretRevision));
    }

    [Fact]
    public void Reload_LoadsSettingsForCurrentProfile()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        settings.SetString("CollectorEndpoint", "http://profile-two:4317/");
        settings.SetString("CollectorProtocol", OtlpProtocol.HttpProtobuf.ToString());
        settings.SetBoolean("DiskOnFailureEnabled", false);
        settings.SetString("SpoolPath", "E:\\ProfileTwo\\spool");
        settings.SetString("MaxSpoolSizeGb", "4");
        settings.SetString("MaxSpoolAgeDays", "12");
        settings.SetString("StaticHeaders", "Authorization: Bearer profile-two");

        viewModel.Reload();

        viewModel.CollectorEndpoint.Should().Be("http://profile-two:4317/");
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.DiskOnFailureEnabled.Should().BeFalse();
        viewModel.SpoolPath.Should().Be("E:\\ProfileTwo\\spool");
        viewModel.MaxSpoolSizeGb.Should().Be("4");
        viewModel.MaxSpoolAgeDays.Should().Be("12");
        viewModel.Options.Buffer.SpoolPath.Should().Be("E:\\ProfileTwo\\spool");
        viewModel.Options.Buffer.MaxSpoolBytes.Should().Be(4L * 1024 * 1024 * 1024);
        viewModel.Options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(12));
        viewModel.StaticHeaders.Should().Be("Authorization: Bearer profile-two");
        viewModel.Options.Otlp.Headers.Should().Contain("Authorization", "Bearer profile-two");
    }

    [Fact]
    public void UpdateCollectorHealth_WhenExportSucceeds_ShowsHealthyStatus()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.UpdateCollectorHealth(CollectorHealthSnapshot.Healthy(
            new Uri("http://collector.local:4317/"),
            OtlpProtocol.Grpc,
            exportedRecords: 3,
            checkedAt: new DateTimeOffset(2026, 6, 17, 20, 0, 0, TimeSpan.Zero)));

        viewModel.CollectorHealthState.Should().Be(CollectorHealthState.Healthy);
        viewModel.CollectorHealthBrush.Should().Be("#2E7D32");
        viewModel.CollectorHealthSummary.Should().Be("Collector connected");
        viewModel.CollectorHealthDebugInfo.Should().Contain("http://collector.local:4317/");
        viewModel.CollectorHealthDebugInfo.Should().Contain("Grpc");
        viewModel.CollectorHealthDebugInfo.Should().Contain("3 record(s)");
    }

    [Fact]
    public void UpdateCollectorHealth_WhenExportFails_ShowsUnhealthyStatusAndDebugInfo()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.UpdateCollectorHealth(CollectorHealthSnapshot.Unhealthy(
            new Uri("http://collector.local:4317/"),
            OtlpProtocol.Grpc,
            "SocketException",
            "connection refused",
            checkedAt: new DateTimeOffset(2026, 6, 17, 20, 0, 0, TimeSpan.Zero)));

        viewModel.CollectorHealthState.Should().Be(CollectorHealthState.Unhealthy);
        viewModel.CollectorHealthBrush.Should().Be("#C62828");
        viewModel.CollectorHealthSummary.Should().Be("Collector export failed");
        viewModel.CollectorHealthDebugInfo.Should().Contain("SocketException");
        viewModel.CollectorHealthDebugInfo.Should().Contain("connection refused");
    }

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

    private sealed class InMemoryPluginSettingsStore : INinaOtelSettingsStore
    {
        private readonly Dictionary<string, object> values = new();

        public string GetString(string name, string defaultValue) =>
            values.TryGetValue(name, out var value) && value is string text ? text : defaultValue;

        public void SetString(string name, string value) => values[name] = value;

        public bool GetBoolean(string name, bool defaultValue) =>
            values.TryGetValue(name, out var value) && value is bool boolean ? boolean : defaultValue;

        public void SetBoolean(string name, bool value) => values[name] = value;
    }
}
