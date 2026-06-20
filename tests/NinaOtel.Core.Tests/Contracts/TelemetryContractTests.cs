using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.NightSummary;
using NinaOtel.Addons.OnStepX;
using NinaOtel.Addons.PHD2;
using NinaOtel.Addons.TargetScheduler;
using Xunit;

namespace NinaOtel.Core.Tests.Contracts;

public sealed class TelemetryContractTests
{
    [Fact]
    public void TelemetryRecord_PositionalConstructor_DoesNotIncludeTraceId()
    {
        var constructor = typeof(TelemetryRecord)
            .GetConstructors()
            .Single(static candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length > 0 && parameters[0].ParameterType == typeof(TelemetrySignal);
            });

        constructor.GetParameters()
            .Select(static parameter => parameter.Name)
            .Should()
            .NotContain("TraceId");
    }

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

    [Fact]
    public void LogRecord_SnapshotsAttributesFromSourceDictionary()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["source.file"] = "CameraVM.cs",
        };

        var record = TelemetryRecord.Log(
            DateTimeOffset.Parse("2026-06-14T01:02:03Z"),
            "nina",
            TelemetrySeverity.Error,
            "camera failed",
            TelemetryPriority.Critical,
            attributes);

        attributes["source.file"] = "Mutated.cs";
        attributes["source.line"] = 149;

        record.Attributes["source.file"].Should().Be("CameraVM.cs");
        record.Attributes.Should().NotContainKey("source.line");
    }

    [Fact]
    public void LogRecord_WithNoAttributes_ExposesReadOnlyEmptyAttributes()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.Parse("2026-06-14T01:02:03Z"),
            "nina",
            TelemetrySeverity.Error,
            "camera failed",
            TelemetryPriority.Critical);

        if (record.Attributes is IDictionary<string, object?> mutableAttributes)
        {
            var mutate = () => mutableAttributes["source.file"] = "CameraVM.cs";

            mutate.Should().Throw<NotSupportedException>();
        }

        record.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void SpanRecord_CanCarryTraceIdWithoutChangingFactorySignature()
    {
        var record = TelemetryRecord.Span(
            DateTimeOffset.Parse("2026-06-14T01:02:03Z"),
            "nina.sequence",
            "nina.exposure",
            SpanEventKind.Start,
            "span-1",
            TelemetryPriority.Normal) with
            {
                TraceId = "00112233445566778899aabbccddeeff",
            };

        record.TraceId.Should().Be("00112233445566778899aabbccddeeff");
    }

    [Fact]
    public void AddonValidationFailure_SnapshotsErrorsFromSourceArray()
    {
        var errors = new[] { "Missing id" };

        var result = AddonValidationResult.Failure(errors);

        errors[0] = "Mutated";

        result.Errors.Should().ContainSingle()
            .Which.Should().Be("Missing id");
    }

    [Fact]
    public void AddonConfiguration_SnapshotsSettingsAndMetadataDefaultsSupportedConfigVersion()
    {
        var settings = new Dictionary<string, string>
        {
            ["endpoint"] = "tcp://camera",
        };

        var configuration = new AddonConfiguration(settings: settings);
        var metadata = new AddonMetadata("camera", "Camera", new Version(1, 0, 0), "test");

        settings["endpoint"] = "mutated";
        settings["new"] = "ignored";

        configuration.ConfigVersion.Should().Be(1);
        configuration.Settings["endpoint"].Should().Be("tcp://camera");
        configuration.Settings.Should().NotContainKey("new");
        metadata.SupportedConfigVersion.Should().Be(1);

        if (configuration.Settings is IDictionary<string, string> mutableSettings)
        {
            var mutate = () => mutableSettings["endpoint"] = "mutated again";

            mutate.Should().Throw<NotSupportedException>();
        }
    }

    [Fact]
    public void FirstPartyAddons_ExposeStableMetadata()
    {
        ITelemetryAddon[] addons =
        [
            new Phd2TelemetryAddon(),
            new TargetSchedulerTelemetryAddon(),
            new NightSummaryTelemetryAddon(),
            new OnStepXTelemetryAddon(),
        ];

        addons.Select(addon => addon.Metadata.Id)
            .Should()
            .Equal("phd2", "target-scheduler", "night-summary", "onstepx");

        foreach (var addon in addons)
        {
            addon.Validate(AddonConfiguration.Default).IsValid.Should().BeTrue();
        }
    }
}
