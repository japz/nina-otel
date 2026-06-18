using FluentAssertions;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using OxyPlot;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class FocuserTelemetryCollectorTests
{
    [Fact]
    public void Start_RegistersCollectorAsFocuserConsumer()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediator()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();

        mediator.Consumers.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeFocuserMediator { ThrowOnRegister = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "focuser_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenFocuserDisconnectedBeforeAnyConnectedSample_DoesNotPublishMetrics()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = false,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenFocuserDisconnects_PublishesClearMetricsForPreviousFocuser()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = false,
            Name = "EAF",
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record => double.IsNaN(record.NumericValue!.Value));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "focuser_position",
            "focuser_temperature");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenFocuserNameChanges_PublishesClearMetricsForPreviousFocuser()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "Moonlite",
            Position = 2048,
            Temperature = 2.5,
        });

        sink.Records.Should().HaveCount(4);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["focuser_name"], "EAF"));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["focuser_name"], "Moonlite"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesPositionAndTemperatureMetrics()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.focuser",
                "focuser_position",
                1234,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["focuser_name"] = "EAF" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.focuser",
                "focuser_temperature",
                -4.5,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["focuser_name"] = "EAF" }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeFocuserMediator();
        using var collector = new FocuserTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenTemperatureIsNaN_PublishesOnlyPosition()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = double.NaN,
        });

        sink.Records.Should().ContainSingle().Which.Name.Should().Be("focuser_position");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenTemperatureBecomesNaN_PublishesTemperatureClearMetric()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1235,
            Temperature = double.NaN,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "focuser_position" &&
            record.NumericValue == 1235);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "focuser_temperature" &&
            double.IsNaN(record.NumericValue!.Value));
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

    private sealed class FakeFocuserMediator : IFocuserMediator
    {
        public List<IFocuserConsumer> Consumers { get; } = [];

        public bool ThrowOnRegister { get; init; }

        public event Func<object, EventArgs, Task>? Connected
        {
            add { }
            remove { }
        }

        public event Func<object, EventArgs, Task>? Disconnected
        {
            add { }
            remove { }
        }

        public void RegisterHandler(IFocuserVM handler)
        {
        }

        public void RegisterConsumer(IFocuserConsumer consumer)
        {
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
        }

        public void RemoveConsumer(IFocuserConsumer consumer) => Consumers.Remove(consumer);

        public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(Array.Empty<string>());

        public Task<bool> Connect() => Task.FromResult(true);

        public Task Disconnect() => Task.CompletedTask;

        public void Broadcast(FocuserInfo deviceInfo)
        {
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public FocuserInfo GetInfo() => new();

        public string Action(string actionName, string actionParameters) => string.Empty;

        public string SendCommandString(string command, bool raw = true) => string.Empty;

        public bool SendCommandBool(string command, bool raw = true) => false;

        public void SendCommandBlind(string command, bool raw = true)
        {
        }

        public IDevice GetDevice() => throw new NotSupportedException();

        public void ToggleTempComp(bool tempComp)
        {
        }

        public Task<int> MoveFocuser(int position, CancellationToken ct) => Task.FromResult(position);

        public Task<int> MoveFocuserRelative(int position, CancellationToken ct) => Task.FromResult(position);

        public Task<int> MoveFocuserByTemperatureRelative(double temperature, double slope, CancellationToken ct) =>
            Task.FromResult(0);

        public void BroadcastSuccessfulAutoFocusRun(AutoFocusInfo info)
        {
        }

        public void BroadcastNewAutoFocusPoint(DataPoint dataPoint)
        {
        }

        public void BroadcastUserFocused(FocuserInfo info)
        {
        }

        public void BroadcastAutoFocusRunStarting()
        {
        }
    }
}
