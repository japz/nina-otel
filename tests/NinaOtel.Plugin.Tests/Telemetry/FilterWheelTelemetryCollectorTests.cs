using FluentAssertions;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class FilterWheelTelemetryCollectorTests
{
    [Fact]
    public void Start_RegistersCollectorAsFilterWheelConsumerOnce()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
    }

    [Fact]
    public void Start_SubscribesToFilterChangedOnce()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        mediator.FilterChangedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Start_SubscribesToLifecycleEventsOnce()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();

        mediator.ConnectedSubscriberCount.Should().Be(1);
        mediator.DisconnectedSubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Start_WhenMediatorHasCurrentInfo_PublishesInitialFilterWheelMetric()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Source == "nina.filter_wheel" &&
            record.Name == "fwheel_filter" &&
            record.NumericValue == 2 &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
    }

    [Fact]
    public async Task FilterChanged_WhenRaised_PublishesFilterChangeSpanAndLog()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record => record.Signal == TelemetrySignal.Span)
            .Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Span &&
            record.Source == "nina.filter_wheel" &&
            record.Name == "nina.filter_change" &&
            record.SpanKind == SpanEventKind.Stop &&
            record.Priority == TelemetryPriority.Normal &&
            !string.IsNullOrWhiteSpace(record.SpanId) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_from"], "Ha") &&
            Equals(record.Attributes["filter_to"], "OIII") &&
            Equals(record.Attributes["filter_from_position"], 2) &&
            Equals(record.Attributes["filter_to_position"], 3));
        sink.Records.Should().ContainSingle(record => record.Signal == TelemetrySignal.Log)
            .Which.Should().Match<TelemetryRecord>(record =>
            record.Source == "nina.filter_wheel" &&
            record.Name == "filter_change" &&
            record.Body == "Filter changed from Ha to OIII" &&
            record.Severity == TelemetrySeverity.Information &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["title"], "Filter changed") &&
            Equals(record.Attributes["text"], "Filter changed from Ha to OIII") &&
            Equals(record.Attributes["filter_from"], "Ha") &&
            Equals(record.Attributes["filter_to"], "OIII") &&
            Equals(record.Attributes["filter_wheel_name"], "EFW"));
    }

    [Fact]
    public async Task Connected_WhenRaised_PublishesLifecycleLogWithKnownFilterWheelName()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.filter_wheel" &&
            record.Name == "fwheel_connected" &&
            record.Body == "Filter Wheel connected" &&
            record.Severity == TelemetrySeverity.Information &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["filter_wheel_name"], "EFW"));
    }

    [Fact]
    public async Task Connected_BeforeKnownFilterWheelName_UsesUnknownFilterWheelName()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await mediator.RaiseConnectedAsync();

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "fwheel_connected" &&
            Equals(record.Attributes["filter_wheel_name"], "Unknown"));
    }

    [Fact]
    public async Task Connected_WhenCurrentInfoHasNoSelectedFilter_UpdatesKnownFilterWheelNameForFilterChange()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfoWithoutSelectedFilter("EFW"),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await mediator.RaiseConnectedAsync();
        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });

        sink.Records.Should().ContainSingle(record => record.Name == "nina.filter_change")
            .Which.Attributes["filter_wheel_name"].Should().Be("EFW");
    }

    [Fact]
    public async Task Disconnected_WhenRaised_PublishesLifecycleLogAndClearsPreviousFilterMetric()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Log &&
            record.Source == "nina.filter_wheel" &&
            record.Name == "fwheel_disconnected" &&
            record.Body == "Filter Wheel disconnected" &&
            record.Severity == TelemetrySeverity.Information &&
            record.Priority == TelemetryPriority.Normal &&
            Equals(record.Attributes["filter_wheel_name"], "EFW"));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
    }

    [Fact]
    public async Task Disconnected_WhenRaisedRepeatedly_LogsOnceUntilConnectedAgain()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseDisconnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Where(record => record.Name == "fwheel_disconnected").Should().ContainSingle();
        sink.Records.Where(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value)).Should().ContainSingle();

        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        sink.Records.Where(record => record.Name == "fwheel_disconnected").Should().HaveCount(2);
    }

    [Fact]
    public async Task LifecycleEvents_WhenSinkThrows_DoNotThrow()
    {
        var mediator = new FakeFilterWheelMediator();
        using var collector = new FilterWheelTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var act = async () =>
        {
            await mediator.RaiseConnectedAsync();
            await mediator.RaiseDisconnectedAsync();
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LifecycleEvents_AfterDisposeWhenUnsubscribeFails_DoNotPublish()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
            ThrowOnConnectedRemove = true,
            ThrowOnDisconnectedRemove = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        collector.Dispose();
        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();

        mediator.ConnectedSubscriberCount.Should().Be(1);
        mediator.DisconnectedSubscriberCount.Should().Be(1);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task FilterChanged_BeforeKnownFilterWheelName_UsesUnknownFilterWheelName()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "L", Position = 1 },
            new FilterInfo { Name = "SII", Position = 4 });

        sink.Records.Should().ContainSingle(record => record.Name == "nina.filter_change")
            .Which.Attributes["filter_wheel_name"].Should().Be("Unknown");
    }

    [Fact]
    public async Task FilterChanged_WhenFilterNamesAreMissing_UsesUnknownFilterNamesInLog()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = " ", Position = 1 },
            new FilterInfo { Name = null!, Position = 4 });

        sink.Records.Should().ContainSingle(record => record.Name == "filter_change")
            .Which.Should().Match<TelemetryRecord>(record =>
            record.Body == "Filter changed from Unknown to Unknown" &&
            Equals(record.Attributes["text"], "Filter changed from Unknown to Unknown") &&
            Equals(record.Attributes["filter_from"], "Unknown") &&
            Equals(record.Attributes["filter_to"], "Unknown"));
    }

    [Fact]
    public async Task FilterChanged_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeFilterWheelMediator();
        using var collector = new FilterWheelTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);
        collector.Start();

        var act = () => mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "L", Position = 1 },
            new FilterInfo { Name = "SII", Position = 4 });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FilterChanged_AfterDisposeWhenUnsubscribeFails_DoesNotPublishSpan()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
            ThrowOnFilterChangedRemove = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        collector.Dispose();
        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });

        mediator.FilterChangedSubscriberCount.Should().Be(1);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenFilterChangedSubscriptionThrowsAfterAttaching_UnsubscribesHandlerBeforeReturning()
    {
        var mediator = new FakeFilterWheelMediator
        {
            ThrowOnFilterChangedAddAfterSubscribe = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        sink.Records.Clear();
        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });

        mediator.FilterChangedRemoveCalls.Should().Be(1);
        mediator.FilterChangedSubscriberCount.Should().Be(0);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenLifecycleSubscriptionThrowsAfterAttaching_RollsBackSubscribedHandlersAndPublishesHealth()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
            ThrowOnDisconnectedAddAfterSubscribe = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.filter_wheel" &&
            record.Name == "filter_wheel_collector.registration_failed" &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Disconnected subscription failed."));
        sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));

        sink.Records.Clear();
        await mediator.RaiseConnectedAsync();
        await mediator.RaiseDisconnectedAsync();
        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });
        mediator.Broadcast(ConnectedInfo("EFW", "OIII", 3));

        mediator.ConnectedRemoveCalls.Should().Be(1);
        mediator.DisconnectedRemoveCalls.Should().Be(1);
        mediator.FilterChangedRemoveCalls.Should().Be(1);
        mediator.ConnectedSubscriberCount.Should().Be(0);
        mediator.DisconnectedSubscriberCount.Should().Be(0);
        mediator.FilterChangedSubscriberCount.Should().Be(0);
        mediator.RemoveCalls.Should().Be(1);
        mediator.Consumers.Should().BeEmpty();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenFailedStartCannotRemoveConsumer_BlocksLaterBroadcasts()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
            ThrowOnDisconnectedAddAfterSubscribe = true,
            ThrowOnRemove = true,
        };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        sink.Records.Clear();

        mediator.Broadcast(ConnectedInfo("EFW", "OIII", 3));

        mediator.RemoveCalls.Should().Be(1);
        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task FilterChanged_WithSameTimestampAndAttributes_PublishesDistinctSpanIds()
    {
        var mediator = new FakeFilterWheelMediator
        {
            CurrentInfo = ConnectedInfo("EFW", "Ha", 2),
        };
        var sink = new RecordingTelemetrySink();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, timeProvider);
        collector.Start();
        sink.Records.Clear();

        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });
        await mediator.RaiseFilterChangedAsync(
            new FilterInfo { Name = "Ha", Position = 2 },
            new FilterInfo { Name = "OIII", Position = 3 });

        var spanIds = sink.Records
            .Where(static record => record.Name == "nina.filter_change")
            .Select(static record => record.SpanId)
            .ToArray();
        spanIds.Should().HaveCount(2);
        spanIds.Distinct(StringComparer.Ordinal).Should().HaveCount(2);
    }

    [Fact]
    public void Dispose_RemovesCollectorFromMediatorOnce()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        collector.Dispose();
        collector.Dispose();

        mediator.Consumers.Should().BeEmpty();
        mediator.RemoveCalls.Should().Be(1);
        mediator.ConnectedSubscriberCount.Should().Be(0);
        mediator.DisconnectedSubscriberCount.Should().Be(0);
        mediator.FilterChangedSubscriberCount.Should().Be(0);
        mediator.ConnectedRemoveCalls.Should().Be(1);
        mediator.DisconnectedRemoveCalls.Should().Be(1);
        mediator.FilterChangedRemoveCalls.Should().Be(1);
    }

    [Fact]
    public void Dispose_WhenMediatorRemovalFails_DoesNotThrow()
    {
        var mediator = new FakeFilterWheelMediator { ThrowOnRemove = true };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();

        var act = () => collector.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDeviceInfo_AfterDisposeWhenMediatorRemovalFails_DoesNotPublishMetric()
    {
        var mediator = new FakeFilterWheelMediator { ThrowOnRemove = true };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);
        collector.Start();
        sink.Records.Clear();

        collector.Dispose();
        mediator.Broadcast(ConnectedInfo("EFW", "OIII", 3));

        mediator.Consumers.Should().ContainSingle().Which.Should().BeSameAs(collector);
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_PublishesHealthAndDoesNotThrow()
    {
        var mediator = new FakeFilterWheelMediator { ThrowOnRegister = true };
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.Start();

        act.Should().NotThrow();
        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.filter_wheel" &&
            record.Name == "filter_wheel_collector.registration_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Registration failed."));
    }

    [Fact]
    public void Start_WhenMediatorRegistrationFails_DoesNotRetryOrRemoveOnDispose()
    {
        var mediator = new FakeFilterWheelMediator { ThrowOnRegister = true };
        var sink = new RecordingTelemetrySink();
        var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.Start();
        collector.Start();
        collector.Dispose();

        mediator.RegisterCalls.Should().Be(1);
        sink.Records.Should().ContainSingle(record => record.Signal == TelemetrySignal.Health);
        mediator.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public void UpdateDeviceInfo_WhenConnectedWithSelectedFilter_PublishesFilterWheelMetric()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "OIII", 3));

        sink.Records.Should().ContainSingle().Which.Should().BeEquivalentTo(
            TelemetryRecord.Metric(
                sink.Records[0].Timestamp,
                "nina.filter_wheel",
                "fwheel_filter",
                3,
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["filter_wheel_name"] = "EFW",
                    ["filter_name"] = "OIII",
                }),
            options => options.Excluding(record => record.Timestamp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UpdateDeviceInfo_WhenFilterWheelNameIsBlankOrNull_UsesUnknownAttribute(string? filterWheelName)
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo(filterWheelName, "L", 1));

        sink.Records.Should().ContainSingle().Which.Attributes["filter_wheel_name"].Should().Be("Unknown");
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDisconnectedBeforeAnyConnectedSample_DoesNotPublishMetrics()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(new FilterWheelInfo
        {
            Connected = false,
            Name = "EFW",
            SelectedFilter = new FilterInfo { Name = "L", Position = 1 },
        });

        sink.Records.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnavailableBeforeAnySampleCases))]
    public void UpdateDeviceInfo_WhenUnavailableBeforeAnySample_DoesNotPublishMetrics(FilterWheelInfo deviceInfo)
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(deviceInfo);

        sink.Records.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnavailableAfterPriorSampleCases))]
    public void UpdateDeviceInfo_WhenUnavailableAfterPriorSample_ClearsPreviousMetric(FilterWheelInfo deviceInfo)
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "Ha", 2));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(deviceInfo);

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenFilterWheelDisconnects_ClearsPreviousMetric()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "Ha", 2));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(new FilterWheelInfo
        {
            Connected = false,
            Name = "EFW",
        });

        sink.Records.Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenFilterWheelOrFilterIdentityChanges_ClearsOldMetricAndPublishesNewMetric()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "Ha", 2));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("CFW", "SII", 4));

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "fwheel_filter" &&
            record.NumericValue == 4 &&
            Equals(record.Attributes["filter_wheel_name"], "CFW") &&
            Equals(record.Attributes["filter_name"], "SII"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenOnlyFilterNameChanges_ClearsOldMetricAndPublishesNewMetric()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "Ha", 2));
        sink.Records.Clear();

        collector.UpdateDeviceInfo(ConnectedInfo("EFW", "OIII", 3));

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().ContainSingle(record =>
            record.Name == "fwheel_filter" &&
            double.IsNaN(record.NumericValue!.Value) &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "Ha"));
        sink.Records.Should().ContainSingle(record =>
            record.Name == "fwheel_filter" &&
            record.NumericValue == 3 &&
            Equals(record.Attributes["filter_wheel_name"], "EFW") &&
            Equals(record.Attributes["filter_name"], "OIII"));
    }

    [Fact]
    public void UpdateDeviceInfo_WhenSinkThrows_DoesNotThrow()
    {
        var mediator = new FakeFilterWheelMediator();
        using var collector = new FilterWheelTelemetryCollector(
            mediator,
            new ThrowingTelemetrySink(),
            TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(ConnectedInfo("EFW", "Ha", 2));

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDeviceInfo_WhenDeviceInfoIsNull_DoesNotThrowOrPublish()
    {
        var mediator = new FakeFilterWheelMediator();
        var sink = new RecordingTelemetrySink();
        using var collector = new FilterWheelTelemetryCollector(mediator, sink, TimeProvider.System);

        var act = () => collector.UpdateDeviceInfo(null!);

        act.Should().NotThrow();
        sink.Records.Should().BeEmpty();
    }

    public static TheoryData<FilterWheelInfo> UnavailableBeforeAnySampleCases() =>
        new()
        {
            ConnectedInfo("EFW", "Ha", 2, isMoving: true),
            new FilterWheelInfo
            {
                Connected = true,
                Name = "EFW",
                IsMoving = false,
                SelectedFilter = null!,
            },
            ConnectedInfo("EFW", "", 2),
            ConnectedInfo("EFW", " ", 2),
            ConnectedInfo("EFW", null, 2),
        };

    public static TheoryData<FilterWheelInfo> UnavailableAfterPriorSampleCases() =>
        new()
        {
            ConnectedInfo("EFW", "Ha", 2, isMoving: true),
            new FilterWheelInfo
            {
                Connected = true,
                Name = "EFW",
                IsMoving = false,
                SelectedFilter = null!,
            },
            ConnectedInfo("EFW", "", 2),
            ConnectedInfo("EFW", " ", 2),
            ConnectedInfo("EFW", null, 2),
        };

    private static FilterWheelInfo ConnectedInfo(
        string? filterWheelName,
        string? filterName,
        short position,
        bool isMoving = false) =>
        new()
        {
            Connected = true,
            Name = filterWheelName!,
            IsMoving = isMoving,
            SelectedFilter = new FilterInfo
            {
                Name = filterName!,
                Position = position,
            },
        };

    private static FilterWheelInfo ConnectedInfoWithoutSelectedFilter(string? filterWheelName) =>
        new()
        {
            Connected = true,
            Name = filterWheelName!,
            IsMoving = false,
            SelectedFilter = null!,
        };

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeFilterWheelMediator : IFilterWheelMediator
    {
        private Func<object, EventArgs, Task>? connected;
        private Func<object, EventArgs, Task>? disconnected;
        private Func<object, FilterChangedEventArgs, Task>? filterChanged;

        public List<IFilterWheelConsumer> Consumers { get; } = [];

        public FilterWheelInfo CurrentInfo { get; init; } = new();

        public bool ThrowOnRegister { get; init; }

        public bool ThrowOnRemove { get; init; }

        public bool ThrowOnFilterChangedAddAfterSubscribe { get; init; }

        public bool ThrowOnConnectedAddAfterSubscribe { get; init; }

        public bool ThrowOnDisconnectedAddAfterSubscribe { get; init; }

        public bool ThrowOnConnectedRemove { get; init; }

        public bool ThrowOnDisconnectedRemove { get; init; }

        public bool ThrowOnFilterChangedRemove { get; init; }

        public int RegisterCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int FilterChangedRemoveCalls { get; private set; }

        public int ConnectedRemoveCalls { get; private set; }

        public int DisconnectedRemoveCalls { get; private set; }

        public event Func<object, EventArgs, Task>? Connected
        {
            add
            {
                connected += value;
                if (ThrowOnConnectedAddAfterSubscribe)
                {
                    throw new InvalidOperationException("Connected subscription failed.");
                }
            }
            remove
            {
                ConnectedRemoveCalls++;
                if (ThrowOnConnectedRemove)
                {
                    throw new InvalidOperationException("Connected unsubscribe failed.");
                }

                connected -= value;
            }
        }

        public event Func<object, EventArgs, Task>? Disconnected
        {
            add
            {
                disconnected += value;
                if (ThrowOnDisconnectedAddAfterSubscribe)
                {
                    throw new InvalidOperationException("Disconnected subscription failed.");
                }
            }
            remove
            {
                DisconnectedRemoveCalls++;
                if (ThrowOnDisconnectedRemove)
                {
                    throw new InvalidOperationException("Disconnected unsubscribe failed.");
                }

                disconnected -= value;
            }
        }

        public event Func<object, FilterChangedEventArgs, Task>? FilterChanged
        {
            add
            {
                filterChanged += value;
                if (ThrowOnFilterChangedAddAfterSubscribe)
                {
                    throw new InvalidOperationException("FilterChanged subscription failed.");
                }
            }
            remove
            {
                FilterChangedRemoveCalls++;
                if (ThrowOnFilterChangedRemove)
                {
                    throw new InvalidOperationException("FilterChanged unsubscribe failed.");
                }

                filterChanged -= value;
            }
        }

        public int FilterChangedSubscriberCount => filterChanged?.GetInvocationList().Length ?? 0;

        public int ConnectedSubscriberCount => connected?.GetInvocationList().Length ?? 0;

        public int DisconnectedSubscriberCount => disconnected?.GetInvocationList().Length ?? 0;

        public void RegisterHandler(IFilterWheelVM handler)
        {
        }

        public void RegisterConsumer(IFilterWheelConsumer consumer)
        {
            RegisterCalls++;
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Consumers.Add(consumer);
            consumer.UpdateDeviceInfo(CurrentInfo);
        }

        public void RemoveConsumer(IFilterWheelConsumer consumer)
        {
            RemoveCalls++;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("Removal failed.");
            }

            Consumers.Remove(consumer);
        }

        public void Broadcast(FilterWheelInfo deviceInfo)
        {
            foreach (var consumer in Consumers.ToArray())
            {
                consumer.UpdateDeviceInfo(deviceInfo);
            }
        }

        public async Task RaiseFilterChangedAsync(FilterInfo from, FilterInfo to)
        {
            if (filterChanged is null)
            {
                return;
            }

            await filterChanged.Invoke(this, new FilterChangedEventArgs(from, to));
        }

        public async Task RaiseConnectedAsync()
        {
            if (connected is null)
            {
                return;
            }

            await connected.Invoke(this, EventArgs.Empty);
        }

        public async Task RaiseDisconnectedAsync()
        {
            if (disconnected is null)
            {
                return;
            }

            await disconnected.Invoke(this, EventArgs.Empty);
        }

        public FilterWheelInfo GetInfo() => CurrentInfo;

        public Task<IList<string>> Rescan() =>
            throw new NotSupportedException("Filter wheel telemetry must not rescan.");

        public Task<bool> Connect() =>
            throw new NotSupportedException("Filter wheel telemetry must not connect.");

        public Task Disconnect() =>
            throw new NotSupportedException("Filter wheel telemetry must not disconnect.");

        public IDevice GetDevice() =>
            throw new NotSupportedException("Filter wheel telemetry must not call GetDevice.");

        public string Action(string actionName, string actionParameters) =>
            throw new NotSupportedException("Filter wheel telemetry must not send actions.");

        public string SendCommandString(string command, bool raw = true) =>
            throw new NotSupportedException("Filter wheel telemetry must not send commands.");

        public bool SendCommandBool(string command, bool raw = true) =>
            throw new NotSupportedException("Filter wheel telemetry must not send commands.");

        public void SendCommandBlind(string command, bool raw = true) =>
            throw new NotSupportedException("Filter wheel telemetry must not send commands.");

        public Task<FilterInfo> ChangeFilter(
            FilterInfo inputFilter,
            CancellationToken token,
            IProgress<ApplicationStatus> progress) =>
            throw new NotSupportedException("Filter wheel telemetry must not change filters.");
    }
}
