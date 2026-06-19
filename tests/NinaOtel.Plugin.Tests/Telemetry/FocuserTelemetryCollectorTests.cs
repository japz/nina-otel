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
    public void Start_RegistersCollectorAsFocuserConsumerAndSubscribesFocuserEventsOnce()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

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
            record.Source == "nina.focuser" &&
            record.Name == "focuser_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task Connected_PublishesFocuserConnectionLogWithCurrentConnectedFocuserName()
    {
        var mediator = new FakeFocuserMediator
        {
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.focuser" &&
            record.Name == "focuser_connected" &&
            record.Body == "Focuser connected" &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["focuser_name"], "EAF"));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoIsUnavailable_UsesLastConnectedFocuserName()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();
        mediator.CurrentInfo = new FocuserInfo
        {
            Connected = false,
            Name = "Ignored stale name",
        };

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "focuser_connected" &&
            record.Body == "Focuser connected" &&
            Equals(record.Attributes["focuser_name"], "EAF"));
    }

    [Fact]
    public async Task Connected_WhenNoKnownFocuser_UsesUnknownName()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "focuser_connected" &&
            record.Body == "Focuser connected" &&
            Equals(record.Attributes["focuser_name"], "Unknown"));
    }

    [Fact]
    public async Task Connected_WhenFocuserNameChanges_ClearsPreviousFocuserMetricsBeforeUpdatingName()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();
        mediator.CurrentInfo = new FocuserInfo
        {
            Connected = true,
            Name = "Moonlite",
        };

        await mediator.RaiseConnectedAsync();
        mediator.Broadcast(new FocuserInfo
        {
            Connected = true,
            Name = "Moonlite",
            Position = 2048,
            Temperature = 2.5,
        });

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "focuser_position" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["focuser_name"], "EAF"));
        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "focuser_temperature" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["focuser_name"], "EAF"));
    }

    [Fact]
    public async Task Disconnected_PublishesDisconnectLogClearsMetricsAndSuppressesDuplicateUntilConnectedAgain()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.focuser" &&
            record.Name == "focuser_disconnected" &&
            record.Body == "Focuser disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["focuser_name"], "EAF"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(2)
            .And.OnlyContain(record =>
                double.IsNaN(record.NumericValue!.Value) &&
                Equals(record.Attributes["focuser_name"], "EAF"));

        sink.Records.Clear();
        mediator.CurrentInfo = new FocuserInfo
        {
            Connected = true,
            Name = "Moonlite",
        };
        await mediator.RaiseConnectedAsync();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "focuser_disconnected" &&
            Equals(record.Attributes["focuser_name"], "Moonlite"));
    }

    [Fact]
    public async Task Disconnected_WhenDeviceInfoAlreadyClearedMetrics_StillUsesPreviousFocuserName()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = false,
            Name = "EAF",
        });
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "focuser_disconnected" &&
            Equals(record.Attributes["focuser_name"], "EAF"));
        sink.Records.Where(static record => record.Signal == TelemetrySignal.Metric).Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnected_WhenNoKnownFocuser_UsesUnknownName()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Name == "focuser_disconnected" &&
            record.Body == "Focuser disconnected" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["focuser_name"], "Unknown"));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeFocuserMediator { ThrowOnAddDisconnected = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

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
            record.Source == "nina.focuser" &&
            record.Name == "focuser_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Disconnected subscription failed."));
    }

    [Fact]
    public void Start_WhenEventSubscriptionFailsAfterInitialMetrics_ClearsThoseMetrics()
    {
        var mediator = new FakeFocuserMediator
        {
            ThrowOnAddDisconnected = true,
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
                Position = 1234,
                Temperature = -4.5,
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "focuser_position" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["focuser_name"], "EAF"));
        sink.Records.Should().Contain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "focuser_temperature" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["focuser_name"], "EAF"));
    }

    [Fact]
    public async Task Start_WhenEventSubscriptionFails_LateMediatorEventsDoNotPublishLifecycleLogs()
    {
        var mediator = new FakeFocuserMediator
        {
            ThrowOnAddDisconnected = true,
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenRollbackRemoveConsumerFails_LateDeviceUpdatesDoNotPublishMetrics()
    {
        var mediator = new FakeFocuserMediator
        {
            ThrowOnAddDisconnected = true,
            ThrowOnRemove = true,
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        mediator.Broadcast(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });

        sink.Records.Should().BeEmpty();
        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public async Task Start_WhenEventAccessorAttachesThenThrows_RollbackUnsubscribesLateLifecycleCallback()
    {
        var mediator = new FakeFocuserMediator
        {
            ThrowOnAddConnected = true,
            AttachConnectedBeforeThrow = true,
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
            },
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().BeEmpty();
        mediator.ConnectedSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_WhenRollbackUnsubscribeFailedAfterPartialAttach_RetriesUnsubscribe()
    {
        var mediator = new FakeFocuserMediator
        {
            ThrowOnAddConnected = true,
            AttachConnectedBeforeThrow = true,
            ThrowOnRemoveConnected = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
        mediator.RemoveConnectedCalls.Should().Be(2);
    }

    [Fact]
    public async Task Callbacks_WhenSinkThrows_DoNotThrowIntoNina()
    {
        var mediator = new FakeFocuserMediator
        {
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
            },
        };
        using var collector = new FocuserTelemetryCollector(
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
        var mediator = new FakeFocuserMediator
        {
            ThrowOnRemoveConnected = true,
            ThrowOnRemoveDisconnected = true,
            CurrentInfo = new FocuserInfo
            {
                Connected = true,
                Name = "EAF",
            },
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);
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

    [Fact]
    public void UpdateEndAutoFocusRun_WhenCompleted_PublishesAutofocusSpanWithReferenceFields()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(
            mediator,
            sink,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 23, 15, 0, TimeSpan.Zero)));

        collector.UpdateDeviceInfo(new FocuserInfo
        {
            Connected = true,
            Name = "EAF",
            Position = 1234,
            Temperature = -4.5,
        });
        sink.Records.Clear();

        collector.UpdateEndAutoFocusRun(CreateAutoFocusInfo(12456, -3.25, "Ha"));

        var span = sink.Records.Should()
            .ContainSingle(static record => record.Signal == TelemetrySignal.Span)
            .Which;
        span.Source.Should().Be("nina.focuser");
        span.Name.Should().Be("nina.autofocus");
        span.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 18, 23, 15, 0, TimeSpan.Zero));
        span.SpanKind.Should().Be(SpanEventKind.Stop);
        span.SpanId.Should().NotBeNullOrWhiteSpace();
        span.Attributes.Should().Contain("focuser_name", "EAF");
        span.Attributes.Should().Contain("autofocus_position", 12456);
        span.Attributes.Should().Contain("autofocus_temperature", -3.25);
        span.Attributes.Should().Contain("autofocus_filter", "Ha");
    }

    [Fact]
    public void UpdateEndAutoFocusRun_WhenFilterOrValuesAreUnavailable_PublishesReferenceDefaults()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateEndAutoFocusRun(CreateAutoFocusInfo(double.NaN, double.NaN, string.Empty));

        var span = sink.Records.Should()
            .ContainSingle(static record => record.Signal == TelemetrySignal.Span && record.Name == "nina.autofocus")
            .Which;
        span.Attributes.Should().NotContainKey("focuser_name");
        span.Attributes.Should().Contain("autofocus_position", 0);
        span.Attributes.Should().Contain("autofocus_temperature", 0d);
        span.Attributes.Should().Contain("autofocus_filter", "Unknown");
    }

    [Theory]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(2147484647d, int.MaxValue)]
    [InlineData(-2147484648d, int.MinValue)]
    public void UpdateEndAutoFocusRun_WhenPositionCannotConvert_PublishesSpanWithBoundedPosition(
        double position,
        int expectedPosition)
    {
        var span = PublishAutofocusSpan(
            DateTimeOffset.UtcNow,
            CreateAutoFocusInfo(position, -3.25, "Ha"));

        span.Attributes.Should().Contain("autofocus_position", expectedPosition);
    }

    [Fact]
    public void UpdateEndAutoFocusRun_WhenInfoIsNull_DoesNotPublishAndDoesNotThrow()
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateEndAutoFocusRun(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void UpdateEndAutoFocusRun_WhenIdentityInputsRepeat_CreatesStableAndDistinctSpanIds()
    {
        var timestamp = new DateTimeOffset(2026, 6, 18, 23, 15, 0, TimeSpan.Zero);
        var first = PublishAutofocusSpan(
            timestamp,
            CreateAutoFocusInfo(12456, -3.25, "Ha"),
            "EAF");
        var repeated = PublishAutofocusSpan(
            timestamp,
            CreateAutoFocusInfo(12456, -3.25, "Ha"),
            "EAF");
        var differentTimestamp = PublishAutofocusSpan(
            timestamp.AddSeconds(1),
            CreateAutoFocusInfo(12456, -3.25, "Ha"),
            "EAF");
        var differentFilter = PublishAutofocusSpan(
            timestamp,
            CreateAutoFocusInfo(12456, -3.25, "OIII"),
            "EAF");

        first.SpanId.Should().Be(repeated.SpanId);
        first.SpanId.Should().NotBe(differentTimestamp.SpanId);
        first.SpanId.Should().NotBe(differentFilter.SpanId);
    }

    [Fact]
    public void UpdateEndAutoFocusRun_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeFocuserMediator();
        using var collector = new FocuserTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateEndAutoFocusRun(CreateAutoFocusInfo(12456, -3.25, "Ha"));

        act.Should().NotThrow();
    }

    private static TelemetryRecord PublishAutofocusSpan(
        DateTimeOffset timestamp,
        AutoFocusInfo autofocusInfo,
        string? focuserName = null)
    {
        var mediator = new FakeFocuserMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FocuserTelemetryCollector(mediator, sink, new FixedTimeProvider(timestamp));

        if (focuserName is not null)
        {
            collector.UpdateDeviceInfo(new FocuserInfo
            {
                Connected = true,
                Name = focuserName,
                Position = 1234,
                Temperature = -4.5,
            });
            sink.Records.Clear();
        }

        collector.UpdateEndAutoFocusRun(autofocusInfo);

        return sink.Records.Should()
            .ContainSingle(static record => record.Signal == TelemetrySignal.Span && record.Name == "nina.autofocus")
            .Which;
    }

    private static AutoFocusInfo CreateAutoFocusInfo(double position, double temperature, string filter) =>
        new(temperature, position, filter, DateTime.UtcNow);

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

        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;

        public FocuserInfo CurrentInfo { get; set; } = new();

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

        public void RegisterHandler(IFocuserVM handler)
        {
        }

        public void RegisterConsumer(IFocuserConsumer consumer)
        {
            RegisterCalls++;
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
            consumer.UpdateDeviceInfo(CurrentInfo);
        }

        public void RemoveConsumer(IFocuserConsumer consumer)
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

        public void Broadcast(FocuserInfo deviceInfo)
        {
            CurrentInfo = deviceInfo;
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public FocuserInfo GetInfo()
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
