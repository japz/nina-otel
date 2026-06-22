using FluentAssertions;
using Xunit;

namespace NinaOtel.Plugin.Tests.Plugin;

public sealed class NinaOtelPluginWiringTests
{
    [Fact]
    public void Plugin_ImportsStartsAndDisposesRotatorTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IRotatorMediator rotatorMediator");
        source.Should().Contain("new RotatorTelemetryCollector(rotatorMediator, telemetrySink, timeProvider)");
        source.Should().Contain("rotatorTelemetry.Start();");
        source.Should().Contain("rotatorTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesFilterWheelTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IFilterWheelMediator filterWheelMediator");
        source.Should().Contain("new FilterWheelTelemetryCollector(filterWheelMediator, telemetrySink, timeProvider)");
        source.Should().Contain("filterWheelTelemetry.Start();");
        source.Should().Contain("filterWheelTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesMountTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ITelescopeMediator telescopeMediator");
        source.Should().Contain("new MountTelemetryCollector(telescopeMediator, telemetrySink, timeProvider)");
        source.Should().Contain("mountTelemetry.Start();");
        source.Should().Contain("mountTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesWeatherTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IWeatherDataMediator weatherDataMediator");
        source.Should().Contain("new WeatherTelemetryCollector(weatherDataMediator, telemetrySink, timeProvider)");
        source.Should().Contain("weatherTelemetry.Start();");
        source.Should().Contain("weatherTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_UsesImportedProfileServiceToStartAndDisposeAstrometricTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("private readonly AstrometricTelemetryCollector astrometricTelemetry;");
        source.Should().Contain("new AstrometricTelemetryCollector(profileService, telemetrySink, timeProvider)");
        source.Should().Contain("astrometricTelemetry.Start();");
        source.Should().Contain("astrometricTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesSwitchTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ISwitchMediator switchMediator");
        source.Should().Contain("new SwitchTelemetryCollector(switchMediator, telemetrySink, timeProvider)");
        source.Should().Contain("switchTelemetry.Start();");
        source.Should().Contain("switchTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesGuiderTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IGuiderMediator guiderMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(guiderMediator);");
        source.Should().Contain("new GuiderTelemetryCollector(guiderMediator, telemetrySink, timeProvider)");
        source.Should().Contain("guiderTelemetry.Start();");
        source.Should().Contain("guiderTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesSafetyMonitorTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ISafetyMonitorMediator safetyMonitorMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(safetyMonitorMediator);");
        source.Should().Contain("new SafetyMonitorTelemetryCollector(safetyMonitorMediator, telemetrySink, timeProvider)");
        source.Should().Contain("safetyMonitorTelemetry.Start();");
        source.Should().Contain("safetyMonitorTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesFlatDeviceTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IFlatDeviceMediator flatDeviceMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(flatDeviceMediator);");
        source.Should().Contain("new FlatDeviceTelemetryCollector(flatDeviceMediator, telemetrySink, timeProvider)");
        source.Should().Contain("flatDeviceTelemetry.Start();");
        source.Should().Contain("flatDeviceTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesDomeTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IDomeMediator domeMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(domeMediator);");
        source.Should().Contain("new DomeTelemetryCollector(domeMediator, telemetrySink, timeProvider)");
        source.Should().Contain("domeTelemetry.Start();");
        source.Should().Contain("domeTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_UsesProfileHostTelemetrySinkForAllTelemetryPublishers()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("private readonly ITelemetrySink telemetrySink;");
        source.Should().Contain("telemetrySink = new ProfileHostTelemetrySink(pipeline, profileService);");
        source.Should().Contain("new CameraTelemetryCollector(cameraMediator, telemetrySink, timeProvider)");
        source.Should().Contain("new FocuserTelemetryCollector(focuserMediator, telemetrySink, timeProvider)");
        source.Should().Contain("new ImageTelemetryCollector(imageSaveMediator, telemetrySink, timeProvider)");
        source.Should().Contain("new CoreLifecycleTelemetryProducer(telemetrySink, timeProvider, options)");
        var addonHostStart = source.IndexOf("addonHost = new AddonHost(", StringComparison.Ordinal);
        addonHostStart.Should().BeGreaterThanOrEqualTo(0);
        source.Substring(addonHostStart, 120).Should().Contain("telemetrySink,");
    }

    [Fact]
    public void Plugin_StartsFirstPartyAddonCatalogWithOptionsAndHealthCallback()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("FirstPartyAddonCatalog.CreateAll()");
        source.Should().Contain("CreateAddonConfigurations(options.Addons)");
        source.Should().Contain("NinaOtelOptionsViewModel.UpdateAddonHealth");
        source.Should().NotContain("Array.Empty<ITelemetryAddon>()");
    }

    [Fact]
    public void Plugin_ConstructsStartsAndDisposesNinaLogTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("private readonly NinaLogTelemetryCollector ninaLogTelemetry;");
        source.Should().Contain("new NinaLogTelemetryCollector(options.CoreTelemetry, telemetrySink, timeProvider)");
        source.Should().Contain("ninaLogTelemetry.Start();");
        source.Should().Contain("ninaLogTelemetry.UpdateOptions(options.CoreTelemetry);");
        source.Should().Contain("ninaLogTelemetry.Dispose();");
    }

    private static string FindPluginSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NinaOtel.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run from inside the repository");
        return Path.Combine(
            directory!.FullName,
            "src",
            "NinaOtel.Plugin",
            "NinaOtelPlugin.cs");
    }
}
