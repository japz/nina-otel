using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentAssertions.Equivalency;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class WeatherTelemetryCollectorTests
{
    private static readonly string[] AllWeatherMetricNames =
    [
        "wx_cloud_cover",
        "wx_dewpoint",
        "wx_humidity",
        "wx_pressure",
        "wx_rain_rate",
        "wx_sky_brightness",
        "wx_sky_quality",
        "wx_sky_temperature",
        "wx_star_fwhm",
        "wx_temperature",
        "wx_wind_direction",
        "wx_wind_gust",
        "wx_wind_speed",
    ];

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

        var act = () => new WeatherTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_RegistersCollectorAsWeatherConsumerAndSubscribesWeatherEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

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
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialWeatherMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("AAG CloudWatcher");
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(AllWeatherMetricNames);
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.weather" &&
            record.Name == "weather_collector.registration_failed" &&
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
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Connected_PublishesWeatherConnectionLogWithCurrentConnectedDeviceName()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "AAG CloudWatcher",
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.weather" &&
            record.Name == "wx_connected" &&
            record.Body == "Weather source connected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoIsUnavailable_UsesLastConnectedWeatherDeviceName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = false,
            Name = "Ignored stale name",
        };

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "wx_connected" &&
            record.Body == "Weather source connected" &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
    }

    [Fact]
    public async Task Connected_WhenNoKnownWeatherDevice_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "wx_connected" &&
            record.Body == "Weather source connected" &&
            Equals(record.Attributes["wx_device_name"], "Unknown"));
    }

    [Fact]
    public async Task Connected_WhenWeatherDeviceNameChanges_ClearsPreviousMetricsBeforeUpdatingName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "Weather Station 2",
        };

        await proxy.RaiseConnectedAsync();
        collector.UpdateDeviceInfo(ConnectedInfo("Weather Station 2"));

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "wx_temperature" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.weather" &&
            record.Name == "wx_disconnected" &&
            record.Body == "Weather source disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(13)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));

        sink.Records.Clear();
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "Weather Station 2",
        };
        await proxy.RaiseConnectedAsync();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "wx_disconnected" &&
            Equals(record.Attributes["wx_device_name"], "Weather Station 2"));
    }

    [Fact]
    public async Task Disconnected_WhenDeviceInfoAlreadyClearedMetrics_StillUsesPreviousDeviceName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        collector.UpdateDeviceInfo(new WeatherDataInfo
        {
            Connected = false,
            Name = "AAG CloudWatcher",
        });
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "wx_disconnected" &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownWeatherDevice_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "wx_disconnected" &&
            record.Body == "Weather source disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["wx_device_name"], "Unknown"));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

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
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.weather" &&
            record.Name == "weather_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Disconnected subscription failed."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFailsAfterInitialMetrics_ClearsThoseMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.CurrentInfo = ConnectedInfo("AAG CloudWatcher");
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().Contain(record =>
                record.Name == "wx_temperature" &&
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionFails_LateMediatorEventsDoNotPublishLifecycleLogs()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "AAG CloudWatcher",
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenRollbackRemoveConsumerFails_LateDeviceUpdatesDoNotPublishMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddDisconnected = true;
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        proxy.Broadcast(ConnectedInfo("AAG CloudWatcher"));

        sink.Records.Should().BeEmpty();
        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public async Task Start_WhenEventAccessorAttachesThenThrows_RollbackUnsubscribesLateLifecycleCallback()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddConnected = true;
        proxy.AttachConnectedBeforeThrow = true;
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "AAG CloudWatcher",
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().BeEmpty();
        proxy.ConnectedSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_WhenRollbackUnsubscribeFailedAfterPartialAttach_RetriesUnsubscribe()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddConnected = true;
        proxy.AttachConnectedBeforeThrow = true;
        proxy.ThrowOnRemoveConnected = true;
        var sink = new RecordingTelemetrySink();
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        proxy.RemoveConnectedCalls.Should().Be(2);
    }

    [Fact]
    public void Start_WhenRegistrationFailsAndSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        using var collector = new WeatherTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediatorOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
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
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

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
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnected_PublishesAllFiniteWeatherMetricsWithOneBatchTimestamp()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());

        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));

        sink.Records.Should().HaveCount(13);
        var batchTimestamp = sink.Records[0].Timestamp;
        sink.Records.Should().OnlyContain(record => record.Timestamp == batchTimestamp);
        sink.Records.Should().OnlyContain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.weather" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_cloud_cover", 11.25), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_dewpoint", 2.5), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_humidity", 77.75), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_pressure", 1008.4), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_rain_rate", 0.12), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_sky_brightness", 18.9), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_sky_quality", 20.7), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_sky_temperature", -21.4), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_star_fwhm", 3.8), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_temperature", 5.6), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_wind_direction", 245.5), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_wind_gust", 8.2), ExcludingTimestamp);
        sink.Records.Should().ContainEquivalentOf(ExpectedMetric("wx_wind_speed", 4.1), ExcludingTimestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UpdateDeviceInfo_WhenWeatherDeviceNameIsBlankOrNull_UsesUnknownAttribute(string? deviceName)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(deviceName));

        sink.Records.Should().OnlyContain(record => Equals(record.Attributes["wx_device_name"], "Unknown"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenAllValuesAreNonFiniteBeforeAnySample_PublishesNothing()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfoWithNonFiniteValues("AAG CloudWatcher"));

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSomeValuesAreNonFiniteBeforeAnySample_PublishesOnlyFiniteMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
        var info = ConnectedInfo("AAG CloudWatcher");
        info.CloudCover = double.NaN;
        info.DewPoint = double.PositiveInfinity;
        info.Humidity = double.NegativeInfinity;

        collector.UpdateDeviceInfo(info);

        sink.Records.Should().HaveCount(10);
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(
            AllWeatherMetricNames.Except(
            [
                "wx_cloud_cover",
                "wx_dewpoint",
                "wx_humidity",
            ]));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenPreviouslyPublishedValuesBecomeNonFinite_ClearsOnlyStaleMetricsAndMarksThemUnpublished()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();

        var staleInfo = ConnectedInfo("AAG CloudWatcher");
        staleInfo.CloudCover = double.NaN;
        staleInfo.DewPoint = double.PositiveInfinity;
        staleInfo.Humidity = double.NegativeInfinity;

        collector.UpdateDeviceInfo(staleInfo);

        sink.Records.Should().HaveCount(13);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(3)
            .And.OnlyContain(record =>
                Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher") &&
                new[] { "wx_cloud_cover", "wx_dewpoint", "wx_humidity" }.Contains(record.Name));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(10)
            .And.OnlyContain(record =>
                Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher") &&
                !new[] { "wx_cloud_cover", "wx_dewpoint", "wx_humidity" }.Contains(record.Name));

        sink.Records.Clear();
        collector.UpdateDeviceInfo(staleInfo);

        sink.Records.Should().HaveCount(10);
        sink.Records.Should().OnlyContain(static record => !double.IsNaN(record.NumericValue!.Value));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenWeatherDeviceDisconnects_ClearsPreviousMetricsAndResetsState()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new WeatherDataInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().HaveCount(13);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(AllWeatherMetricNames);

        sink.Records.Clear();
        collector.UpdateDeviceInfo(new WeatherDataInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenWeatherDeviceNameChanges_ClearsOldMetricsBeforePublishingNewMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("Weather Station 2"));

        sink.Records.Should().HaveCount(26);
        sink.Records.Take(13)
            .Should().OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["wx_device_name"], "AAG CloudWatcher"));
        sink.Records.Take(13).Select(static record => record.Name).Should().BeEquivalentTo(AllWeatherMetricNames);
        sink.Records.Skip(13)
            .Should().OnlyContain(record =>
                !double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["wx_device_name"], "Weather Station 2"));
        sink.Records.Skip(13).Select(static record => record.Name).Should().BeEquivalentTo(AllWeatherMetricNames);
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new WeatherTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Callbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "AAG CloudWatcher",
        };
        using var collector = new WeatherTelemetryCollector(
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
    public async Task Dispose_WhenEventUnsubscriptionFails_DoesNotThrowAndLateEventsDoNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemoveConnected = true;
        proxy.ThrowOnRemoveDisconnected = true;
        proxy.CurrentInfo = new WeatherDataInfo
        {
            Connected = true,
            Name = "AAG CloudWatcher",
        };
        var sink = new RecordingTelemetrySink();
        var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);
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
    public void Collector_DoesNotCallWeatherControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new WeatherTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("AAG CloudWatcher"));
        collector.Dispose();

        proxy.ForbiddenCalls.Should().BeEmpty();
    }

    private static TelemetryRecord ExpectedMetric(string name, double value) =>
        TelemetryRecord.Metric(
            default,
            "nina.weather",
            name,
            value,
            TelemetryPriority.Normal,
            new Dictionary<string, object?> { ["wx_device_name"] = "AAG CloudWatcher" });

    private static EquivalencyAssertionOptions<TelemetryRecord> ExcludingTimestamp(
        EquivalencyAssertionOptions<TelemetryRecord> options) =>
        options.Excluding(record => record.Timestamp);

    private static WeatherDataInfo ConnectedInfo(string? deviceName) =>
        new()
        {
            Connected = true,
            Name = deviceName!,
            CloudCover = 11.25,
            DewPoint = 2.5,
            Humidity = 77.75,
            Pressure = 1008.4,
            RainRate = 0.12,
            SkyBrightness = 18.9,
            SkyQuality = 20.7,
            SkyTemperature = -21.4,
            StarFWHM = 3.8,
            Temperature = 5.6,
            WindDirection = 245.5,
            WindGust = 8.2,
            WindSpeed = 4.1,
        };

    private static WeatherDataInfo ConnectedInfoWithNonFiniteValues(string deviceName) =>
        new()
        {
            Connected = true,
            Name = deviceName,
            CloudCover = double.NaN,
            DewPoint = double.PositiveInfinity,
            Humidity = double.NegativeInfinity,
            Pressure = double.NaN,
            RainRate = double.PositiveInfinity,
            SkyBrightness = double.NegativeInfinity,
            SkyQuality = double.NaN,
            SkyTemperature = double.PositiveInfinity,
            StarFWHM = double.NegativeInfinity,
            Temperature = double.NaN,
            WindDirection = double.PositiveInfinity,
            WindGust = double.NegativeInfinity,
            WindSpeed = double.NaN,
        };

    private static PassiveWeatherMediatorProxy CreateMediator(out IWeatherDataMediator mediator)
    {
        mediator = DispatchProxy.Create<IWeatherDataMediator, PassiveWeatherMediatorProxy>();
        return (PassiveWeatherMediatorProxy)(object)mediator;
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

    public class PassiveWeatherMediatorProxy : DispatchProxy
    {
        private static readonly HashSet<string> ForbiddenMethods =
        [
            "Connect",
            "Disconnect",
            "Rescan",
            "GetDevice",
            "GetDeviceInfo",
            "Action",
            "SendCommandString",
            "SendCommandBool",
            "SendCommandBlind",
        ];

        public List<IWeatherDataConsumer> Consumers { get; } = [];

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;

        public WeatherDataInfo CurrentInfo { get; set; } = new();

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

        public void Broadcast(WeatherDataInfo deviceInfo)
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
                return nameof(PassiveWeatherMediatorProxy);
            }

            if (ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Weather telemetry must not call {methodName}.");
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

            var consumer = args?.Length > 0 ? args[0] as IWeatherDataConsumer : null;
            consumer.Should().NotBeNull("the collector should register itself as an IWeatherDataConsumer");
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

            var consumer = args?.Length > 0 ? args[0] as IWeatherDataConsumer : null;
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

        private WeatherDataInfo GetInfo()
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
