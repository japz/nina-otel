using FluentAssertions;
using Xunit;

namespace NinaOtel.Core.Tests.Plugin;

public sealed class ImageTelemetryCollectorSourceTests
{
    [Fact]
    public void PublishImageSaved_GatesImageMetricsToLightFramesWithoutSuppressingWorkflowTelemetry()
    {
        var source = File.ReadAllText(FindCollectorPath());
        var publishImageSavedSource = ExtractMethodSource(source, "private void PublishImageSaved(ImageSavedEventArgs args)");
        var lightFrameBlockSource = ExtractBlockSource(publishImageSavedSource, "if (IsLightFrame(args))");

        source.Should().Contain("private static bool IsLightFrame(ImageSavedEventArgs args)");
        source.Should().Contain("ImageType?.Trim()");
        source.Should().Contain("""StringComparison.OrdinalIgnoreCase""");
        publishImageSavedSource.Should().Contain("PublishExposureSpan(args, attributes);");
        publishImageSavedSource.Should().Contain("PublishImageSaveSpan(args, attributes);");
        publishImageSavedSource.Should().Contain("if (IsLightFrame(args))");
        publishImageSavedSource.Should().Contain("PublishImageMetrics(timestamp, args, attributes);");
        publishImageSavedSource.Should().Contain("PublishImageLog(timestamp, args, attributes);");
        lightFrameBlockSource.Should().Contain("PublishImageMetrics(timestamp, args, attributes);");
        lightFrameBlockSource.Should().NotContain("PublishImageLog(timestamp, args, attributes);");

        publishImageSavedSource.IndexOf("PublishExposureSpan(args, attributes);", StringComparison.Ordinal)
            .Should().BeLessThan(publishImageSavedSource.IndexOf("if (IsLightFrame(args))", StringComparison.Ordinal));
        publishImageSavedSource.IndexOf("PublishImageSaveSpan(args, attributes);", StringComparison.Ordinal)
            .Should().BeLessThan(publishImageSavedSource.IndexOf("if (IsLightFrame(args))", StringComparison.Ordinal));
        publishImageSavedSource.IndexOf("PublishImageLog(timestamp, args, attributes);", StringComparison.Ordinal)
            .Should().BeGreaterThan(publishImageSavedSource.IndexOf("if (IsLightFrame(args))", StringComparison.Ordinal));
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
                "ImageTelemetryCollector.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find ImageTelemetryCollector.cs from test output directory.");
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

    private static string ExtractBlockSource(string source, string blockHeader)
    {
        var start = source.IndexOf(blockHeader, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{blockHeader} should exist");

        var braceStart = source.IndexOf('{', start);
        braceStart.Should().BeGreaterThan(start, $"{blockHeader} should have a body");

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

        throw new InvalidOperationException($"Could not extract {blockHeader}.");
    }
}
