using FluentAssertions;
using Xunit;

namespace NinaOtel.Core.Tests.Plugin;

public sealed class NinaLogTelemetryCollectorSourceTests
{
    [Fact]
    public void RawForwardingAttributes_IncludeRawLineOnlyForRawLogRecords()
    {
        var source = File.ReadAllText(FindCollectorPath());
        var createAttributesSource = ExtractCreateAttributesSource(source);

        source.Should().Contain("CreateRawAttributes(NinaLogEvent logEvent)");
        source.Should().Contain("[\"raw.line\"] = logEvent.RawLine");
        createAttributesSource.Should().NotContain("raw.line");
        source.Should().Contain(
            """
                    TryPublishSafely(new TelemetryRecord(
                        TelemetrySignal.Log,
                        logEvent.Timestamp,
                        SourceName,
                        RawLogName,
                        TelemetryPriority.Debug,
                        CreateRawAttributes(logEvent),
                        Body: logEvent.RawLine,
            """);
        source.Should().Contain(
            """
                    if (ShouldPublishFilteredLog(logEvent))
                    {
                        TryPublishSafely(new TelemetryRecord(
                            TelemetrySignal.Log,
                            logEvent.Timestamp,
                            SourceName,
                            FilteredLogName,
                            TelemetryPriority.Important,
                            CreateAttributes(logEvent),
            """);
        source.Should().Contain(
            """
                    TryPublishSafely(new TelemetryRecord(
                        TelemetrySignal.Log,
                        logEvent.Timestamp,
                        SourceName,
                        breadcrumbName,
                        TelemetryPriority.Normal,
                        CreateAttributes(logEvent),
            """);
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
                "NinaLogTelemetryCollector.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find NinaLogTelemetryCollector.cs from test output directory.");
    }

    private static string ExtractCreateAttributesSource(string source)
    {
        const string createAttributesDeclaration =
            "private static IReadOnlyDictionary<string, object?> CreateAttributes(NinaLogEvent logEvent)";
        const string createRawAttributesDeclaration =
            "private static IReadOnlyDictionary<string, object?> CreateRawAttributes(NinaLogEvent logEvent)";

        var start = source.IndexOf(createAttributesDeclaration, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "CreateAttributes should exist");
        var end = source.IndexOf(createRawAttributesDeclaration, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "CreateRawAttributes should follow CreateAttributes");

        return source[start..end];
    }
}
