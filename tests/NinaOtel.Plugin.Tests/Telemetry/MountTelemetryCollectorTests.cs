using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class MountTelemetryCollectorTests
{
    [Fact]
    public void Start_RegistersCollectorAsTelescopeConsumerOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        proxy.RegisterCalls.Should().Be(1);
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_DoesNotRetryOrRemoveOnDispose()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        proxy.RegisterCalls.Should().Be(1);
        sink.Records.Should().ContainSingle(record => record.Signal == TelemetrySignal.Health);
        proxy.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialMountMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "mount_altitude",
            "mount_azimuth");
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediatorOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.Consumers.Should().BeEmpty();
        proxy.RemoveCalls.Should().Be(1);
    }

    [Fact]
    public void Dispose_WhenMediatorRemovalFails_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.mount" &&
            record.Name == "mount_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Registration failed."));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesAltitudeAndAzimuthMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.mount",
                "mount_altitude",
                52.5,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["mount_name"] = "EQ6-R" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.mount",
                "mount_azimuth",
                184.25,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["mount_name"] = "EQ6-R" }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UpdateDeviceInfo_WhenMountNameIsBlankOrNull_UsesUnknownAttribute(string? mountName)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(mountName, 52.5, double.NaN));

        sink.Records.Should().ContainSingle().Which.Attributes["mount_name"].Should().Be("Unknown");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDisconnectedBeforeAnyConnectedSample_DoesNotPublishMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new TelescopeInfo
        {
            Connected = false,
            Name = "EQ6-R",
            Altitude = 52.5,
            Azimuth = 184.25,
        });

        sink.Records.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnavailableBeforeAnySampleCases))]
    public void UpdateDeviceInfo_WhenValueIsUnavailableBeforeAnySample_PublishesOnlyAvailableMetrics(
        TelescopeInfo deviceInfo,
        string[] expectedMetricNames)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(deviceInfo);

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(expectedMetricNames);
    }

    [Theory]
    [MemberData(nameof(UnavailableAfterPriorSampleCases))]
    public void UpdateDeviceInfo_WhenValueBecomesUnavailable_ClearsOnlyThatMetricAndPublishesAvailableMetric(
        TelescopeInfo deviceInfo,
        string clearedMetricName,
        string availableMetricName,
        double availableValue)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(deviceInfo);

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == clearedMetricName &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == availableMetricName &&
            record.NumericValue == availableValue &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenMountDisconnects_ClearsPreviousMetricsForPreviousMount()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new TelescopeInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "mount_altitude",
            "mount_azimuth");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenMountNameChanges_ClearsOldMetricsAndPublishesNewMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("AM5", 48.75, 201.5));

        sink.Records.Should().HaveCount(4);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["mount_name"], "EQ6-R"));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["mount_name"], "AM5"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new MountTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Collector_DoesNotSubscribeToTelescopeEventsOrCallControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        collector.Dispose();

        proxy.ForbiddenCalls.Should().BeEmpty();
    }

    public static TheoryData<TelescopeInfo, string[]> UnavailableBeforeAnySampleCases() =>
        new()
        {
            { ConnectedInfo("EQ6-R", double.NaN, 184.25), ["mount_azimuth"] },
            { ConnectedInfo("EQ6-R", double.PositiveInfinity, 184.25), ["mount_azimuth"] },
            { ConnectedInfo("EQ6-R", double.NegativeInfinity, 184.25), ["mount_azimuth"] },
            { ConnectedInfo("EQ6-R", 52.5, double.NaN), ["mount_altitude"] },
            { ConnectedInfo("EQ6-R", 52.5, double.PositiveInfinity), ["mount_altitude"] },
            { ConnectedInfo("EQ6-R", 52.5, double.NegativeInfinity), ["mount_altitude"] },
            { ConnectedInfo("EQ6-R", double.NaN, double.NaN), [] },
            { ConnectedInfo("EQ6-R", double.PositiveInfinity, double.NegativeInfinity), [] },
        };

    public static TheoryData<TelescopeInfo, string, string, double> UnavailableAfterPriorSampleCases() =>
        new()
        {
            { ConnectedInfo("EQ6-R", double.NaN, 185.5), "mount_altitude", "mount_azimuth", 185.5 },
            { ConnectedInfo("EQ6-R", double.PositiveInfinity, 185.5), "mount_altitude", "mount_azimuth", 185.5 },
            { ConnectedInfo("EQ6-R", double.NegativeInfinity, 185.5), "mount_altitude", "mount_azimuth", 185.5 },
            { ConnectedInfo("EQ6-R", 53.75, double.NaN), "mount_azimuth", "mount_altitude", 53.75 },
            { ConnectedInfo("EQ6-R", 53.75, double.PositiveInfinity), "mount_azimuth", "mount_altitude", 53.75 },
            { ConnectedInfo("EQ6-R", 53.75, double.NegativeInfinity), "mount_azimuth", "mount_altitude", 53.75 },
        };

    private static TelescopeInfo ConnectedInfo(string? mountName, double altitude, double azimuth) =>
        new()
        {
            Connected = true,
            Name = mountName!,
            Altitude = altitude,
            Azimuth = azimuth,
        };

    private static PassiveTelescopeMediatorProxy CreateMediator(out ITelescopeMediator mediator)
    {
        mediator = DispatchProxy.Create<ITelescopeMediator, PassiveTelescopeMediatorProxy>();
        return (PassiveTelescopeMediatorProxy)(object)mediator;
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

    public class PassiveTelescopeMediatorProxy : DispatchProxy
    {
        private static readonly HashSet<string> ForbiddenEvents =
        [
            "Connected",
            "Disconnected",
            "Parked",
            "Unparked",
            "Homed",
            "Slewed",
            "BeforeMeridianFlip",
            "AfterMeridianFlip",
        ];

        private static readonly HashSet<string> ForbiddenMethods =
        [
            "Connect",
            "Disconnect",
            "Rescan",
            "GetInfo",
            "Broadcast",
            "Action",
            "SendCommandString",
            "SendCommandBool",
            "SendCommandBlind",
            "GetDevice",
            "MoveAxis",
            "PulseGuide",
            "Sync",
            "SlewToCoordinatesAsync",
            "SlewToTopocentricCoordinates",
            "MeridianFlip",
            "SetTrackingEnabled",
            "SetTrackingMode",
            "SetCustomTrackingRate",
            "SendToSnapPort",
            "GetCurrentPosition",
            "ParkTelescope",
            "UnparkTelescope",
            "WaitForSlew",
            "FindHome",
            "StopSlew",
            "DestinationSideOfPier",
            "RaiseBeforeMeridianFlip",
            "RaiseAfterMeridianFlip",
        ];

        public List<ITelescopeConsumer> Consumers { get; } = [];

        public TelescopeInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; set; }

        public bool ThrowOnRemove { get; set; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public List<string> ForbiddenCalls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            var methodName = targetMethod!.Name;

            if (methodName is "Equals")
            {
                return ReferenceEquals(this, args?[0]);
            }

            if (methodName is "GetHashCode")
            {
                return RuntimeHelpers.GetHashCode(this);
            }

            if (methodName is "ToString")
            {
                return nameof(PassiveTelescopeMediatorProxy);
            }

            if (IsForbiddenEventAccessor(methodName) || ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Mount telemetry must not call {methodName}.");
            }

            return methodName switch
            {
                "RegisterConsumer" => RegisterConsumer(args),
                "RemoveConsumer" => RemoveConsumer(args),
                "RegisterHandler" => null,
                _ => DefaultReturnValue(targetMethod.ReturnType),
            };
        }

        private object? RegisterConsumer(object?[]? args)
        {
            RegisterCalls++;
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            var consumer = args?.Length > 0 ? args[0] as ITelescopeConsumer : null;
            consumer.Should().NotBeNull("the collector should register itself as an ITelescopeConsumer");
            Consumers.Add(consumer!);
            consumer!.UpdateDeviceInfo(CurrentInfo);
            return null;
        }

        private object? RemoveConsumer(object?[]? args)
        {
            RemoveCalls++;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("Removal failed.");
            }

            var consumer = args?.Length > 0 ? args[0] as ITelescopeConsumer : null;
            Consumers.Remove(consumer!);
            return null;
        }

        private static bool IsForbiddenEventAccessor(string methodName)
        {
            if (!methodName.StartsWith("add_", StringComparison.Ordinal) &&
                !methodName.StartsWith("remove_", StringComparison.Ordinal))
            {
                return false;
            }

            var eventName = methodName[(methodName.IndexOf('_') + 1)..];
            return ForbiddenEvents.Contains(eventName);
        }

        private static object? DefaultReturnValue(Type returnType)
        {
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

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
