using System.Reflection;
using FluentAssertions;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class FlatDeviceTelemetryCollectorTests
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

        var act = () => new FlatDeviceTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_SubscribesFlatDeviceEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new FlatDeviceTelemetryCollector(
            mediator,
            new RecordingTelemetrySink(),
            TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddOpenedCalls.Should().Be(1);
        proxy.AddClosedCalls.Should().Be(1);
        proxy.AddBrightnessChangedCalls.Should().Be(1);
        proxy.AddLightToggledCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(1);
        proxy.DisconnectedSubscriberCount.Should().Be(1);
        proxy.OpenedSubscriberCount.Should().Be(1);
        proxy.ClosedSubscriberCount.Should().Be(1);
        proxy.BrightnessChangedSubscriberCount.Should().Be(1);
        proxy.LightToggledSubscriberCount.Should().Be(1);
    }

    [Theory]
    [InlineData("connected", "calibrator_connected", "Cover/Calibrator connected")]
    [InlineData("disconnected", "calibrator_disconnected", "Cover/Calibrator disconnected")]
    [InlineData("opened", "calibrator_opened", "Cover opened")]
    [InlineData("closed", "calibrator_closed", "Cover closed")]
    public async Task LifecycleEvent_WhenRaised_PublishesLog(string eventName, string telemetryName, string body)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync(eventName);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.flat_device" &&
            record.Name == telemetryName &&
            record.Body == body &&
            record.Priority == TelemetryPriority.Normal &&
            record.Severity == TelemetrySeverity.Information);
    }

    [Fact]
    public async Task BrightnessChanged_WhenRaised_PublishesBrightnessLogWithFromToAttributes()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseBrightnessChangedAsync(10, 42);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.flat_device" &&
            record.Name == "calibrator_brightness" &&
            record.Body == "Calibrator brightness changed to 42" &&
            Equals(record.Attributes["title"], "Calibrator brightness changed") &&
            Equals(record.Attributes["calibrator_brightness_from"], 10) &&
            Equals(record.Attributes["calibrator_brightness_to"], 42));
    }

    [Fact]
    public async Task LightToggled_WhenRaised_PublishesLightStateFromMediatorInfo()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = new FlatDeviceInfo { LightOn = true };
        var expectedState = proxy.CurrentInfo.LocalizedLightOnState;
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync("lightToggled");

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.flat_device" &&
            record.Name == "calibrator_light_toggled" &&
            record.Body == $"Calibrator light: {expectedState}" &&
            Equals(record.Attributes["title"], "Calibrator light toggled") &&
            Equals(record.Attributes["calibrator_light_state"], expectedState));
    }

    [Fact]
    public async Task LightToggled_WhenMediatorInfoCannotBeRead_UsesUnknownState()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnGetInfo = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync("lightToggled");

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "calibrator_light_toggled" &&
            record.Body == "Calibrator light: Unknown" &&
            Equals(record.Attributes["calibrator_light_state"], "Unknown"));
    }

    [Fact]
    public async Task Event_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new FlatDeviceTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var act = () => proxy.RaiseAsync("opened");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_WhenUnsubscribeFails_LateEventsDoNotPublishLogs()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnOpenedRemove = true;
        var sink = new RecordingTelemetrySink();
        var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        await proxy.RaiseAsync("opened");

        proxy.OpenedSubscriberCount.Should().Be(1);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionThrowsAfterAttaching_UnsubscribesHandlersBeforeReturning()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnOpenedAddAfterSubscribe = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        sink.Records.Clear();
        await proxy.RaiseAsync("opened");

        proxy.RemoveOpenedCalls.Should().Be(1);
        proxy.OpenedSubscriberCount.Should().Be(0);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthRecord()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnClosedAdd = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.flat_device" &&
            record.Name == "flat_device_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Closed subscription failed."));
    }

    [Fact]
    public async Task Collector_DoesNotCallControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new FlatDeviceTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync("connected");
        await proxy.RaiseAsync("opened");
        await proxy.RaiseBrightnessChangedAsync(1, 2);
        await proxy.RaiseAsync("lightToggled");

        proxy.UnsupportedMemberCalls.Should().BeEmpty();
    }

    private static FlatDeviceMediatorProxy CreateMediator(out IFlatDeviceMediator mediator)
    {
        mediator = DispatchProxy.Create<IFlatDeviceMediator, FlatDeviceMediatorProxy>();
        return (FlatDeviceMediatorProxy)mediator;
    }

    private sealed class FlatDeviceMediatorProxy : DispatchProxy
    {
        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private Func<object, EventArgs, Task>? opened;
        private Func<object, EventArgs, Task>? closed;
        private Func<object, EventArgs, Task>? lightToggled;
        private Func<object, FlatDeviceBrightnessChangedEventArgs, Task>? brightnessChanged;

        public FlatDeviceInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnClosedAdd { get; set; }

        public bool ThrowOnOpenedAddAfterSubscribe { get; set; }

        public bool ThrowOnOpenedRemove { get; set; }

        public bool ThrowOnGetInfo { get; set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int AddOpenedCalls { get; private set; }

        public int AddClosedCalls { get; private set; }

        public int AddBrightnessChangedCalls { get; private set; }

        public int AddLightToggledCalls { get; private set; }

        public int RemoveOpenedCalls { get; private set; }

        public List<string> UnsupportedMemberCalls { get; } = [];

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public int OpenedSubscriberCount => opened?.GetInvocationList().Length ?? 0;

        public int ClosedSubscriberCount => closed?.GetInvocationList().Length ?? 0;

        public int BrightnessChangedSubscriberCount => brightnessChanged?.GetInvocationList().Length ?? 0;

        public int LightToggledSubscriberCount => lightToggled?.GetInvocationList().Length ?? 0;

        public async Task RaiseAsync(string eventName)
        {
            var handler = eventName switch
            {
                "connected" => connected,
                "disconnected" => disconnected,
                "opened" => opened,
                "closed" => closed,
                "lightToggled" => lightToggled,
                _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null),
            };

            if (handler is not null)
            {
                await handler.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task RaiseBrightnessChangedAsync(int from, int to)
        {
            if (brightnessChanged is not null)
            {
                await brightnessChanged.Invoke(
                    this,
                    new FlatDeviceBrightnessChangedEventArgs(from, to));
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var methodName = targetMethod?.Name ?? string.Empty;
            switch (methodName)
            {
                case "add_Connected":
                    AddConnectedCalls++;
                    connected += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Connected":
                    connected -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Disconnected":
                    AddDisconnectedCalls++;
                    disconnected += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Disconnected":
                    disconnected -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Opened":
                    AddOpenedCalls++;
                    opened += (Func<object, EventArgs, Task>)args![0]!;
                    if (ThrowOnOpenedAddAfterSubscribe)
                    {
                        throw new InvalidOperationException("Opened subscription failed.");
                    }

                    return null;
                case "remove_Opened":
                    RemoveOpenedCalls++;
                    if (ThrowOnOpenedRemove)
                    {
                        throw new InvalidOperationException("Opened unsubscription failed.");
                    }

                    opened -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Closed":
                    AddClosedCalls++;
                    if (ThrowOnClosedAdd)
                    {
                        throw new InvalidOperationException("Closed subscription failed.");
                    }

                    closed += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Closed":
                    closed -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_BrightnessChanged":
                    AddBrightnessChangedCalls++;
                    brightnessChanged += (Func<object, FlatDeviceBrightnessChangedEventArgs, Task>)args![0]!;
                    return null;
                case "remove_BrightnessChanged":
                    brightnessChanged -= (Func<object, FlatDeviceBrightnessChangedEventArgs, Task>)args![0]!;
                    return null;
                case "add_LightToggled":
                    AddLightToggledCalls++;
                    lightToggled += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_LightToggled":
                    lightToggled -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "GetInfo":
                    if (ThrowOnGetInfo)
                    {
                        throw new InvalidOperationException("GetInfo failed.");
                    }

                    return CurrentInfo;
                default:
                    UnsupportedMemberCalls.Add(methodName);
                    return DefaultReturnValue(targetMethod?.ReturnType);
            }
        }

        private static object? DefaultReturnValue(Type? returnType)
        {
            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var fromResult = typeof(Task)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethod)
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
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
            throw new InvalidOperationException("Sink failed.");
    }
}
