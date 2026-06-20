using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class ProfileHostTelemetrySinkTests
{
    [Theory]
    [MemberData(nameof(SignalRecords))]
    public void TryPublish_AddsProfileAndHostAttributesToEverySignal(TelemetryRecord record)
    {
        var inner = new RecordingTelemetrySink();
        var sink = new ProfileHostTelemetrySink(
            inner,
            CreateProfileService("Deep Sky"),
            () => "observatory-pc");

        var result = sink.TryPublish(record);

        result.Should().BeTrue();
        var published = inner.Records.Should().ContainSingle().Which;
        published.Attributes.Should().Contain("profile_name", "Deep Sky");
        published.Attributes.Should().Contain("host_name", "observatory-pc");
    }

    [Fact]
    public void TryPublish_PreservesRecordFieldsAndExistingAttributes()
    {
        var timestamp = new DateTimeOffset(2026, 6, 18, 22, 15, 30, TimeSpan.Zero);
        var record = new TelemetryRecord(
            TelemetrySignal.Span,
            timestamp,
            "nina.capture",
            "nina.exposure",
            TelemetryPriority.Important,
            new Dictionary<string, object?>
            {
                ["existing"] = 42,
                ["profile_name"] = "explicit-profile",
            },
            NumericValue: 12.5,
            Body: "body",
            Severity: TelemetrySeverity.Warning,
            SpanKind: SpanEventKind.Stop,
            SpanId: "span-1",
            ParentSpanId: "parent-1")
        {
            TraceId = "trace-1",
        };
        var inner = new RecordingTelemetrySink();
        var sink = new ProfileHostTelemetrySink(
            inner,
            CreateProfileService("Profile From Service"),
            () => "host-from-provider");

        sink.TryPublish(record).Should().BeTrue();

        var published = inner.Records.Should().ContainSingle().Which;
        published.Should().NotBeSameAs(record);
        published.Signal.Should().Be(record.Signal);
        published.Timestamp.Should().Be(timestamp);
        published.Source.Should().Be(record.Source);
        published.Name.Should().Be(record.Name);
        published.Priority.Should().Be(record.Priority);
        published.NumericValue.Should().Be(12.5);
        published.Body.Should().Be("body");
        published.Severity.Should().Be(TelemetrySeverity.Warning);
        published.SpanKind.Should().Be(SpanEventKind.Stop);
        published.SpanId.Should().Be("span-1");
        published.ParentSpanId.Should().Be("parent-1");
        published.TraceId.Should().Be("trace-1");
        published.Attributes.Should().Contain("existing", 42);
        published.Attributes.Should().Contain("profile_name", "explicit-profile");
        published.Attributes.Should().Contain("host_name", "host-from-provider");
    }

    [Fact]
    public void TryPublish_WhenProfileLookupFails_ForwardsRecordWithHost()
    {
        var inner = new RecordingTelemetrySink();
        var profileProxy = CreateProfileServiceProxy(out var profileService);
        profileProxy.ThrowOnActiveProfile = true;
        var sink = new ProfileHostTelemetrySink(inner, profileService, () => "observatory-pc");
        var record = TelemetryRecord.Log(
            DateTimeOffset.UtcNow,
            "test",
            TelemetrySeverity.Information,
            "message",
            TelemetryPriority.Routine);

        var act = () => sink.TryPublish(record);

        act.Should().NotThrow();
        inner.Records.Should().ContainSingle().Which.Attributes.Should()
            .Contain("host_name", "observatory-pc")
            .And.NotContainKey("profile_name");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TryPublish_WhenProfileOrHostIsBlank_ForwardsOriginalRecord(string blank)
    {
        var record = TelemetryRecord.Metric(
            DateTimeOffset.UtcNow,
            "test",
            "metric",
            1,
            TelemetryPriority.Routine);
        var inner = new RecordingTelemetrySink();
        var sink = new ProfileHostTelemetrySink(inner, CreateProfileService(blank), () => blank);

        sink.TryPublish(record).Should().BeTrue();

        inner.Records.Should().ContainSingle().Which.Should().BeSameAs(record);
    }

    [Fact]
    public void TryPublish_WhenHostLookupFails_ForwardsRecordWithProfile()
    {
        var inner = new RecordingTelemetrySink();
        var sink = new ProfileHostTelemetrySink(
            inner,
            CreateProfileService("Deep Sky"),
            () => throw new InvalidOperationException("Host unavailable."));
        var record = TelemetryRecord.Metric(
            DateTimeOffset.UtcNow,
            "test",
            "metric",
            1,
            TelemetryPriority.Routine);

        var act = () => sink.TryPublish(record);

        act.Should().NotThrow();
        inner.Records.Should().ContainSingle().Which.Attributes.Should()
            .Contain("profile_name", "Deep Sky")
            .And.NotContainKey("host_name");
    }

    [Fact]
    public void TryPublish_WhenInnerSinkThrows_ReturnsFalseWithoutThrowing()
    {
        var sink = new ProfileHostTelemetrySink(
            new ThrowingTelemetrySink(),
            CreateProfileService("Deep Sky"),
            () => "observatory-pc");
        var record = TelemetryRecord.Health(
            DateTimeOffset.UtcNow,
            "test",
            "health",
            TelemetryPriority.Important);

        var act = () => sink.TryPublish(record);

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    public static TheoryData<TelemetryRecord> SignalRecords()
    {
        var timestamp = new DateTimeOffset(2026, 6, 18, 22, 15, 30, TimeSpan.Zero);
        return new TheoryData<TelemetryRecord>
        {
            TelemetryRecord.Metric(timestamp, "test", "metric", 1, TelemetryPriority.Normal),
            TelemetryRecord.Log(timestamp, "test", TelemetrySeverity.Information, "message", TelemetryPriority.Routine),
            TelemetryRecord.Span(timestamp, "test", "span", SpanEventKind.Start, "span-1", TelemetryPriority.Important),
            TelemetryRecord.Health(timestamp, "test", "health", TelemetryPriority.Critical),
        };
    }

    private static IProfileService CreateProfileService(string? activeProfileName)
    {
        var profile = DispatchProxy.Create<IProfile, ProfileProxy>();
        ((ProfileProxy)(object)profile).Name = activeProfileName;

        var profileServiceProxy = CreateProfileServiceProxy(out var profileService);
        profileServiceProxy.ActiveProfile = profile;
        return profileService;
    }

    private static ProfileServiceProxy CreateProfileServiceProxy(out IProfileService profileService)
    {
        profileService = DispatchProxy.Create<IProfileService, ProfileServiceProxy>();
        return (ProfileServiceProxy)(object)profileService;
    }

    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public bool TryPublish(TelemetryRecord record)
        {
            Records.Add(record);
            return true;
        }
    }

    private sealed class ThrowingTelemetrySink : ITelemetrySink
    {
        public bool TryPublish(TelemetryRecord record) =>
            throw new InvalidOperationException("Sink unavailable.");
    }

    public class ProfileServiceProxy : DispatchProxy
    {
        public IProfile? ActiveProfile { get; set; }

        public bool ThrowOnActiveProfile { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            if (targetMethod!.Name is "get_ActiveProfile")
            {
                if (ThrowOnActiveProfile)
                {
                    throw new InvalidOperationException("Profile unavailable.");
                }

                return ActiveProfile;
            }

            return DefaultReturnValue(this, targetMethod, args);
        }
    }

    public class ProfileProxy : DispatchProxy
    {
        public string? Name { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            return targetMethod!.Name switch
            {
                "get_Name" => Name,
                _ => DefaultReturnValue(this, targetMethod, args),
            };
        }
    }

    private static object? DefaultReturnValue(
        object instance,
        MethodInfo targetMethod,
        object?[]? args)
    {
        var methodName = targetMethod.Name;
        var returnType = targetMethod.ReturnType;

        if (methodName is nameof(Equals))
        {
            return args?.Length > 0 && ReferenceEquals(instance, args[0]);
        }

        if (methodName is nameof(GetHashCode))
        {
            return RuntimeHelpers.GetHashCode(instance);
        }

        if (methodName is nameof(ToString))
        {
            return instance.GetType().Name;
        }

        if (returnType == typeof(bool))
        {
            return false;
        }

        if (returnType == typeof(string))
        {
            return string.Empty;
        }

        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result]);
        }

        if (returnType == typeof(int))
        {
            return 0;
        }

        if (returnType == typeof(double))
        {
            return 0d;
        }

        if (returnType == typeof(Guid))
        {
            return Guid.Empty;
        }

        if (returnType == typeof(DateTime))
        {
            return DateTime.MinValue;
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
