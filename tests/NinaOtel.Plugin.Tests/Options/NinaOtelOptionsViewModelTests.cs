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
        viewModel.CollectorHealthState.Should().Be(CollectorHealthState.Unknown);
        viewModel.CollectorHealthBrush.Should().Be("#808080");
        viewModel.CollectorHealthSummary.Should().Be("Collector not checked yet");
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
