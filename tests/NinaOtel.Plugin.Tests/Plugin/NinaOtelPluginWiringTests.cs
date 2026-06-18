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
