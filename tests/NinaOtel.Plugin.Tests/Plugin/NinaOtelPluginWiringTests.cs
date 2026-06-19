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
        source.Should().Contain("new RotatorTelemetryCollector(rotatorMediator, pipeline, timeProvider)");
        source.Should().Contain("rotatorTelemetry.Start();");
        source.Should().Contain("rotatorTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesFilterWheelTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IFilterWheelMediator filterWheelMediator");
        source.Should().Contain("new FilterWheelTelemetryCollector(filterWheelMediator, pipeline, timeProvider)");
        source.Should().Contain("filterWheelTelemetry.Start();");
        source.Should().Contain("filterWheelTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesMountTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ITelescopeMediator telescopeMediator");
        source.Should().Contain("new MountTelemetryCollector(telescopeMediator, pipeline, timeProvider)");
        source.Should().Contain("mountTelemetry.Start();");
        source.Should().Contain("mountTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesWeatherTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IWeatherDataMediator weatherDataMediator");
        source.Should().Contain("new WeatherTelemetryCollector(weatherDataMediator, pipeline, timeProvider)");
        source.Should().Contain("weatherTelemetry.Start();");
        source.Should().Contain("weatherTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_UsesImportedProfileServiceToStartAndDisposeAstrometricTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("private readonly AstrometricTelemetryCollector astrometricTelemetry;");
        source.Should().Contain("new AstrometricTelemetryCollector(profileService, pipeline, timeProvider)");
        source.Should().Contain("astrometricTelemetry.Start();");
        source.Should().Contain("astrometricTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesSwitchTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ISwitchMediator switchMediator");
        source.Should().Contain("new SwitchTelemetryCollector(switchMediator, pipeline, timeProvider)");
        source.Should().Contain("switchTelemetry.Start();");
        source.Should().Contain("switchTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesGuiderTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IGuiderMediator guiderMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(guiderMediator);");
        source.Should().Contain("new GuiderTelemetryCollector(guiderMediator, pipeline, timeProvider)");
        source.Should().Contain("guiderTelemetry.Start();");
        source.Should().Contain("guiderTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesSafetyMonitorTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("ISafetyMonitorMediator safetyMonitorMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(safetyMonitorMediator);");
        source.Should().Contain("new SafetyMonitorTelemetryCollector(safetyMonitorMediator, pipeline, timeProvider)");
        source.Should().Contain("safetyMonitorTelemetry.Start();");
        source.Should().Contain("safetyMonitorTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesFlatDeviceTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IFlatDeviceMediator flatDeviceMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(flatDeviceMediator);");
        source.Should().Contain("new FlatDeviceTelemetryCollector(flatDeviceMediator, pipeline, timeProvider)");
        source.Should().Contain("flatDeviceTelemetry.Start();");
        source.Should().Contain("flatDeviceTelemetry.Dispose();");
    }

    [Fact]
    public void Plugin_ImportsStartsAndDisposesDomeTelemetryCollector()
    {
        var source = File.ReadAllText(FindPluginSourcePath());

        source.Should().Contain("IDomeMediator domeMediator");
        source.Should().Contain("ArgumentNullException.ThrowIfNull(domeMediator);");
        source.Should().Contain("new DomeTelemetryCollector(domeMediator, pipeline, timeProvider)");
        source.Should().Contain("domeTelemetry.Start();");
        source.Should().Contain("domeTelemetry.Dispose();");
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
