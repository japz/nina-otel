using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class GuiderTelemetryCollectorTests
{
    private static readonly string[] AllGuiderMetricNames =
    [
        "guider_rms_ra_arcsec",
        "guider_rms_dec_arcsec",
        "guider_rms_arcsec",
        "guider_rms_ra_pixel",
        "guider_rms_dec_pixel",
        "guider_rms_pixel",
        "guider_rms_peak_ra_arcsec",
        "guider_rms_peak_dec_arcsec",
        "guider_rms_peak_arcsec",
        "guider_rms_peak_ra_pixel",
        "guider_rms_peak_dec_pixel",
        "guider_rms_peak_pixel",
        "guider_ra_distance",
        "guider_ra_duration",
        "guider_dec_distance",
        "guider_dec_duration",
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

        var act = () => new GuiderTelemetryCollector(
            nullDependency == "mediator" ? null! : mediator,
            nullDependency == "sink" ? null! : sink,
            nullDependency == "timeProvider" ? null! : timeProvider);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(nullDependency);
    }

    [Fact]
    public void Start_RegistersConsumerAndSubscribesGuiderEventsOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        proxy.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddConnectedCalls.Should().Be(1);
        proxy.AddDisconnectedCalls.Should().Be(1);
        proxy.AddGuideEventCalls.Should().Be(1);
        proxy.AddGuidingStartedCalls.Should().Be(1);
        proxy.AddGuidingStoppedCalls.Should().Be(1);
        proxy.AddAfterDitherCalls.Should().Be(1);
        proxy.GuideEventSubscriberCount.Should().Be(1);
        proxy.TotalLifecycleSubscriberCount.Should().Be(5);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_StoresInfoButDoesNotPublishUntilGuideEvent()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PHD2");
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, new IncrementingTimeProvider());

        collector.Start();

        sink.Records.Should().BeEmpty();

        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(AllGuiderMetricNames);
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrowOrRetryOrRemoveAgain()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        var sink = new RecordingTelemetrySink();
        var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () =>
        {
            collector.Start();
            collector.Start();
            collector.Dispose();
        };

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.AddGuideEventCalls.Should().Be(0);
        proxy.RemoveCalls.Should().Be(1);
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.guider" &&
            record.Name == "guider_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Registration failed."));
    }

    [Fact]
    public void Start_WhenMediatorAddsConsumerThenRegistrationThrows_RollsBackConsumer()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.AttachConsumerBeforeRegisterThrow = true;
        var sink = new RecordingTelemetrySink();
        var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "guider_collector.registration_failed" &&
            Equals(record.Attributes["error_message"], "Registration failed after adding consumer."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthRollsBackAndDoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddGuidingStopped = true;
        proxy.AttachGuidingStoppedBeforeAddThrow = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        proxy.RegisterCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.AddGuideEventCalls.Should().Be(1);
        proxy.RemoveGuideEventCalls.Should().Be(1);
        proxy.RemoveGuidingStartedCalls.Should().Be(1);
        proxy.RemoveGuidingStoppedCalls.Should().Be(1);
        proxy.RemoveAfterDitherCalls.Should().Be(0);
        proxy.GuideEventSubscriberCount.Should().Be(0);
        proxy.TotalLifecycleSubscriberCount.Should().Be(0);
        proxy.Consumers.Should().BeEmpty();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Name == "guider_collector.registration_failed" &&
            Equals(record.Attributes["error_message"], "Guiding stopped subscription failed."));
    }

    [Fact]
    public void Start_WhenRegistrationFailsAndSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRegister = true;
        using var collector = new GuiderTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Connected_PublishesGuiderConnectedLogWithCurrentConnectedGuiderName()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PHD2");
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.guider" &&
            record.Name == "guider_connected" &&
            record.Body == "Guider connected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["guider_name"], "PHD2"));
    }

    [Fact]
    public async Task Connected_WhenNoKnownGuider_UsesUnknownName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "guider_connected" &&
            Equals(record.Attributes["guider_name"], "Unknown"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();
        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.guider" &&
            record.Name == "guider_disconnected" &&
            record.Body == "Guider disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(16)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["guider_name"], "PHD2"));

        sink.Records.Clear();
        proxy.CurrentInfo = ConnectedInfo("MetaGuide");
        await proxy.RaiseConnectedAsync();
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "guider_disconnected" &&
            Equals(record.Attributes["guider_name"], "MetaGuide"));
    }

    [Fact]
    public async Task Disconnected_WhenDeviceInfoAlreadyClearedMetrics_StillUsesPreviousGuiderName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        collector.UpdateDeviceInfo(new GuiderInfo
        {
            Connected = false,
            Name = "PHD2",
        });
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "guider_disconnected" &&
            Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnected_AfterNameChangeBeforeNextGuideStep_LogsCurrentGuiderAndClearsOldMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        collector.UpdateDeviceInfo(ConnectedInfo("MetaGuide"));
        sink.Records.Clear();

        await proxy.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "guider_disconnected" &&
            Equals(record.Attributes["guider_name"], "MetaGuide"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(16)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["guider_name"], "PHD2"));
    }

    [Theory]
    [InlineData("dither", "guider_dither", "Dither")]
    [InlineData("started", "guider_guiding_started", "Guiding started")]
    [InlineData("stopped", "guider_guiding_stopped", "Guiding stopped")]
    public async Task GuiderLifecycleEvent_PublishesLog(
        string eventName,
        string expectedRecordName,
        string expectedBody)
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PHD2");
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseLifecycleEventAsync(eventName);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.guider" &&
            record.Name == expectedRecordName &&
            record.Body == expectedBody &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["guider_name"], "PHD2"));
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionFails_LateMediatorEventsDoNotPublishLifecycleLogsOrMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnAddGuidingStopped = true;
        proxy.AttachGuidingStoppedBeforeAddThrow = true;
        proxy.CurrentInfo = ConnectedInfo("PHD2");
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await proxy.RaiseConnectedAsync();
        proxy.RaiseGuideEvent(GuideStep());
        await proxy.RaiseLifecycleEventAsync("started");
        await proxy.RaiseLifecycleEventAsync("stopped");

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesGuideEventAndRemovesConsumerOnce()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        proxy.Consumers.Should().BeEmpty();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveGuideEventCalls.Should().Be(1);
        proxy.RemoveGuidingStartedCalls.Should().Be(1);
        proxy.RemoveGuidingStoppedCalls.Should().Be(1);
        proxy.RemoveAfterDitherCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);
        proxy.GuideEventSubscriberCount.Should().Be(0);
        proxy.TotalLifecycleSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_BeforeSuccessfulRegistration_DoesNotRemoveConsumerOrUnsubscribeGuideEvent()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Dispose();
        collector.Dispose();

        proxy.RemoveGuideEventCalls.Should().Be(0);
        proxy.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_WhenMediatorTeardownFails_DoesNotThrowStillAttemptsRemovalAndLateEventsDoNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.ThrowOnRemoveConnected = true;
        proxy.ThrowOnRemoveDisconnected = true;
        proxy.ThrowOnRemoveGuideEvent = true;
        proxy.ThrowOnRemoveGuidingStarted = true;
        proxy.ThrowOnRemoveGuidingStopped = true;
        proxy.ThrowOnRemoveAfterDither = true;
        proxy.ThrowOnRemove = true;
        var sink = new RecordingTelemetrySink();
        var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        proxy.RemoveConnectedCalls.Should().Be(1);
        proxy.RemoveDisconnectedCalls.Should().Be(1);
        proxy.RemoveGuideEventCalls.Should().Be(1);
        proxy.RemoveGuidingStartedCalls.Should().Be(1);
        proxy.RemoveGuidingStoppedCalls.Should().Be(1);
        proxy.RemoveAfterDitherCalls.Should().Be(1);
        proxy.RemoveCalls.Should().Be(1);

        await proxy.RaiseConnectedAsync();
        proxy.RaiseGuideEvent(GuideStep());
        await proxy.RaiseLifecycleEventAsync("started");

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void GuideEvent_WhenNoCurrentInfoOrDisconnected_DoesNotPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        proxy.RaiseGuideEvent(GuideStep());
        collector.UpdateDeviceInfo(new GuiderInfo
        {
            Connected = false,
            Name = "PHD2",
        });
        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void GuideEvent_WhenConnected_PublishesAllFiniteMetricsWithOneBatchTimestampAndGuiderName()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));

        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Should().HaveCount(16);
        var batchTimestamp = sink.Records[0].Timestamp;
        sink.Records.Should().OnlyContain(record =>
            record.Timestamp == batchTimestamp &&
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.guider" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(AllGuiderMetricNames);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_ra_arcsec" && record.NumericValue == 0.55);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_dec_arcsec" && record.NumericValue == 1.1);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_arcsec" && record.NumericValue == 2.75);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_ra_pixel" && record.NumericValue == 1.1);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_dec_pixel" && record.NumericValue == 2.2);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_pixel" && record.NumericValue == 5.5);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_peak_ra_arcsec" && record.NumericValue == 1.65);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_peak_dec_arcsec" && record.NumericValue == 2.2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "guider_rms_peak_arcsec" &&
            record.NumericValue!.Value.ShouldBeApproximately(Math.Sqrt(Math.Pow(1.65, 2) + Math.Pow(2.2, 2))));
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_peak_ra_pixel" && record.NumericValue == 3.3);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_peak_dec_pixel" && record.NumericValue == 4.4);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "guider_rms_peak_pixel" &&
            record.NumericValue!.Value.ShouldBeApproximately(Math.Sqrt(Math.Pow(3.3, 2) + Math.Pow(4.4, 2))));
        sink.Records.Should().ContainSingle(record => record.Name == "guider_ra_distance" && record.NumericValue == 6.1);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_ra_duration" && record.NumericValue == 7.2);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_dec_distance" && record.NumericValue == 8.3);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_dec_duration" && record.NumericValue == 9.4);
    }

    [Fact]
    public void GuideEvent_GuiderDecDistanceUsesDecDistanceRawNotRaDuration()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));

        proxy.RaiseGuideEvent(new FakeGuideStep
        {
            RADistanceRaw = 1,
            RADurationValue = 222,
            DECDistanceRaw = 333,
            DECDurationValue = 4,
        });

        sink.Records.Should().ContainSingle(record =>
            record.Name == "guider_dec_distance" &&
            record.NumericValue == 333);
        sink.Records.Should().NotContain(record =>
            record.Name == "guider_dec_distance" &&
            record.NumericValue == 222);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void GuideEvent_WhenGuiderNameIsBlankOrNull_UsesUnknownAttribute(string? guiderName)
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo(guiderName));

        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Should().OnlyContain(record => Equals(record.Attributes["guider_name"], "Unknown"));
    }

    [Fact]
    public void GuideEvent_WhenValuesAreNonFinite_SkipsAndClearsPreviouslyPublishedMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        var info = ConnectedInfo("PHD2");
        collector.UpdateDeviceInfo(info);
        proxy.RaiseGuideEvent(GuideStep());
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new GuiderInfo
        {
            Connected = true,
            Name = "PHD2",
            RMSError = new RMSError(
                double.NaN,
                2.2,
                double.NegativeInfinity,
                4.4,
                5.5,
                0.5),
        });
        proxy.RaiseGuideEvent(new FakeGuideStep
        {
            RADistanceRaw = double.NaN,
            RADurationValue = double.PositiveInfinity,
            DECDistanceRaw = 8.3,
            DECDurationValue = 9.4,
        });

        sink.Records.Should().HaveCount(16);
        sink.Records.Where(static record => double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(8)
            .And.OnlyContain(record =>
                new[]
                {
                    "guider_rms_ra_arcsec",
                    "guider_rms_ra_pixel",
                    "guider_rms_peak_ra_arcsec",
                    "guider_rms_peak_arcsec",
                    "guider_rms_peak_ra_pixel",
                    "guider_rms_peak_pixel",
                    "guider_ra_distance",
                    "guider_ra_duration",
                }.Contains(record.Name));
        sink.Records.Where(static record => !double.IsNaN(record.NumericValue!.Value))
            .Should().HaveCount(8)
            .And.OnlyContain(record =>
                new[]
                {
                    "guider_rms_arcsec",
                    "guider_rms_dec_arcsec",
                    "guider_rms_dec_pixel",
                    "guider_rms_pixel",
                    "guider_rms_peak_dec_arcsec",
                    "guider_rms_peak_dec_pixel",
                    "guider_dec_distance",
                    "guider_dec_duration",
                }.Contains(record.Name));

        sink.Records.Clear();
        proxy.RaiseGuideEvent(new FakeGuideStep
        {
            RADistanceRaw = double.NaN,
            RADurationValue = double.PositiveInfinity,
            DECDistanceRaw = 8.3,
            DECDurationValue = 9.4,
        });

        sink.Records.Should().HaveCount(8);
        sink.Records.Should().OnlyContain(static record => !double.IsNaN(record.NumericValue!.Value));
    }

    [Fact]
    public void GuideEvent_WhenPriorDeviceInfoIsMutatedAfterUpdate_UsesStoredSnapshot()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        var info = ConnectedInfo("PHD2");

        collector.UpdateDeviceInfo(info);
        info.Connected = false;
        info.Name = "Mutated";
        info.RMSError = new RMSError(9, 9, 9, 9, 9, 1);

        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Should().HaveCount(16);
        sink.Records.Should().OnlyContain(record => Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_ra_arcsec" && record.NumericValue == 0.55);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_dec_arcsec" && record.NumericValue == 1.1);
        sink.Records.Should().ContainSingle(record => record.Name == "guider_rms_arcsec" && record.NumericValue == 2.75);
        sink.Records.Should().NotContain(record => record.NumericValue == 9);
    }

    [Fact]
    public void GuideEvent_WhenAllValuesAreNonFiniteBeforeAnySample_PublishesNothing()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(new GuiderInfo
        {
            Connected = true,
            Name = "PHD2",
            RMSError = new RMSError(
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                double.NaN,
                double.PositiveInfinity,
                1),
        });

        proxy.RaiseGuideEvent(new FakeGuideStep
        {
            RADistanceRaw = double.NaN,
            RADurationValue = double.PositiveInfinity,
            DECDistanceRaw = double.NegativeInfinity,
            DECDurationValue = double.NaN,
        });

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenGuiderDisconnects_ClearsPreviousMetricsForPreviousGuider()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new GuiderInfo
        {
            Connected = false,
            Name = "Ignored",
        });

        sink.Records.Should().HaveCount(16);
        sink.Records.Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Select(static record => record.Name).Should().BeEquivalentTo(AllGuiderMetricNames);

        sink.Records.Clear();
        proxy.RaiseGuideEvent(GuideStep());
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenGuiderNameChanges_ClearsOldMetricsBeforePublishingNewMetrics()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(
            mediator,
            sink,
            new IncrementingTimeProvider());
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("MetaGuide"));

        sink.Records.Should().BeEmpty();

        proxy.RaiseGuideEvent(GuideStep());

        sink.Records.Should().HaveCount(32);
        var batchTimestamp = sink.Records[0].Timestamp;
        sink.Records.Should().OnlyContain(record => record.Timestamp == batchTimestamp);
        sink.Records.Take(16).Should().OnlyContain(record =>
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["guider_name"], "PHD2"));
        sink.Records.Take(16).Select(static record => record.Name).Should().BeEquivalentTo(AllGuiderMetricNames);
        sink.Records.Skip(16).Should().OnlyContain(record =>
            !double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["guider_name"], "MetaGuide"));
        sink.Records.Skip(16).Select(static record => record.Name).Should().BeEquivalentTo(AllGuiderMetricNames);
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void GuideEvent_WhenSinkThrows_DoesNotThrow()
    {
        var proxy = CreateMediator(out var mediator);
        using var collector = new GuiderTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));

        var act = () => proxy.RaiseGuideEvent(GuideStep());

        act.Should().NotThrow();
    }

    [Fact]
    public async Task LifecycleCallbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var proxy = CreateMediator(out var mediator);
        proxy.CurrentInfo = ConnectedInfo("PHD2");
        using var collector = new GuiderTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var connectedAct = async () => await proxy.RaiseConnectedAsync();
        var disconnectedAct = async () => await proxy.RaiseDisconnectedAsync();
        var ditherAct = async () => await proxy.RaiseLifecycleEventAsync("dither");
        var startedAct = async () => await proxy.RaiseLifecycleEventAsync("started");
        var stoppedAct = async () => await proxy.RaiseLifecycleEventAsync("stopped");

        await connectedAct.Should().NotThrowAsync();
        await disconnectedAct.Should().NotThrowAsync();
        await ditherAct.Should().NotThrowAsync();
        await startedAct.Should().NotThrowAsync();
        await stoppedAct.Should().NotThrowAsync();
    }

    [Fact]
    public void Collector_DoesNotCallGuiderCommandOrControlApis()
    {
        var proxy = CreateMediator(out var mediator);
        var sink = new RecordingTelemetrySink();
        using var collector = new GuiderTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.UpdateDeviceInfo(ConnectedInfo("PHD2"));
        proxy.RaiseGuideEvent(GuideStep());
        collector.Dispose();

        proxy.ForbiddenCalls.Should().BeEmpty();
    }

    private static GuiderInfo ConnectedInfo(string? guiderName) =>
        new()
        {
            Connected = true,
            Name = guiderName!,
            RMSError = new RMSError(1.1, 2.2, 3.3, 4.4, 5.5, 0.5),
            PixelScale = 0.5,
        };

    private static IGuideStep GuideStep() =>
        new FakeGuideStep
        {
            RADistanceRaw = 6.1,
            RADurationValue = 7.2,
            DECDistanceRaw = 8.3,
            DECDurationValue = 9.4,
        };

    private static PassiveGuiderMediatorProxy CreateMediator(out IGuiderMediator mediator)
    {
        mediator = DispatchProxy.Create<IGuiderMediator, PassiveGuiderMediatorProxy>();
        return (PassiveGuiderMediatorProxy)(object)mediator;
    }

    private sealed class FakeGuideStep : IGuideStep
    {
        public string Event => "GuideStep";

        public string TimeStamp => "2026-06-18T12:30:00Z";

        public string Host => "localhost";

        public int Inst => 1;

        public double Frame => 42;

        public double Time => 123.4;

        public double RADistanceRaw { get; set; }

        public double DECDistanceRaw { get; set; }

        public double RADuration => RADurationValue;

        public double DECDuration => DECDurationValue;

        public double RADurationValue { get; init; }

        public double DECDurationValue { get; init; }

        public IGuideStep Clone() => this;
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

    public class PassiveGuiderMediatorProxy : DispatchProxy
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
            "Dither",
            "StartRMSRecording",
            "GetRMSRecording",
            "StopRMSRecording",
            "StartGuiding",
            "StopGuiding",
            "AutoSelectGuideStar",
            "ClearCalibration",
            "SetShiftRate",
            "StopShifting",
            "GetLockPosition",
        ];

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private EventHandler<IGuideStep>? guideEvent;
        private Func<object, EventArgs, Task>? afterDither;
        private Func<object, EventArgs, Task>? guidingStarted;
        private Func<object, EventArgs, Task>? guidingStopped;

        public List<IGuiderConsumer> Consumers { get; } = [];

        public GuiderInfo CurrentInfo { get; set; } = new();

        public bool ThrowOnRegister { get; set; }

        public bool AttachConsumerBeforeRegisterThrow { get; set; }

        public bool ThrowOnRemove { get; set; }

        public bool ThrowOnAddConnected { get; set; }

        public bool ThrowOnAddDisconnected { get; set; }

        public bool ThrowOnAddGuideEvent { get; set; }

        public bool ThrowOnAddGuidingStarted { get; set; }

        public bool ThrowOnAddGuidingStopped { get; set; }

        public bool AttachGuidingStoppedBeforeAddThrow { get; set; }

        public bool ThrowOnAddAfterDither { get; set; }

        public bool ThrowOnRemoveConnected { get; set; }

        public bool ThrowOnRemoveDisconnected { get; set; }

        public bool ThrowOnRemoveGuideEvent { get; set; }

        public bool ThrowOnRemoveGuidingStarted { get; set; }

        public bool ThrowOnRemoveGuidingStopped { get; set; }

        public bool ThrowOnRemoveAfterDither { get; set; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int AddConnectedCalls { get; private set; }

        public int AddDisconnectedCalls { get; private set; }

        public int AddGuideEventCalls { get; private set; }

        public int AddGuidingStartedCalls { get; private set; }

        public int AddGuidingStoppedCalls { get; private set; }

        public int AddAfterDitherCalls { get; private set; }

        public int RemoveConnectedCalls { get; private set; }

        public int RemoveDisconnectedCalls { get; private set; }

        public int RemoveGuideEventCalls { get; private set; }

        public int RemoveGuidingStartedCalls { get; private set; }

        public int RemoveGuidingStoppedCalls { get; private set; }

        public int RemoveAfterDitherCalls { get; private set; }

        public int GuideEventSubscriberCount => guideEvent?.GetInvocationList().Length ?? 0;

        public int TotalLifecycleSubscriberCount =>
            (connected?.GetInvocationList().Length ?? 0) +
            (disconnected?.GetInvocationList().Length ?? 0) +
            (afterDither?.GetInvocationList().Length ?? 0) +
            (guidingStarted?.GetInvocationList().Length ?? 0) +
            (guidingStopped?.GetInvocationList().Length ?? 0);

        public List<string> ForbiddenCalls { get; } = [];

        public Task RaiseConnectedAsync() =>
            connected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync() =>
            disconnected?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask;

        public void RaiseGuideEvent(IGuideStep guideStep) =>
            guideEvent?.Invoke(this, guideStep);

        public Task RaiseLifecycleEventAsync(string eventName) =>
            eventName switch
            {
                "dither" => afterDither?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                "started" => guidingStarted?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                "stopped" => guidingStopped?.Invoke(this, EventArgs.Empty) ?? Task.CompletedTask,
                _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null),
            };

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
                return nameof(PassiveGuiderMediatorProxy);
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

            if (methodName is "add_GuideEvent")
            {
                return AddGuideEvent(args);
            }

            if (methodName is "remove_GuideEvent")
            {
                return RemoveGuideEvent(args);
            }

            if (methodName is "add_GuidingStarted")
            {
                return AddGuidingStarted(args);
            }

            if (methodName is "remove_GuidingStarted")
            {
                return RemoveGuidingStarted(args);
            }

            if (methodName is "add_GuidingStopped")
            {
                return AddGuidingStopped(args);
            }

            if (methodName is "remove_GuidingStopped")
            {
                return RemoveGuidingStopped(args);
            }

            if (methodName is "add_AfterDither")
            {
                return AddAfterDither(args);
            }

            if (methodName is "remove_AfterDither")
            {
                return RemoveAfterDither(args);
            }

            if (ForbiddenMethods.Contains(methodName))
            {
                ForbiddenCalls.Add(methodName);
                throw new NotSupportedException($"Guider telemetry must not call {methodName}.");
            }

            return methodName switch
            {
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

            var consumer = args?.Length > 0 ? args[0] as IGuiderConsumer : null;
            consumer.Should().NotBeNull("the collector should register itself as an IGuiderConsumer");
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

            var consumer = args?.Length > 0 ? args[0] as IGuiderConsumer : null;
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

        private object? AddGuideEvent(object?[]? args)
        {
            AddGuideEventCalls++;
            if (ThrowOnAddGuideEvent)
            {
                throw new InvalidOperationException("Guide event subscription failed.");
            }

            guideEvent += args?.Length > 0 ? args[0] as EventHandler<IGuideStep> : null;
            return null;
        }

        private object? RemoveGuideEvent(object?[]? args)
        {
            RemoveGuideEventCalls++;
            if (ThrowOnRemoveGuideEvent)
            {
                throw new InvalidOperationException("Guide event unsubscription failed.");
            }

            guideEvent -= args?.Length > 0 ? args[0] as EventHandler<IGuideStep> : null;
            return null;
        }

        private object? AddGuidingStarted(object?[]? args)
        {
            AddGuidingStartedCalls++;
            if (ThrowOnAddGuidingStarted)
            {
                throw new InvalidOperationException("Guiding started subscription failed.");
            }

            guidingStarted += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveGuidingStarted(object?[]? args)
        {
            RemoveGuidingStartedCalls++;
            if (ThrowOnRemoveGuidingStarted)
            {
                throw new InvalidOperationException("Guiding started unsubscription failed.");
            }

            guidingStarted -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddGuidingStopped(object?[]? args)
        {
            AddGuidingStoppedCalls++;
            if (AttachGuidingStoppedBeforeAddThrow)
            {
                guidingStopped += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            }

            if (ThrowOnAddGuidingStopped)
            {
                throw new InvalidOperationException("Guiding stopped subscription failed.");
            }

            if (!AttachGuidingStoppedBeforeAddThrow)
            {
                guidingStopped += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            }

            return null;
        }

        private object? RemoveGuidingStopped(object?[]? args)
        {
            RemoveGuidingStoppedCalls++;
            if (ThrowOnRemoveGuidingStopped)
            {
                throw new InvalidOperationException("Guiding stopped unsubscription failed.");
            }

            guidingStopped -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? AddAfterDither(object?[]? args)
        {
            AddAfterDitherCalls++;
            if (ThrowOnAddAfterDither)
            {
                throw new InvalidOperationException("After dither subscription failed.");
            }

            afterDither += args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
            return null;
        }

        private object? RemoveAfterDither(object?[]? args)
        {
            RemoveAfterDitherCalls++;
            if (ThrowOnRemoveAfterDither)
            {
                throw new InvalidOperationException("After dither unsubscription failed.");
            }

            afterDither -= args?.Length > 0 ? args[0] as Func<object, EventArgs, Task> : null;
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

internal static class DoubleAssertionExtensions
{
    public static bool ShouldBeApproximately(this double actual, double expected) =>
        Math.Abs(actual - expected) < 0.000000001;
}
