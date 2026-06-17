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

        var viewModel = new NinaOtelOptionsViewModel(settings);

        viewModel.CollectorEndpoint.Should().Be("http://collector.local:4318/");
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.DiskOnFailureEnabled.Should().BeFalse();
        viewModel.Options.Otlp.Endpoint.Should().Be(new Uri("http://collector.local:4318/"));
        viewModel.Options.Otlp.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.Options.Buffer.DiskOnFailureEnabled.Should().BeFalse();
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
    public void Reload_LoadsSettingsForCurrentProfile()
    {
        var settings = new InMemoryPluginSettingsStore();
        var viewModel = new NinaOtelOptionsViewModel(settings);
        settings.SetString("CollectorEndpoint", "http://profile-two:4317/");
        settings.SetString("CollectorProtocol", OtlpProtocol.HttpProtobuf.ToString());
        settings.SetBoolean("DiskOnFailureEnabled", false);

        viewModel.Reload();

        viewModel.CollectorEndpoint.Should().Be("http://profile-two:4317/");
        viewModel.CollectorProtocol.Should().Be(OtlpProtocol.HttpProtobuf);
        viewModel.DiskOnFailureEnabled.Should().BeFalse();
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
