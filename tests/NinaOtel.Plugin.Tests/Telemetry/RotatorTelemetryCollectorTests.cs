using FluentAssertions;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class RotatorTelemetryCollectorTests
{
    [Fact]
    public void Start_RegistersCollectorAsRotatorConsumer()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialRotatorMetrics()
    {
        var mediator = new FakeRotatorMediator
        {
            CurrentInfo = new RotatorInfo
            {
                Connected = true,
                Name = "Pegasus Falcon",
                MechanicalPosition = 182.5f,
                Position = 17.25f,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "rotator_mechanical_angle",
            "rotator_angle");
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediator()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        mediator.Consumers.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_WhenMediatorRemovalFails_DoesNotThrow()
    {
        var mediator = new FakeRotatorMediator { ThrowOnRemove = true };
        var sink = new RecordingTelemetrySink();
        var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeRotatorMediator { ThrowOnRegister = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.rotator" &&
            record.Name == "rotator_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesRotatorMetrics()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = 182.5f,
            Position = 17.25f,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.rotator",
                "rotator_mechanical_angle",
                182.5,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["rotator_name"] = "Pegasus Falcon" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.rotator",
                "rotator_angle",
                17.25,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["rotator_name"] = "Pegasus Falcon" }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenRotatorNameIsBlank_UsesUnknownAttribute()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = " ",
            MechanicalPosition = 182.5f,
            Position = float.NaN,
        });

        sink.Records.Should().ContainSingle().Which.Attributes["rotator_name"].Should().Be("Unknown");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenRotatorNameIsNull_UsesUnknownAttribute()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = null!,
            MechanicalPosition = 182.5f,
            Position = float.NaN,
        });

        sink.Records.Should().ContainSingle().Which.Attributes["rotator_name"].Should().Be("Unknown");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenValuesAreUnavailableBeforeAnySample_DoesNotPublishMetrics()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = float.NaN,
            Position = float.NaN,
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenValueBecomesUnavailable_ClearsOnlyThatMetricAndPublishesAvailableMetric()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = 182.5f,
            Position = 17.25f,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = float.NaN,
            Position = 18.5f,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "rotator_mechanical_angle" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["rotator_name"], "Pegasus Falcon"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "rotator_angle" &&
            record.NumericValue == 18.5 &&
            Equals(record.Attributes["rotator_name"], "Pegasus Falcon"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenRotatorDisconnects_PublishesClearMetricsForPreviousRotator()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = 182.5f,
            Position = 17.25f,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = false,
            Name = "Pegasus Falcon",
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["rotator_name"], "Pegasus Falcon"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "rotator_mechanical_angle",
            "rotator_angle");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenRotatorNameChanges_ClearsOldMetricsAndPublishesNewMetrics()
    {
        var mediator = new FakeRotatorMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new RotatorTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = 182.5f,
            Position = 17.25f,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "NiteCrawler",
            MechanicalPosition = 203.75f,
            Position = 34.5f,
        });

        sink.Records.Should().HaveCount(4);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["rotator_name"], "Pegasus Falcon"));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(2)
            .And.OnlyContain(record => Equals(record.Attributes["rotator_name"], "NiteCrawler"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeRotatorMediator();
        using var collector = new RotatorTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(new RotatorInfo
        {
            Connected = true,
            Name = "Pegasus Falcon",
            MechanicalPosition = 182.5f,
            Position = 17.25f,
        });

        act.Should().NotThrow();
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

    private sealed class FakeRotatorMediator : IRotatorMediator
    {
        public List<IRotatorConsumer> Consumers { get; } = [];

        public RotatorInfo CurrentInfo { get; init; } = new();

        public bool ThrowOnRegister { get; init; }

        public bool ThrowOnRemove { get; init; }

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

        public event EventHandler<RotatorEventArgs>? Synced
        {
            add => throw new NotSupportedException("Rotator telemetry must not subscribe to Synced.");
            remove => throw new NotSupportedException("Rotator telemetry must not unsubscribe from Synced.");
        }

        public event Func<object, RotatorEventArgs, Task>? Moved
        {
            add => throw new NotSupportedException("Rotator telemetry must not subscribe to Moved.");
            remove => throw new NotSupportedException("Rotator telemetry must not unsubscribe from Moved.");
        }

        public event Func<object, RotatorEventArgs, Task>? MovedMechanical
        {
            add => throw new NotSupportedException("Rotator telemetry must not subscribe to MovedMechanical.");
            remove => throw new NotSupportedException("Rotator telemetry must not unsubscribe from MovedMechanical.");
        }

        public void RegisterHandler(IRotatorVM handler)
        {
        }

        public void RegisterConsumer(IRotatorConsumer consumer)
        {
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
            consumer.UpdateDeviceInfo(CurrentInfo);
        }

        public void RemoveConsumer(IRotatorConsumer consumer)
        {
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("Removal failed.");
            }

            Consumers.Remove(consumer);
        }

        public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(Array.Empty<string>());

        public Task<bool> Connect() => Task.FromResult(true);

        public Task Disconnect() => Task.CompletedTask;

        public void Broadcast(RotatorInfo deviceInfo)
        {
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public RotatorInfo GetInfo() => new();

        public string Action(string actionName, string actionParameters) => string.Empty;

        public string SendCommandString(string command, bool raw = true) => string.Empty;

        public bool SendCommandBool(string command, bool raw = true) => false;

        public void SendCommandBlind(string command, bool raw = true)
        {
        }

        public IDevice GetDevice() => throw new NotSupportedException();

        public void Sync(float skyAngle) => throw new NotSupportedException("Rotator telemetry must not sync.");

        public Task<float> MoveMechanical(float position, CancellationToken ct) =>
            throw new NotSupportedException("Rotator telemetry must not move.");

        public Task<float> Move(float position, CancellationToken ct) =>
            throw new NotSupportedException("Rotator telemetry must not move.");

        public Task<float> MoveRelative(float position, CancellationToken ct) =>
            throw new NotSupportedException("Rotator telemetry must not move.");

        public float GetTargetPosition(float position) =>
            throw new NotSupportedException("Rotator telemetry must not query target position.");

        public float GetTargetMechanicalPosition(float position) =>
            throw new NotSupportedException("Rotator telemetry must not query target mechanical position.");
    }
}
