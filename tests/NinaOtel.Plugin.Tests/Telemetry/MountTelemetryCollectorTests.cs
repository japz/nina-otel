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
    public void Start_RegistersCollectorAsTelescopeConsumerAndSubscribesMountEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddParkedCalls.Should().Be(1);
        proxy.AddUnparkedCalls.Should().Be(1);
        proxy.AddHomedCalls.Should().Be(1);
        proxy.AddSlewedCalls.Should().Be(1);
        proxy.TotalSubscriberCount.Should().Be(6);
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_DoesNotRetryOrRemoveAgainOnDispose()
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
        proxy.RemoveCalls.Should().Be(1);
        proxy.Consumers.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenMediatorAddsConsumerThenRegistrationThrows_RollsBackConsumer()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.AttachConsumerBeforeRegisterThrow = true;
        var sink = new RecordingTelemetrySink();
        var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "mount_collector.registration_failed" &&
            Equals(record.Attributes["error_message"], "Registration failed after adding consumer."));
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
    public async Task Connected_PublishesMountConnectionLogWithCurrentConnectedMountName()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.mount" &&
            record.Name == "mount_connected" &&
            record.Body == "Mount connected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
    }

    [Fact]
    public async Task Connected_WhenNoKnownMount_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "mount_connected" &&
            Equals(record.Attributes["mount_name"], "Unknown"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.mount" &&
            record.Name == "mount_disconnected" &&
            record.Body == "Mount disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(2)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["mount_name"], "EQ6-R"));

        sink.Records.Clear();
        proxy.CurrentInfo = ConnectedInfo("AM5", 48.75, 201.5);
        await proxy.RaiseConnectedAsync();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "mount_disconnected" &&
            Equals(record.Attributes["mount_name"], "AM5"));
    }

    [Fact]
    public async Task Disconnected_WhenDeviceInfoAlreadyClearedMetrics_StillUsesPreviousMountName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("EQ6-R", 52.5, 184.25));
        collector.UpdateDeviceInfo(new TelescopeInfo
        {
            Connected = false,
            Name = "EQ6-R",
        });
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "mount_disconnected" &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownMount_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "mount_disconnected" &&
            record.Body == "Mount disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["mount_name"], "Unknown"));
    }

    [Theory]
    [InlineData("parked", "mount_parked", "Mount has parked")]
    [InlineData("unparked", "mount_unparked", "Mount has unparked")]
    [InlineData("homed", "mount_homed", "Mount has homed")]
    public async Task MountStateEvent_PublishesLifecycleLog(
        string eventName,
        string expectedRecordName,
        string expectedBody)
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseLifecycleEventAsync(eventName);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.mount" &&
            record.Name == expectedRecordName &&
            record.Body == expectedBody &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
    }

    [Fact]
    public async Task Slewed_PublishesMountSlewedLog()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseSlewedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.mount" &&
            record.Name == "mount_slewed" &&
            record.Body == "Mount slewed" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["mount_name"], "EQ6-R"));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthAndRollsBack()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddSlewed = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveParkedCalls.Should().Be(1);
        proxy.RemoveUnparkedCalls.Should().Be(1);
        proxy.RemoveHomedCalls.Should().Be(1);
        proxy.TotalSubscriberCount.Should().Be(0);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.mount" &&
            record.Name == "mount_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Slewed subscription failed."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFailsAfterInitialMetrics_ClearsThoseMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddSlewed = true;
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().Contain(record =>
                record.Name == "mount_altitude" &&
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["mount_name"], "EQ6-R"));
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionFails_LateMediatorEventsDoNotPublishLifecycleLogs()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddSlewed = true;
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        using var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseLifecycleEventAsync("parked");
        await proxy.RaiseSlewedAsync();

        sink.Records.Should().BeEmpty();
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
    public async Task Callbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        using var collector = new MountTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var connectedAct = async () => await proxy.RaiseConnectedAsync();
        var disconnectedAct = async () => await proxy.RaiseDisconnectedAsync();
        var parkedAct = async () => await proxy.RaiseLifecycleEventAsync("parked");
        var unparkedAct = async () => await proxy.RaiseLifecycleEventAsync("unparked");
        var homedAct = async () => await proxy.RaiseLifecycleEventAsync("homed");
        var slewedAct = async () => await proxy.RaiseSlewedAsync();

        await connectedAct.Should().NotThrowAsync();
        await disconnectedAct.Should().NotThrowAsync();
        await parkedAct.Should().NotThrowAsync();
        await unparkedAct.Should().NotThrowAsync();
        await homedAct.Should().NotThrowAsync();
        await slewedAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_WhenEventUnsubscriptionFails_DoesNotThrowAndLateEventsDoNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemoveConnected = true;
        proxy.ThrowOnRemoveDisconnected = true;
        proxy.ThrowOnRemoveParked = true;
        proxy.ThrowOnRemoveUnparked = true;
        proxy.ThrowOnRemoveHomed = true;
        proxy.ThrowOnRemoveSlewed = true;
        proxy.CurrentInfo = ConnectedInfo("EQ6-R", 52.5, 184.25);
        var sink = new RecordingTelemetrySink();
        var collector = new MountTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveParkedCalls.Should().Be(1);
        proxy.RemoveUnparkedCalls.Should().Be(1);
        proxy.RemoveHomedCalls.Should().Be(1);
        proxy.RemoveSlewedCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseLifecycleEventAsync("parked");
        await proxy.RaiseSlewedAsync();

        sink.Records.Should().BeEmpty();
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
    public void Collector_DoesNotCallMountControlApis()
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
        private static readonly HashSet<string> ForbiddenMethods =
        [
            "Connect",
            "Disconnect",
            "Rescan",
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

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private Func<object, EventArgs, Task>? parked;
        private Func<object, EventArgs, Task>? unparked;
        private Func<object, EventArgs, Task>? homed;
        private Func<object, MountSlewedEventArgs, Task>? slewed;

        public TelescopeInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; set; }

        public bool AttachConsumerBeforeRegisterThrow { get; set; }

        public bool ThrowOnRemove { get; set; }

        public bool ThrowOnAddSlewed { get; set; }

        public bool ThrowOnRemoveConnected { get; set; }

        public bool ThrowOnRemoveDisconnected { get; set; }

        public bool ThrowOnRemoveParked { get; set; }

        public bool ThrowOnRemoveUnparked { get; set; }

        public bool ThrowOnRemoveHomed { get; set; }

        public bool ThrowOnRemoveSlewed { get; set; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int AddParkedCalls { get; private set; }

        public int AddUnparkedCalls { get; private set; }

        public int AddHomedCalls { get; private set; }

        public int AddSlewedCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int RemoveParkedCalls { get; private set; }

        public int RemoveUnparkedCalls { get; private set; }

        public int RemoveHomedCalls { get; private set; }

        public int RemoveSlewedCalls { get; private set; }

        public int TotalSubscriberCount =>
            (connected?.GetInvocationList().Length ?? 0) +
            (disconnected?.GetInvocationList().Length ?? 0) +
            (parked?.GetInvocationList().Length ?? 0) +
            (unparked?.GetInvocationList().Length ?? 0) +
            (homed?.GetInvocationList().Length ?? 0) +
            (slewed?.GetInvocationList().Length ?? 0);

        public List<string> ForbiddenCalls { get; } = [];

        public Task RaiseConnectedAsync() =>
            connected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync() =>
            disconnected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseLifecycleEventAsync(string eventName) =>
            eventName switch
            {
                "parked" => parked?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                "unparked" => unparked?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                "homed" => homed?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null),
            };

        public Task RaiseSlewedAsync(MountSlewedEventArgs? args = null) =>
            slewed?.Invoke(this, args!) ?? Task.CompletedTask;

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

            if (ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Mount telemetry must not call {methodName}.");
            }

            return methodName switch
            {
                "add_Connected" => AddConnected(args),
                "remove_Connected" => RemoveConnected(args),
                "add_Disconnected" => AddDisconnected(args),
                "remove_Disconnected" => RemoveDisconnected(args),
                "add_Parked" => AddParked(args),
                "remove_Parked" => RemoveParked(args),
                "add_Unparked" => AddUnparked(args),
                "remove_Unparked" => RemoveUnparked(args),
                "add_Homed" => AddHomed(args),
                "remove_Homed" => RemoveHomed(args),
                "add_Slewed" => AddSlewed(args),
                "remove_Slewed" => RemoveSlewed(args),
                "GetInfo" => CurrentInfo,
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
            if (AttachConsumerBeforeRegisterThrow)
            {
                Consumers.Add(consumer!);
                throw new InvalidOperationException("Registration failed after adding consumer.");
            }

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

        private object? AddConnected(object?[]? args)
        {
            AddConnectedCalls++;
            connected += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveConnected(object?[]? args)
        {
            RemoveConnectedCalls++;
            if (ThrowOnRemoveConnected)
            {
                throw new InvalidOperationException("Connected unsubscription failed.");
            }

            connected -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddDisconnected(object?[]? args)
        {
            AddDisconnectedCalls++;
            disconnected += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveDisconnected(object?[]? args)
        {
            RemoveDisconnectedCalls++;
            if (ThrowOnRemoveDisconnected)
            {
                throw new InvalidOperationException("Disconnected unsubscription failed.");
            }

            disconnected -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddParked(object?[]? args)
        {
            AddParkedCalls++;
            parked += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveParked(object?[]? args)
        {
            RemoveParkedCalls++;
            if (ThrowOnRemoveParked)
            {
                throw new InvalidOperationException("Parked unsubscription failed.");
            }

            parked -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddUnparked(object?[]? args)
        {
            AddUnparkedCalls++;
            unparked += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveUnparked(object?[]? args)
        {
            RemoveUnparkedCalls++;
            if (ThrowOnRemoveUnparked)
            {
                throw new InvalidOperationException("Unparked unsubscription failed.");
            }

            unparked -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddHomed(object?[]? args)
        {
            AddHomedCalls++;
            homed += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveHomed(object?[]? args)
        {
            RemoveHomedCalls++;
            if (ThrowOnRemoveHomed)
            {
                throw new InvalidOperationException("Homed unsubscription failed.");
            }

            homed -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddSlewed(object?[]? args)
        {
            AddSlewedCalls++;
            if (ThrowOnAddSlewed)
            {
                throw new InvalidOperationException("Slewed subscription failed.");
            }

            slewed += args?.Length > 0 ? args[0] as Func<object, MountSlewedEventArgs, Task> : null;
            return null;
        }

        private object? RemoveSlewed(object?[]? args)
        {
            RemoveSlewedCalls++;
            if (ThrowOnRemoveSlewed)
            {
                throw new InvalidOperationException("Slewed unsubscription failed.");
            }

            slewed -= args?.Length > 0 ? args[0] as Func<object, MountSlewedEventArgs, Task> : null;
            return null;
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
