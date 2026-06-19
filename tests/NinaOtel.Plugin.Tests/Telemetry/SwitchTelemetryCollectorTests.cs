using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class SwitchTelemetryCollectorTests
{
    [Theory]
    [InlineData("mediator")]
    [InlineData("sink")]
    [InlineData("timeProvider")]
    public void Constructor_WhenDependencyIsNull_ThrowsArgumentNullException(string nullDependency)
    {
        var proxy = CreateMediator(out var mediator);
        _ = proxy;
        var sink = new RecordingTelemetrySink();
        var timeProvider = TimeProvider.System;

        var act = () => new SwitchTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_RegistersCollectorAsSwitchConsumerAndSubscribesSwitchEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(1);
        proxy.DisconnectedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialSwitchMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5));
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Source == "nina.switch" &&
            record.Name == "switch_ro_sw1" &&
            record.NumericValue == 42.5 &&
            Equals(record.Attributes["switch_name"], "PowerBox") &&
            Equals(record.Attributes["switch_id"], (short)1) &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.switch" &&
            record.Name == "switch_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Registration failed."));
        proxy.AddConnectedCalls.Should().Be(0);
        proxy.AddDisconnectedCalls.Should().Be(0);
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_DoesNotRetryOrRemoveOnDispose()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public void Start_WhenRegistrationFailsAndSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        using var collector = new SwitchTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Connected_PublishesSwitchConnectionLogWithCurrentConnectedSwitchName()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PowerBox");
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.switch" &&
            record.Name == "switch_connected" &&
            record.Body == "Switch connected" &&
            record.Severity == TelemetrySeverity.Information &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
    }

    [Fact]
    public async Task Connected_PublishesEachConnectionEvent()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PowerBox");
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseConnectedAsync();

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_connected" &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoIsUnavailable_UsesLastKnownSwitchName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        sink.Records.Clear();
        proxy.CurrentInfo = new SwitchInfo
        {
            Connected = false,
            Name = "Ignored stale name",
        };

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_connected" &&
            record.Body == "Switch connected" &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
    }

    [Fact]
    public async Task Connected_WhenGetInfoThrows_UsesLastKnownSwitchName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        sink.Records.Clear();
        proxy.ThrowOnGetInfo = true;

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_connected" &&
            record.Body == "Switch connected" &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
    }

    [Fact]
    public async Task Connected_WhenCurrentSwitchNameChanges_ClearsPreviousMetricsBeforeConnectionLog()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        sink.Records.Clear();
        proxy.CurrentInfo = ConnectedInfo("PowerBox 2");

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "switch_ro_sw1" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_name"], "PowerBox") &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_connected" &&
            record.Body == "Switch connected" &&
            Equals(record.Attributes["switch_name"], "PowerBox 2"));
    }

    [Fact]
    public async Task Connected_WhenNoKnownSwitch_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_connected" &&
            record.Body == "Switch connected" &&
            Equals(record.Attributes["switch_name"], "Unknown"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", 42.5),
            ReadOnlySwitch(3, "Main power", 1.0)));
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.switch" &&
            record.Name == "switch_disconnected" &&
            record.Body == "Switch disconnected" &&
            record.Severity == TelemetrySeverity.Information &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(2)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["switch_name"], "PowerBox"));

        sink.Records.Clear();
        proxy.CurrentInfo = ConnectedInfo("PowerBox 2");
        await proxy.RaiseConnectedAsync();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_disconnected" &&
            Equals(record.Attributes["switch_name"], "PowerBox 2"));
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownSwitch_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "switch_disconnected" &&
            record.Body == "Switch disconnected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["switch_name"], "Unknown"));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthRollsBackAndClearsInitialMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.CurrentInfo = ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5));
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(0);
        proxy.DisconnectedSubscriberCount.Should().Be(0);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "switch_ro_sw1" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.switch" &&
            record.Name == "switch_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Disconnected subscription failed."));
    }

    [Fact]
    public async Task Start_WhenConnectedSubscriptionThrowsAfterAttaching_RemovesHandlerAndSuppressesLateCallbacks()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddConnected = true;
        proxy.AttachConnectedBeforeThrow = true;
        proxy.CurrentInfo = ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5));
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        proxy.ConnectedSubscriberCount.Should().Be(0);
        proxy.DisconnectedSubscriberCount.Should().Be(0);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionThrowsAfterAttaching_RemovesHandlerAndSuppressesLateCallbacks()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.AttachDisconnectedBeforeThrow = true;
        proxy.CurrentInfo = ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5));
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        proxy.DisconnectedSubscriberCount.Should().Be(0);
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenRollbackRemoveConsumerFails_LateCallbacksDoNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        proxy.Broadcast(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediatorOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.Consumers.Should().BeEmpty();
        proxy.RemoveCalls.Should().Be(1);
    }

    [Fact]
    public void Dispose_BeforeSuccessfulRegistration_DoesNotRemoveConsumer()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Dispose();
        collector.Dispose();

        proxy.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public void Dispose_WhenMediatorRemovalFails_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_WhenEventUnsubscriptionFails_DoesNotThrowAndLateEventsDoNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemoveConnected = true;
        proxy.ThrowOnRemoveDisconnected = true;
        proxy.CurrentInfo = ConnectedInfo("PowerBox");
        var sink = new RecordingTelemetrySink();
        var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesFiniteSwitchValuesWithOneBatchTimestamp()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());

        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", 42.5),
            ReadOnlySwitch(3, "Main power", 1.0)));

        sink.Records.Should().HaveCount(2);
        var batchTimestamp = sink.Records[0].Timestamp;
        sink.Records.Should().OnlyContain(record =>
            record.Timestamp == batchTimestamp &&
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.switch" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "switch_ro_sw1" &&
            record.NumericValue == 42.5 &&
            Equals(record.Attributes["switch_id"], (short)1) &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "switch_ro_sw3" &&
            record.NumericValue == 1.0 &&
            Equals(record.Attributes["switch_id"], (short)3) &&
            Equals(record.Attributes["switch_channel_name"], "Main power"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    public void UpdateDeviceInfo_WhenNamesAreBlankOrNull_UsesUnknownAttributes(string? deviceName, string? channelName)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(deviceName, ReadOnlySwitch(1, channelName, 42.5)));

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            Equals(record.Attributes["switch_name"], "Unknown") &&
            Equals(record.Attributes["switch_channel_name"], "Unknown"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnectedWithNullOrEmptySwitches_ClearsPreviousMetricsAndKeepsConnectedState()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new SwitchInfo
        {
            Connected = true,
            Name = "PowerBox",
            ReadonlySwitches = null!,
        });

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "switch_ro_sw1" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_name"], "PowerBox") &&
            Equals(record.Attributes["switch_id"], (short)1) &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));

        sink.Records.Clear();
        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(2, "Flat panel", 12.0)));

        sink.Records.Should().ContainSingle().Which.Name.Should().Be("switch_ro_sw2");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenValuesAreNonFinite_SkipsAndClearsPriorFinitePoints()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", 42.5),
            ReadOnlySwitch(2, "Flat panel", 12.0),
            ReadOnlySwitch(3, "Main power", 1.0)));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", double.NaN),
            ReadOnlySwitch(2, "Flat panel", double.PositiveInfinity),
            ReadOnlySwitch(3, "Main power", double.NegativeInfinity),
            ReadOnlySwitch(4, "Never published", double.NaN)));

        sink.Records.Should().HaveCount(3);
        sink.Records.Should().OnlyContain(record => double.IsNaN(record.NumericValue!.Value));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "switch_ro_sw1",
            "switch_ro_sw2",
            "switch_ro_sw3");

        sink.Records.Clear();
        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", double.NaN),
            ReadOnlySwitch(2, "Flat panel", double.PositiveInfinity)));

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSwitchDisconnects_ClearsPreviousMetricsAndResetsState()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", 42.5),
            ReadOnlySwitch(3, "Main power", 1.0)));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new SwitchInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_name"], "PowerBox"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "switch_ro_sw1",
            "switch_ro_sw3");

        sink.Records.Clear();
        collector.UpdateDeviceInfo(new SwitchInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceNameChanges_ClearsOldPointsBeforePublishingNewPoints()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox 2", ReadOnlySwitch(1, "Dew heater", 43.0)));

        sink.Records.Should().HaveCount(2);
        sink.Records[0].Should().Match<TelemetryRecord>(record =>
            record.Name == "switch_ro_sw1" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_name"], "PowerBox") &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));
        sink.Records[1].Should().Match<TelemetryRecord>(record =>
            record.Name == "switch_ro_sw1" &&
            record.NumericValue == 43.0 &&
            Equals(record.Attributes["switch_name"], "PowerBox 2") &&
            Equals(record.Attributes["switch_channel_name"], "Dew heater"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSwitchDisappears_ClearsOldPoint()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(
            "PowerBox",
            ReadOnlySwitch(1, "Dew heater", 42.5),
            ReadOnlySwitch(2, "Flat panel", 12.0)));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 43.0)));

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "switch_ro_sw1" &&
            record.NumericValue == 43.0);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "switch_ro_sw2" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["switch_channel_name"], "Flat panel"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new SwitchTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task LifecycleEvents_WhenSinkThrows_DoNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PowerBox");
        using var collector = new SwitchTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var connectedAct = async () => await proxy.RaiseConnectedAsync();
        var disconnectedAct = async () => await proxy.RaiseDisconnectedAsync();

        await connectedAct.Should().NotThrowAsync();
        await disconnectedAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Collector_DoesNotCallSwitchControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SwitchTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PowerBox", ReadOnlySwitch(1, "Dew heater", 42.5)));
        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();
        collector.Dispose();

        proxy.ForbiddenCalls.Should().BeEmpty();
    }

    private static SwitchInfo ConnectedInfo(string? switchName, params ISwitch[] readOnlySwitches) =>
        new()
        {
            Connected = true,
            Name = switchName!,
            ReadonlySwitches = new ReadOnlyCollection<ISwitch>(readOnlySwitches),
        };

    private static ISwitch ReadOnlySwitch(short id, string? name, double value) =>
        new FakeSwitch(id, name!, value);

    private static PassiveSwitchMediatorProxy CreateMediator(out ISwitchMediator mediator)
    {
        mediator = DispatchProxy.Create<ISwitchMediator, PassiveSwitchMediatorProxy>();
        return (PassiveSwitchMediatorProxy)(object)mediator;
    }

    private sealed class FakeSwitch(short id, string name, double value) : ISwitch
    {
        public short Id { get; } = id;

        public string Name { get; } = name;

        public string Description => string.Empty;

        public double Value { get; } = value;

        public bool Poll() => true;
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

    private sealed class IncrementingTimeProvider : TimeProvider
    {
        private DateTimeOffset current = new(2026, 6, 18, 12, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var result = current;
            current = current.AddSeconds(1);
            return result;
        }
    }

    public class PassiveSwitchMediatorProxy : DispatchProxy
    {
        private static readonly HashSet<string> ForbiddenMethods =
        [
            "Connect",
            "Disconnect",
            "Rescan",
            "GetDevice",
            "GetDeviceInfo",
            "Broadcast",
            "Action",
            "SendCommandString",
            "SendCommandBool",
            "SendCommandBlind",
            "SetSwitchValue",
        ];

        public List<ISwitchConsumer> Consumers { get; } = [];

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;

        public SwitchInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; set; }

        public bool ThrowOnRemove { get; set; }

        public bool ThrowOnAddConnected { get; set; }

        public bool ThrowOnAddDisconnected { get; set; }

        public bool AttachConnectedBeforeThrow { get; set; }

        public bool AttachDisconnectedBeforeThrow { get; set; }

        public bool ThrowOnRemoveConnected { get; set; }

        public bool ThrowOnRemoveDisconnected { get; set; }

        public bool ThrowOnGetInfo { get; set; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public List<string> ForbiddenCalls { get; } = [];

        public Task RaiseConnectedAsync() =>
            connected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync() =>
            disconnected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public void Broadcast(SwitchInfo deviceInfo)
        {
            CurrentInfo = deviceInfo;
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

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
                return nameof(PassiveSwitchMediatorProxy);
            }

            if (ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Switch telemetry must not call {methodName}.");
            }

            return methodName switch
            {
                "add_Connected" => AddConnected(args),
                "remove_Connected" => RemoveConnected(args),
                "add_Disconnected" => AddDisconnected(args),
                "remove_Disconnected" => RemoveDisconnected(args),
                "GetInfo" => GetInfo(),
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

            var consumer = args?.Length > 0 ? args[0] as ISwitchConsumer : null;
            consumer.Should().NotBeNull("the collector should register itself as an ISwitchConsumer");
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

            var consumer = args?.Length > 0 ? args[0] as ISwitchConsumer : null;
            Consumers.Remove(consumer!);
            return null;
        }

        private object? AddConnected(object?[]? args)
        {
            AddConnectedCalls++;
            var handler = args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            if (ThrowOnAddConnected)
            {
                if (AttachConnectedBeforeThrow && handler is not null)
                {
                    connected += handler;
                }

                throw new InvalidOperationException("Connected subscription failed.");
            }

            connected += handler;
            return null;
        }

        private object? RemoveConnected(object?[]? args)
        {
            RemoveConnectedCalls++;
            if (ThrowOnRemoveConnected)
            {
                throw new InvalidOperationException("Connected unsubscription failed.");
            }

            var handler = args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            connected -= handler;
            return null;
        }

        private object? AddDisconnected(object?[]? args)
        {
            AddDisconnectedCalls++;
            var handler = args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            if (ThrowOnAddDisconnected)
            {
                if (AttachDisconnectedBeforeThrow && handler is not null)
                {
                    disconnected += handler;
                }

                throw new InvalidOperationException("Disconnected subscription failed.");
            }

            disconnected += handler;
            return null;
        }

        private object? RemoveDisconnected(object?[]? args)
        {
            RemoveDisconnectedCalls++;
            if (ThrowOnRemoveDisconnected)
            {
                throw new InvalidOperationException("Disconnected unsubscription failed.");
            }

            var handler = args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            disconnected -= handler;
            return null;
        }

        private SwitchInfo GetInfo()
        {
            if (ThrowOnGetInfo)
            {
                throw new InvalidOperationException("GetInfo failed.");
            }

            return CurrentInfo;
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
