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
    public void Start_RegistersCollectorAsCameraConsumer()
    {
        var mediator = new FakeCameraMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new CameraTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
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
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.camera" &&
            record.Name == "camera_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)));
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

        public CameraInfo CurrentInfo { get; init; } = new();

        public bool ThrowOnRegister { get; init; }

        public bool AtTargetTemp => true;

        public double TargetTemp => -10;

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
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
            consumer.UpdateDeviceInfo(CurrentInfo);
        }

        public void RemoveConsumer(ICameraConsumer consumer) => Consumers.Remove(consumer);

        public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(Array.Empty<string>());

        public Task<bool> Connect() => Task.FromResult(true);

        public Task Disconnect() => Task.CompletedTask;

        public void Broadcast(CameraInfo deviceInfo)
        {
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public CameraInfo GetInfo() => CurrentInfo;

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
