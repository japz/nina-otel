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
