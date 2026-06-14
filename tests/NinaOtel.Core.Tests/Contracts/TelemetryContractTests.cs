using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Contracts;

public sealed class TelemetryContractTests
{
    [Fact]
    public void LogRecord_CarriesSourcePriorityAndAttributes()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.Parse("2026-06-14T01:02:03Z"),
            "nina",
            TelemetrySeverity.Error,
            "camera failed",
            TelemetryPriority.Critical,
            new Dictionary<string, object?>
            {
                ["source.file"] = "CameraVM.cs",
                ["source.line"] = 149,
            });

        record.Signal.Should().Be(TelemetrySignal.Log);
        record.Source.Should().Be("nina");
        record.Priority.Should().Be(TelemetryPriority.Critical);
        record.Attributes["source.file"].Should().Be("CameraVM.cs");
        record.Attributes["source.line"].Should().Be(149);
    }
}
