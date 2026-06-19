using FluentAssertions;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class CameraTelemetryCollectorTests
{
    [Fact]
    public void Start_RegistersCollectorAsCameraConsumerAndSubscribesCameraEventsOnce()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        mediator.RegisterCalls.Should().Be(1);
        mediator.AddConnectedCalls.Should().Be(1);
        mediator.AddDisconnectedCalls.Should().Be(1);
        mediator.ConnectedSubscriberCount.Should().Be(1);
        mediator.DisconnectedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialCameraMetrics()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
                CoolerPower = 42.25,
                HasBattery = true,
                Battery = 86,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "camera_sensor_temperature",
            "camera_cooler_power",
            "camera_battery_level");
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediator()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();

        mediator.Consumers.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeCameraMediator { ThrowOnRegister = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        mediator.AddConnectedCalls.Should().Be(0);
        mediator.AddDisconnectedCalls.Should().Be(0);
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.camera" &&
            record.Name == "camera_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task Connected_PublishesCameraConnectionLogWithCurrentConnectedCameraName()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.camera" &&
            record.Name == "camera_connected" &&
            record.Body == "Camera connected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoIsUnavailable_UsesLastConnectedCameraName()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();
        mediator.CurrentInfo = new CameraInfo
        {
            Connected = false,
            Name = "Ignored stale name",
        };

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "camera_connected" &&
            record.Body == "Camera connected" &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
    }

    [Fact]
    public async Task Connected_WhenCameraNameChanges_ClearsPreviousCameraMetricsBeforeUpdatingName()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();
        mediator.CurrentInfo = new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
        };

        await mediator.RaiseConnectedAsync();
        mediator.Broadcast(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = -7.25,
        });

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "camera_sensor_temperature" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
                CoolerPower = 42.25,
                HasBattery = true,
                Battery = 86,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.camera" &&
            record.Name == "camera_disconnected" &&
            record.Body == "Camera disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(3)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["camera_name"], "ASI2600MM"));

        sink.Records.Clear();
        mediator.CurrentInfo = new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
        };
        await mediator.RaiseConnectedAsync();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "camera_disconnected" &&
            Equals(record.Attributes["camera_name"], "QHY268M"));
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownCamera_SuppressesDuplicateUntilConnectedAgain()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "camera_disconnected" &&
            record.Body == "Camera disconnected" &&
            Equals(record.Attributes["camera_name"], "Unknown"));

        sink.Records.Clear();
        mediator.CurrentInfo = new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
        };
        await mediator.RaiseConnectedAsync();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "camera_disconnected" &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeCameraMediator { ThrowOnAddDisconnected = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        mediator.RegisterCalls.Should().Be(1);
        mediator.AddConnectedCalls.Should().Be(1);
        mediator.AddDisconnectedCalls.Should().Be(1);
        mediator.RemoveConnectedCalls.Should().Be(1);
        mediator.RemoveCalls.Should().Be(1);
        mediator.ConnectedSubscriberCount.Should().Be(0);
        mediator.DisconnectedSubscriberCount.Should().Be(0);
        mediator.Consumers.Should().BeEmpty();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.camera" &&
            record.Name == "camera_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Disconnected subscription failed."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFailsAfterInitialMetrics_ClearsThoseMetrics()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnAddDisconnected = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "camera_sensor_temperature" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionFails_LateMediatorEventsDoNotPublishLifecycleLogs()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnAddDisconnected = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenRollbackRemoveConsumerFails_LateDeviceUpdatesDoNotPublishMetrics()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnAddDisconnected = true,
            ThrowOnRemove = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
                Temperature = -8.5,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        mediator.Broadcast(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -7.25,
        });

        sink.Records.Should().BeEmpty();
        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public async Task Start_WhenEventAccessorAttachesThenThrows_RollbackUnsubscribesLateLifecycleCallback()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnAddConnected = true,
            AttachConnectedBeforeThrow = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().BeEmpty();
        mediator.ConnectedSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_WhenRollbackUnsubscribeFailedAfterPartialAttach_RetriesUnsubscribe()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnAddConnected = true,
            AttachConnectedBeforeThrow = true,
            ThrowOnRemoveConnected = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        var sink = new RecordingTelemetrySink();
        var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        mediator.RemoveConnectedCalls.Should().Be(2);
    }

    [Fact]
    public async Task Callbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var mediator = new FakeCameraMediator
        {
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        using var collector = new CameraTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var connectedAct = async () => await mediator.RaiseConnectedAsync();
        var disconnectedAct = async () => await mediator.RaiseDisconnectedAsync();

        await connectedAct.Should().NotThrowAsync();
        await disconnectedAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_WhenEventUnsubscriptionFails_DoesNotThrowAndLateEventsDoNotPublish()
    {
        var mediator = new FakeCameraMediator
        {
            ThrowOnRemoveConnected = true,
            ThrowOnRemoveDisconnected = true,
            CurrentInfo = new CameraInfo
            {
                Connected = true,
                Name = "ASI2600MM",
            },
        };
        var sink = new RecordingTelemetrySink();
        var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        mediator.RemoveConnectedCalls.Should().Be(1);
        mediator.RemoveDisconnectedCalls.Should().Be(1);
        mediator.RemoveCalls.Should().Be(1);

        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesCameraMetrics()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -8.5,
            CoolerPower = 42.25,
            HasBattery = true,
            Battery = 86,
        });

        sink.Records.Should().HaveCount(3);
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.camera",
                "camera_sensor_temperature",
                -8.5,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["camera_name"] = "ASI2600MM" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.camera",
                "camera_cooler_power",
                42.25,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["camera_name"] = "ASI2600MM" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.camera",
                "camera_battery_level",
                86,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["camera_name"] = "ASI2600MM" }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenCameraNameIsBlank_UsesUnknownAttribute()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = " ",
            Temperature = -8.5,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().ContainSingle().Which.Attributes["camera_name"].Should().Be("Unknown");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenValuesAreUnavailableBeforeAnySample_DoesNotPublishMetrics()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenValuesBecomeUnavailable_PublishesClearMetricsForPreviouslyEmittedValues()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -8.5,
            CoolerPower = 42.25,
            HasBattery = true,
            Battery = 86,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().HaveCount(3);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "camera_sensor_temperature",
            "camera_cooler_power",
            "camera_battery_level");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenQhySensorValuesAreAvailable_PublishesQhyMetrics()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(
            mediator,
            sink,
            TimeProvider.System,
            () => new QhyCameraSensorTelemetry(1012.4, 38.5));

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.camera",
                "qhy_sensor_air_pressure",
                1012.4,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["camera_name"] = "QHY268M" }),
            options => options.Excluding(record => record.Timestamp));
        sink.Records.Should().ContainEquivalentOf(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.camera",
                "qhy_sensor_humidity",
                38.5,
                TelemetryPriority.Normal,
                new Dictionary<string, object?> { ["camera_name"] = "QHY268M" }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenQhySensorValuesAreUnavailableBeforeAnySample_DoesNotPublishQhyMetrics()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(
            mediator,
            sink,
            TimeProvider.System,
            () => new QhyCameraSensorTelemetry(double.NaN, double.NaN));

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenOnlyOneQhySensorValueIsAvailable_PublishesOnlyAvailableMetric()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(
            mediator,
            sink,
            TimeProvider.System,
            () => new QhyCameraSensorTelemetry(1012.4, double.NaN));

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "qhy_sensor_air_pressure" &&
            record.NumericValue == 1012.4 &&
            Equals(record.Attributes["camera_name"], "QHY268M"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenQhySensorValuesBecomeUnavailable_PublishesClearMetricsForPreviouslyEmittedValues()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        QhyCameraSensorTelemetry? qhyTelemetry = new(1012.4, 38.5);
        using var collector = new CameraTelemetryCollector(
            mediator,
            sink,
            TimeProvider.System,
            () => qhyTelemetry);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });
        sink.Records.Clear();
        qhyTelemetry = null;

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "QHY268M"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "qhy_sensor_air_pressure",
            "qhy_sensor_humidity");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenQhySensorReaderThrows_DoesNotThrowAndClearsPreviousQhyMetrics()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        var throwReader = false;
        using var collector = new CameraTelemetryCollector(
            mediator,
            sink,
            TimeProvider.System,
            () =>
            {
                if (throwReader)
                {
                    throw new InvalidOperationException("QHY sensor read failed.");
                }

                return new QhyCameraSensorTelemetry(1012.4, 38.5);
            });

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });
        sink.Records.Clear();
        throwReader = true;

        var act = () => collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = double.NaN,
            CoolerPower = double.NaN,
            HasBattery = false,
            Battery = -1,
        });

        act.Should().NotThrow();
        sink.Records.Should().HaveCount(2);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "QHY268M"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "qhy_sensor_air_pressure",
            "qhy_sensor_humidity");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenCameraDisconnects_PublishesClearMetricsForPreviousCamera()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -8.5,
            CoolerPower = 42.25,
            HasBattery = true,
            Battery = 86,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = false,
            Name = "ASI2600MM",
        });

        sink.Records.Should().HaveCount(3);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["camera_name"], "ASI2600MM"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            "camera_sensor_temperature",
            "camera_cooler_power",
            "camera_battery_level");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenCameraNameChanges_PublishesClearMetricsForPreviousCamera()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -8.5,
            CoolerPower = 42.25,
            HasBattery = true,
            Battery = 86,
        });
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "QHY268M",
            Temperature = -7.25,
            CoolerPower = 39,
            HasBattery = true,
            Battery = 81,
        });

        sink.Records.Should().HaveCount(6);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(3)
            .And.OnlyContain(record => Equals(record.Attributes["camera_name"], "ASI2600MM"));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(3)
            .And.OnlyContain(record => Equals(record.Attributes["camera_name"], "QHY268M"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeCameraMediator();
        using var collector = new CameraTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(new CameraInfo
        {
            Connected = true,
            Name = "ASI2600MM",
            Temperature = -8.5,
            CoolerPower = 42.25,
            HasBattery = true,
            Battery = 86,
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

    private sealed class FakeCameraMediator : ICameraMediator
    {
        public List<ICameraConsumer> Consumers { get; } = [];

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;

        public CameraInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; init; }

        public bool ThrowOnRemove { get; init; }

        public bool ThrowOnAddConnected { get; init; }

        public bool ThrowOnAddDisconnected { get; init; }

        public bool AttachConnectedBeforeThrow { get; init; }

        public bool AttachDisconnectedBeforeThrow { get; init; }

        public bool ThrowOnRemoveConnected { get; init; }

        public bool ThrowOnRemoveDisconnected { get; init; }

        public bool ThrowOnGetInfo { get; init; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public bool AtTargetTemp => true;

        public double TargetTemp => -10;

        public event Func<object, EventArgs, Task>? Connected
        {
            add
            {
                AddConnectedCalls++;
                if (ThrowOnAddConnected)
                {
                    if (AttachConnectedBeforeThrow)
                    {
                        connected += value;
                    }

                    throw new InvalidOperationException("Connected subscription failed.");
                }

                connected += value;
            }

            remove
            {
                RemoveConnectedCalls++;
                if (ThrowOnRemoveConnected)
                {
                    throw new InvalidOperationException("Connected unsubscription failed.");
                }

                connected -= value;
            }
        }

        public event Func<object, EventArgs, Task>? Disconnected
        {
            add
            {
                AddDisconnectedCalls++;
                if (ThrowOnAddDisconnected)
                {
                    if (AttachDisconnectedBeforeThrow)
                    {
                        disconnected += value;
                    }

                    throw new InvalidOperationException("Disconnected subscription failed.");
                }

                disconnected += value;
            }

            remove
            {
                RemoveDisconnectedCalls++;
                if (ThrowOnRemoveDisconnected)
                {
                    throw new InvalidOperationException("Disconnected unsubscription failed.");
                }

                disconnected -= value;
            }
        }

        public event Func<object, EventArgs, Task>? DownloadTimeout
        {
            add { }
            remove { }
        }

        public void RegisterHandler(ICameraVM handler)
        {
        }

        public void RegisterConsumer(ICameraConsumer consumer)
        {
            RegisterCalls++;
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
            consumer.UpdateDeviceInfo(CurrentInfo);
        }

        public void RemoveConsumer(ICameraConsumer consumer)
        {
            RemoveCalls++;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("Removal failed.");
            }

            Consumers.Remove(consumer);
        }

        public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(Array.Empty<string>());

        public Task<bool> Connect() => Task.FromResult(true);

        public Task Disconnect() => Task.CompletedTask;

        public void Broadcast(CameraInfo deviceInfo)
        {
            CurrentInfo = deviceInfo;
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public CameraInfo GetInfo()
        {
            if (ThrowOnGetInfo)
            {
                throw new InvalidOperationException("GetInfo failed.");
            }

            return CurrentInfo;
        }

        public Task RaiseConnectedAsync() =>
            connected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync() =>
            disconnected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public string Action(string actionName, string actionParameters) => string.Empty;

        public string SendCommandString(string command, bool raw = true) => string.Empty;

        public bool SendCommandBool(string command, bool raw = true) => false;

        public void SendCommandBlind(string command, bool raw = true)
        {
        }

        public IDevice GetDevice() => throw new NotSupportedException();

        public Task Capture(
            CaptureSequence sequence,
            CancellationToken token,
            IProgress<ApplicationStatus> progress) => Task.CompletedTask;

        public IAsyncEnumerable<IExposureData> LiveView(CancellationToken token) => EmptyExposureData();

        public IAsyncEnumerable<IExposureData> LiveView(CaptureSequence sequence, CancellationToken token) =>
            EmptyExposureData();

        public Task<IExposureData> Download(CancellationToken token) => throw new NotSupportedException();

        public void AbortExposure()
        {
        }

        public void SetReadoutMode(short mode)
        {
        }

        public void SetReadoutModeForNormalImages(short mode)
        {
        }

        public void SetBinning(short x, short y)
        {
        }

        public void SetDewHeater(bool onOff)
        {
        }

        public Task<bool> CoolCamera(
            double temperature,
            TimeSpan duration,
            IProgress<ApplicationStatus> progress,
            CancellationToken ct) => Task.FromResult(true);

        public Task<bool> WarmCamera(
            TimeSpan duration,
            IProgress<ApplicationStatus> progress,
            CancellationToken ct) => Task.FromResult(true);

        public void RegisterCaptureBlock(ICameraConsumer cameraConsumer)
        {
        }

        public void ReleaseCaptureBlock(ICameraConsumer cameraConsumer)
        {
        }

        public bool IsFreeToCapture(ICameraConsumer cameraConsumer) => true;

        public void RegisterCaptureBlock(object cameraConsumer)
        {
        }

        public void ReleaseCaptureBlock(object cameraConsumer)
        {
        }

        public bool IsFreeToCapture(object cameraConsumer) => true;

        public void SetUSBLimit(int usbLimit)
        {
        }

        public void SetSubSambleRectangle(ObservableRectangle observableRectangle)
        {
        }

        private static async IAsyncEnumerable<IExposureData> EmptyExposureData()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
