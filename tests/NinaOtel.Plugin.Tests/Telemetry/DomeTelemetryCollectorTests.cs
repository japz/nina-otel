using System.Reflection;
using FluentAssertions;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class DomeTelemetryCollectorTests
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

        var act = () => new DomeTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_SubscribesDomeEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new DomeTelemetryCollector(
            mediator,
            new RecordingTelemetrySink(),
            TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddOpenedCalls.Should().Be(1);
        proxy.AddClosedCalls.Should().Be(1);
        proxy.AddHomedCalls.Should().Be(1);
        proxy.AddParkedCalls.Should().Be(1);
        proxy.AddSlewedCalls.Should().Be(1);
        proxy.ConnectedSubscriberCount.Should().Be(1);
        proxy.DisconnectedSubscriberCount.Should().Be(1);
        proxy.OpenedSubscriberCount.Should().Be(1);
        proxy.ClosedSubscriberCount.Should().Be(1);
        proxy.HomedSubscriberCount.Should().Be(1);
        proxy.ParkedSubscriberCount.Should().Be(1);
        proxy.SlewedSubscriberCount.Should().Be(1);
    }

    [Theory]
    [InlineData("connected", "dome_connected", "Dome connected")]
    [InlineData("disconnected", "dome_disconnected", "Dome disconnected")]
    [InlineData("opened", "dome_shutter_open", "Dome shutter opened")]
    [InlineData("closed", "dome_shutter_close", "Dome shutter closed")]
    [InlineData("homed", "dome_shutter_homed", "Dome homed")]
    [InlineData("parked", "dome_shutter_parked", "Dome parked")]
    public async Task LifecycleEvent_WhenRaised_PublishesLog(
        string eventName,
        string telemetryName,
        string body)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync(eventName);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.dome" &&
            record.Name == telemetryName &&
            record.Body == body &&
            record.Priority == TelemetryPriority.Normal &&
            record.Severity == TelemetrySeverity.Information);
    }

    [Fact]
    public async Task Slewed_WhenRaised_PublishesSlewedLogWithFromToAttributes()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseSlewedAsync(12.25, 181.375);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.dome" &&
            record.Name == "dome_slewed" &&
            record.Body == "Dome slewed azimuth to 181.38\u00b0" &&
            record.Priority == TelemetryPriority.Normal &&
            record.Severity == TelemetrySeverity.Information &&
            Equals(record.Attributes["title"], "Dome slewed azimuth") &&
            Equals(record.Attributes["dome_slewed_from"], 12.25) &&
            Equals(record.Attributes["dome_slewed_to"], 181.375));
    }

    [Fact]
    public async Task Slewed_WhenEventArgsAreNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = proxy.RaiseSlewedWithNullArgsAsync;

        await act.Should().NotThrowAsync();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Event_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new DomeTelemetryCollector(
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
        var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        await proxy.RaiseAsync("opened");

        proxy.OpenedSubscriberCount.Should().Be(1);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesDomeEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var collector = new DomeTelemetryCollector(
            mediator,
            new RecordingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveOpenedCalls.Should().Be(1);
        proxy.RemoveClosedCalls.Should().Be(1);
        proxy.RemoveHomedCalls.Should().Be(1);
        proxy.RemoveParkedCalls.Should().Be(1);
        proxy.RemoveSlewedCalls.Should().Be(1);
        proxy.TotalSubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionThrowsAfterAttaching_UnsubscribesHandlersBeforeReturning()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnOpenedAddAfterSubscribe = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        sink.Records.Clear();
        await proxy.RaiseAsync("opened");

        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveOpenedCalls.Should().Be(1);
        proxy.TotalSubscriberCount.Should().Be(0);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthRecord()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnClosedAdd = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.dome" &&
            record.Name == "dome_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Closed subscription failed."));
    }

    [Fact]
    public void Start_WhenCleanupAfterSubscriptionFailureThrows_StillPublishesHealthRecord()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnClosedAdd = true;
        proxy.ThrowOnConnectedRemove = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = collector.Start;

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "dome_collector.registration_failed");
    }

    [Fact]
    public async Task Collector_DoesNotCallControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new DomeTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await proxy.RaiseAsync("connected");
        await proxy.RaiseAsync("opened");
        await proxy.RaiseSlewedAsync(1.5, 2.5);

        proxy.UnsupportedMemberCalls.Should().BeEmpty();
    }

    private static DomeMediatorProxy CreateMediator(out IDomeMediator mediator)
    {
        mediator = DispatchProxy.Create<IDomeMediator, DomeMediatorProxy>();
        return (DomeMediatorProxy)(object)mediator;
    }

    private class DomeMediatorProxy : DispatchProxy
    {
        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private Func<object, EventArgs, Task>? opened;
        private Func<object, EventArgs, Task>? closed;
        private Func<object, EventArgs, Task>? homed;
        private Func<object, EventArgs, Task>? parked;
        private Func<object, DomeEventArgs, Task>? slewed;

        public bool ThrowOnClosedAdd { get; set; }

        public bool ThrowOnOpenedAddAfterSubscribe { get; set; }

        public bool ThrowOnConnectedRemove { get; set; }

        public bool ThrowOnOpenedRemove { get; set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int AddOpenedCalls { get; private set; }

        public int AddClosedCalls { get; private set; }

        public int AddHomedCalls { get; private set; }

        public int AddParkedCalls { get; private set; }

        public int AddSlewedCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int RemoveOpenedCalls { get; private set; }

        public int RemoveClosedCalls { get; private set; }

        public int RemoveHomedCalls { get; private set; }

        public int RemoveParkedCalls { get; private set; }

        public int RemoveSlewedCalls { get; private set; }

        public List<string> UnsupportedMemberCalls { get; } = [];

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public int OpenedSubscriberCount => opened?.GetInvocationList().Length ?? 0;

        public int ClosedSubscriberCount => closed?.GetInvocationList().Length ?? 0;

        public int HomedSubscriberCount => homed?.GetInvocationList().Length ?? 0;

        public int ParkedSubscriberCount => parked?.GetInvocationList().Length ?? 0;

        public int SlewedSubscriberCount => slewed?.GetInvocationList().Length ?? 0;

        public int TotalSubscriberCount =>
            ConnectedSubscriberCount +
            DisconnectedSubscriberCount +
            OpenedSubscriberCount +
            ClosedSubscriberCount +
            HomedSubscriberCount +
            ParkedSubscriberCount +
            SlewedSubscriberCount;

        public async Task RaiseAsync(string eventName)
        {
            var handler = eventName switch
            {
                "connected" => connected,
                "disconnected" => disconnected,
                "opened" => opened,
                "closed" => closed,
                "homed" => homed,
                "parked" => parked,
                _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null),
            };

            if (handler is null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList().Cast<Func<object, EventArgs, Task>>())
            {
                await subscriber(this, EventArgs.Empty);
            }
        }

        public async Task RaiseSlewedAsync(double from, double to)
        {
            if (slewed is null)
            {
                return;
            }

            foreach (var subscriber in slewed.GetInvocationList().Cast<Func<object, DomeEventArgs, Task>>())
            {
                await subscriber(this, new DomeEventArgs(from, to));
            }
        }

        public async Task RaiseSlewedWithNullArgsAsync()
        {
            if (slewed is null)
            {
                return;
            }

            foreach (var subscriber in slewed.GetInvocationList().Cast<Func<object, DomeEventArgs, Task>>())
            {
                await subscriber(this, null!);
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
                    RemoveConnectedCalls++;
                    if (ThrowOnConnectedRemove)
                    {
                        throw new InvalidOperationException("Connected unsubscription failed.");
                    }

                    connected -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Disconnected":
                    AddDisconnectedCalls++;
                    disconnected += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Disconnected":
                    RemoveDisconnectedCalls++;
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
                    RemoveClosedCalls++;
                    closed -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Homed":
                    AddHomedCalls++;
                    homed += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Homed":
                    RemoveHomedCalls++;
                    homed -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Parked":
                    AddParkedCalls++;
                    parked += (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "remove_Parked":
                    RemoveParkedCalls++;
                    parked -= (Func<object, EventArgs, Task>)args![0]!;
                    return null;
                case "add_Slewed":
                    AddSlewedCalls++;
                    slewed += (Func<object, DomeEventArgs, Task>)args![0]!;
                    return null;
                case "remove_Slewed":
                    RemoveSlewedCalls++;
                    slewed -= (Func<object, DomeEventArgs, Task>)args![0]!;
                    return null;
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
