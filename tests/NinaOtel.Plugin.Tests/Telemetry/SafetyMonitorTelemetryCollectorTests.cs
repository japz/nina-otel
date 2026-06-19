using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class SafetyMonitorTelemetryCollectorTests
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

        var act = () => new SafetyMonitorTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_RegistersConsumerAndSubscribesSafetyEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddIsSafeChangedCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(1);
        proxy.DisconnectedSubscriberCount.Should().Be(1);
        proxy.IsSafeChangedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentConnectedInfo_PublishesInitialSafetyGaugeAndBeginsPeriod()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());

        collector.Start();
        var initialGaugeTimestamp = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_issafe")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        proxy.RaiseIsSafeChanged(isSafe: false);

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.safety" &&
            record.Name == "safety_issafe" &&
            record.NumericValue == 0.0 &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_safe_period" &&
            record.Timestamp == initialGaugeTimestamp &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["timeEnd"], initialGaugeTimestamp.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Connected_PublishesConnectionLogCurrentGaugeAndBeginsCurrentSafetyPeriod()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        var periodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_connected")
            .Subject
            .Timestamp;
        proxy.RaiseIsSafeChanged(isSafe: false);

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_connected" &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "safety_issafe" &&
            record.NumericValue == 1.0 &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_safe_period" &&
            record.Timestamp == periodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["timeEnd"], periodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoCannotBeRead_DoesNotInventSafetyStateOrPeriod()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnGetInfo = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_connected" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
        sink.Records.Should().NotContain(record => record.Name == "safety_issafe");
        sink.Records.Should().NotContain(record => record.Name == "safety_safe_period");
        sink.Records.Should().NotContain(record => record.Name == "safety_unsafe_period");
    }

    [Fact]
    public async Task IsSafeChanged_WhenStateChangesFromSafeToUnsafe_PublishesGaugeStateLogAndPeriodClose()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        await proxy.RaiseConnectedAsync();
        var periodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_connected")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        proxy.RaiseIsSafeChanged(isSafe: false);

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "safety_issafe" &&
            record.NumericValue == 0.0 &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_safe_state" &&
            record.Body == "Safety state changed to UNSAFE" &&
            Equals(record.Attributes["title"], "Safety state changed") &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["safety_issafe"], false));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_safe_period" &&
            record.Timestamp == periodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["timeEnd"], periodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsGaugeClosesActivePeriodAndResetsState()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        await proxy.RaiseConnectedAsync();
        proxy.RaiseIsSafeChanged(isSafe: false);
        var unsafePeriodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_safe_state")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().NotContain(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "safety_issafe" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_unsafe_period" &&
            record.Timestamp == unsafePeriodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["timeEnd"], unsafePeriodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownMonitorState_PublishesDisconnectLogWithUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
        sink.Records.Should().NotContain(record => record.Name == "safety_issafe");
        sink.Records.Should().NotContain(record => record.Name == "safety_safe_period");
        sink.Records.Should().NotContain(record => record.Name == "safety_unsafe_period");
    }

    [Fact]
    public async Task Disconnected_WhenUnknownDisconnectAlreadyLogged_SuppressesDuplicateUntilConnectedAgain()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));

        sink.Records.Clear();
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        await proxy.RaiseConnectedAsync();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
    }

    [Fact]
    public async Task Disconnected_WhenSafetyStateArrivesAfterUnknownDisconnect_ClearsGaugeAndClosesPeriod()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        sink.Records.Clear();

        proxy.RaiseIsSafeChanged(isSafe: true);
        var periodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_safe_state")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_disconnected" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "safety_issafe" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "safety_safe_period" &&
            record.Timestamp == periodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown") &&
            Equals(record.Attributes["timeEnd"], periodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenMonitorNameIsBlank_UsesUnknownAttribute()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(" ", isSafe: true));

        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_issafe" &&
            Equals(record.Attributes["safety_monitor_name"], "Unknown"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnectedInfoChanges_ClosesPreviousPeriodAndBeginsReplacementPeriod()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());

        collector.UpdateDeviceInfo(ConnectedInfo("Monitor A", isSafe: true));
        var monitorAPeriodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_issafe")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("Monitor B", isSafe: false));
        var monitorBPeriodStart = sink.Records.Should()
            .ContainSingle(record =>
                record.Name == "safety_issafe" &&
                record.NumericValue == 0.0 &&
                Equals(record.Attributes["safety_monitor_name"], "Monitor B"))
            .Subject
            .Timestamp;

        sink.Records.Should().HaveCount(3);
        sink.Records[0].Should().Match<TelemetryRecord>(record =>
            record.Name == "safety_issafe" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor A"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_safe_period" &&
            record.Timestamp == monitorAPeriodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor A") &&
            Equals(record.Attributes["timeEnd"], monitorBPeriodStart.ToUnixTimeMilliseconds()));
        sink.Records[2].Should().Match<TelemetryRecord>(record =>
            record.Name == "safety_issafe" &&
            record.NumericValue == 0.0 &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor B"));

        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("Monitor B", isSafe: true));

        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_safe_state" &&
            record.Body == "Safety state changed to SAFE" &&
            Equals(record.Attributes["title"], "Safety state changed") &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor B") &&
            Equals(record.Attributes["safety_issafe"], true));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_unsafe_period" &&
            record.Timestamp == monitorBPeriodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor B") &&
            Equals(record.Attributes["timeEnd"], monitorBPeriodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_issafe" &&
            record.NumericValue == 1.0 &&
            Equals(record.Attributes["safety_monitor_name"], "Monitor B"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDisconnected_ClearsPreviousGaugeClosesActivePeriodAndIgnoresDuplicates()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());

        collector.UpdateDeviceInfo(ConnectedInfo("Safety Monitor", isSafe: true));
        var periodStart = sink.Records.Should()
            .ContainSingle(record => record.Name == "safety_issafe")
            .Subject
            .Timestamp;
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new SafetyMonitorInfo
        {
            Connected = false,
            Name = "Ignored",
        });
        collector.UpdateDeviceInfo(new SafetyMonitorInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_issafe" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "safety_safe_period" &&
            record.Timestamp == periodStart &&
            Equals(record.Attributes["safety_monitor_name"], "Safety Monitor") &&
            Equals(record.Attributes["timeEnd"], periodStart.AddSeconds(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_AfterDispose_DoesNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Dispose();

        collector.UpdateDeviceInfo(ConnectedInfo("Safety Monitor", isSafe: true));

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrowOrRetryOrRemove()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () =>
        {
            collector.Start();
            collector.Start();
            collector.Dispose();
        };

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(0);
        proxy.RemoveCalls.Should().Be(0);
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.safety" &&
            record.Name == "safety_monitor_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Registration failed."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddIsSafeChangedCalls.Should().Be(0);
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "safety_monitor_collector.registration_failed");
    }

    [Fact]
    public async Task Callbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        using var collector = new SafetyMonitorTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var updateAct = () => collector.UpdateDeviceInfo(ConnectedInfo("Safety Monitor", isSafe: false));
        var stateAct = () => proxy.RaiseIsSafeChanged(isSafe: false);
        var connectedAct = async () => await proxy.RaiseConnectedAsync();
        var disconnectedAct = async () => await proxy.RaiseDisconnectedAsync();

        updateAct.Should().NotThrow();
        stateAct.Should().NotThrow();
        await connectedAct.Should().NotThrowAsync();
        await disconnectedAct.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_UnsubscribesEventsAndRemovesConsumerOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.Consumers.Should().BeEmpty();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveIsSafeChangedCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(0);
        proxy.DisconnectedSubscriberCount.Should().Be(0);
        proxy.IsSafeChangedSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_WhenMediatorTeardownFails_DoesNotThrowAndAttemptsEveryTeardownStep()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemoveConnected = true;
        proxy.ThrowOnRemoveDisconnected = true;
        proxy.ThrowOnRemoveIsSafeChanged = true;
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveIsSafeChangedCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
    }

    [Fact]
    public async Task Collector_DoesNotCallSafetyMonitorControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("Safety Monitor", isSafe: true);
        var sink = new RecordingTelemetrySink();
        using var collector = new SafetyMonitorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        await proxy.RaiseConnectedAsync();
        proxy.RaiseIsSafeChanged(isSafe: false);
        await proxy.RaiseDisconnectedAsync();
        collector.Dispose();

        proxy.ForbiddenCalls.Should().BeEmpty();
    }

    private static SafetyMonitorInfo ConnectedInfo(string? safetyMonitorName, bool isSafe) =>
        new()
        {
            Connected = true,
            Name = safetyMonitorName!,
            IsSafe = isSafe,
        };

    private static PassiveSafetyMonitorMediatorProxy CreateMediator(out ISafetyMonitorMediator mediator)
    {
        mediator = DispatchProxy.Create<ISafetyMonitorMediator, PassiveSafetyMonitorMediatorProxy>();
        return (PassiveSafetyMonitorMediatorProxy)(object)mediator;
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

    public class PassiveSafetyMonitorMediatorProxy : DispatchProxy
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
        ];

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private EventHandler<IsSafeEventArgs>? isSafeChanged;

        public List<ISafetyMonitorConsumer> Consumers { get; } = [];

        public SafetyMonitorInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; set; }

        public bool ThrowOnRemove { get; set; }

        public bool ThrowOnAddConnected { get; set; }

        public bool ThrowOnAddDisconnected { get; set; }

        public bool ThrowOnAddIsSafeChanged { get; set; }

        public bool ThrowOnGetInfo { get; set; }

        public bool ThrowOnRemoveConnected { get; set; }

        public bool ThrowOnRemoveDisconnected { get; set; }

        public bool ThrowOnRemoveIsSafeChanged { get; set; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int AddIsSafeChangedCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int RemoveIsSafeChangedCalls { get; private set; }

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public int IsSafeChangedSubscriberCount => isSafeChanged?.GetInvocationList().Length ?? 0;

        public List<string> ForbiddenCalls { get; } = [];

        public Task RaiseConnectedAsync() =>
            connected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync() =>
            disconnected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public void RaiseIsSafeChanged(bool isSafe)
        {
            CurrentInfo.IsSafe = isSafe;
            isSafeChanged?.Invoke(this, new IsSafeEventArgs(isSafe));
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
                return nameof(PassiveSafetyMonitorMediatorProxy);
            }

            if (methodName is "add_Connected")
            {
                return AddConnected(args);
            }

            if (methodName is "remove_Connected")
            {
                return RemoveConnected(args);
            }

            if (methodName is "add_Disconnected")
            {
                return AddDisconnected(args);
            }

            if (methodName is "remove_Disconnected")
            {
                return RemoveDisconnected(args);
            }

            if (methodName is "add_IsSafeChanged")
            {
                return AddIsSafeChanged(args);
            }

            if (methodName is "remove_IsSafeChanged")
            {
                return RemoveIsSafeChanged(args);
            }

            if (ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Safety monitor telemetry must not call {methodName}.");
            }

            return methodName switch
            {
                "RegisterConsumer" => RegisterConsumer(args),
                "RemoveConsumer" => RemoveConsumer(args),
                "GetInfo" => GetInfo(),
                "RegisterHandler" => null,
                _ => DefaultReturnValue(targetMethod.ReturnType),
            };
        }

        private SafetyMonitorInfo GetInfo()
        {
            if (ThrowOnGetInfo)
            {
                throw new InvalidOperationException("GetInfo failed.");
            }

            return CurrentInfo;
        }

        private object? RegisterConsumer(object?[]? args)
        {
            RegisterCalls++;
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            var consumer = args?.Length > 0 ? args[0] as ISafetyMonitorConsumer : null;
            consumer.Should().NotBeNull("the collector should register itself as an ISafetyMonitorConsumer");
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

            var consumer = args?.Length > 0 ? args[0] as ISafetyMonitorConsumer : null;
            Consumers.Remove(consumer!);
            return null;
        }

        private object? AddConnected(object?[]? args)
        {
            AddConnectedCalls++;
            if (ThrowOnAddConnected)
            {
                throw new InvalidOperationException("Connected subscription failed.");
            }

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
            if (ThrowOnAddDisconnected)
            {
                throw new InvalidOperationException("Disconnected subscription failed.");
            }

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

        private object? AddIsSafeChanged(object?[]? args)
        {
            AddIsSafeChangedCalls++;
            if (ThrowOnAddIsSafeChanged)
            {
                throw new InvalidOperationException("IsSafeChanged subscription failed.");
            }

            isSafeChanged += args?.Length > 0 ? args[0] as EventHandler<IsSafeEventArgs> : null;
            return null;
        }

        private object? RemoveIsSafeChanged(object?[]? args)
        {
            RemoveIsSafeChangedCalls++;
            if (ThrowOnRemoveIsSafeChanged)
            {
                throw new InvalidOperationException("IsSafeChanged unsubscription failed.");
            }

            isSafeChanged -= args?.Length > 0 ? args[0] as EventHandler<IsSafeEventArgs> : null;
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
