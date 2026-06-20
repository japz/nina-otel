using FluentAssertions;
using NinaOtel.Core.Options;
using Xunit;

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

    [Fact]
    public void OtlpAuthOptions_ToString_RedactsSecretValues()
    {
        var auth = new OtlpAuthOptions
        {
            BearerToken = "bearer-token-secret",
            BasicPasswordProtected = "basic-password-secret",
            ClientCertificatePfxPasswordProtected = "pfx-password-secret",
        };

        var text = auth.ToString();

        text.Should().NotContain("bearer-token-secret");
        text.Should().NotContain("basic-password-secret");
        text.Should().NotContain("pfx-password-secret");
        text.Should().Contain("BearerTokenConfigured = True");
        text.Should().Contain("BasicPasswordConfigured = True");
        text.Should().Contain("ClientCertificatePfxPasswordConfigured = True");
    }

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

    [Fact]
    public void OtlpOptions_Headers_SnapshotsSourceDictionary()
    {
        var headers = new Dictionary<string, string>
        {
            ["authorization"] = "initial",
        };

        var options = new OtlpOptions
        {
            Headers = headers,
        };

        headers["authorization"] = "changed";
        headers["x-new-header"] = "new";

        options.Headers.Should().ContainSingle();
        options.Headers["authorization"].Should().Be("initial");
        options.Headers.Should().NotContainKey("x-new-header");
    }

    [Fact]
    public void AddonOptions_Settings_SnapshotsSourceDictionary()
    {
        var settings = new Dictionary<string, string>
        {
            ["batch.size"] = "100",
        };

        var options = new AddonOptions
        {
            Settings = settings,
        };

        settings["batch.size"] = "200";
        settings["enabled.mode"] = "verbose";

        options.Settings.Should().ContainSingle();
        options.Settings["batch.size"].Should().Be("100");
        options.Settings.Should().NotContainKey("enabled.mode");
    }

    [Fact]
    public void NinaOtelOptions_Addons_SnapshotsSourceDictionary()
    {
        var addons = new Dictionary<string, AddonOptions>
        {
            ["addon-a"] = new() { Enabled = true },
        };

        var options = new NinaOtelOptions
        {
            Addons = addons,
        };

        addons["addon-a"] = new AddonOptions { Enabled = false };
        addons["addon-b"] = new AddonOptions { Enabled = true };

        options.Addons.Should().ContainSingle();
        options.Addons["addon-a"].Enabled.Should().BeTrue();
        options.Addons.Should().NotContainKey("addon-b");
    }
}
