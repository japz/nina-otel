using FluentAssertions;
using Xunit;

namespace NinaOtel.Core.Tests.Plugin;

public sealed class SwitchTelemetryCollectorSourceTests
{
    [Fact]
    public void PublishedSwitchMetric_EmitsUpstreamNameTagAliasForReadOnlySwitchChannel()
    {
        var source = File.ReadAllText(FindCollectorPath());
        var createPublishedMetricSource = ExtractMethodSource(
            source,
            "private static PublishedSwitchMetric CreatePublishedMetric(string switchName, ISwitch readOnlySwitch)");

        createPublishedMetricSource.Should().Contain("""["name"] = NormalizeName(readOnlySwitch.Name)""");
        createPublishedMetricSource.Should().Contain("""["switch_channel_name"] = NormalizeName(readOnlySwitch.Name)""");
    }

    private static string FindCollectorPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "NinaOtel.Plugin",
                "Telemetry",
                "SwitchTelemetryCollector.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find SwitchTelemetryCollector.cs from test output directory.");
    }

    private static string ExtractMethodSource(string source, string methodDeclaration)
    {
        var start = source.IndexOf(methodDeclaration, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{methodDeclaration} should exist");

        var braceStart = source.IndexOf('{', start);
        braceStart.Should().BeGreaterThan(start, $"{methodDeclaration} should have a body");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Could not extract {methodDeclaration}.");
    }
}
