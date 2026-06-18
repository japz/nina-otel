using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NINA.Astrometry;
using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Plugin.Telemetry;
using Xunit;

namespace NinaOtel.Plugin.Tests.Telemetry;

public sealed class AstrometricTelemetryCollectorTests
{
    [Fact]
    public async Task Start_PublishesSunAndMoonMetricsFromActiveProfileCoordinates()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        var timestamp = new DateTimeOffset(2026, 6, 18, 22, 15, 30, TimeSpan.Zero);
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            new FixedTimeProvider(timestamp),
            calculator,
            TimeSpan.FromHours(1));

        collector.Start();

        await WaitUntilAsync(() => sink.Count == 2);
        var records = sink.Snapshot();
        records.Should().OnlyContain(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Source == "nina.astrometry" &&
            record.Timestamp == timestamp &&
            record.Priority == TelemetryPriority.Normal &&
            record.Attributes.Count == 0);
        records.Should().ContainSingle(record =>
            record.Name == "astro_sun_altitude" &&
            record.NumericValue == 14.25);
        records.Should().ContainSingle(record =>
            record.Name == "astro_moon_altitude" &&
            record.NumericValue == -31.5);

        var request = calculator.Requests.Should().ContainSingle().Which;
        request.UtcNow.Should().Be(timestamp.UtcDateTime);
        request.UtcNow.Kind.Should().Be(DateTimeKind.Utc);
        request.Latitude.Should().Be(52.25);
        request.Longitude.Should().Be(5.75);
        request.Elevation.Should().Be(31.5);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromHours(1));

        collector.Start();
        collector.Start();

        await WaitUntilAsync(() => sink.Count == 2);
        await Task.Delay(50);
        calculator.Requests.Should().ContainSingle();
        sink.Snapshot().Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Start_PublishesPeriodicallyAfterPromptSample()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromMilliseconds(25));

        collector.Start();

        await WaitUntilAsync(() => sink.Count >= 4);
        calculator.Requests.Count.Should().BeGreaterThanOrEqualTo(2);
        sink.Snapshot().Where(static record => record.Signal == TelemetrySignal.Metric)
            .Should().HaveCountGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Start_WhenActiveProfileIsNull_DoesNotThrowOrPublish()
    {
        var profileService = CreateProfileService(activeProfile: null);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromHours(1));

        var act = () => collector.Start();

        act.Should().NotThrow();
        await Task.Delay(50);
        sink.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenAstrometrySettingsAreNull_DoesNotThrowOrPublish()
    {
        var profile = CreateProfile(astrometrySettings: null);
        var profileService = CreateProfileService(profile);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromHours(1));

        var act = () => collector.Start();

        act.Should().NotThrow();
        await Task.Delay(50);
        sink.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenProfileAccessThrows_PublishesHealthAndDoesNotThrow()
    {
        var proxy = CreateProfileServiceProxy(out var profileService);
        proxy.ThrowOnActiveProfile = true;
        var sink = new RecordingTelemetrySink();
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5)),
            TimeSpan.FromHours(1));

        var act = () => collector.Start();

        act.Should().NotThrow();
        await WaitUntilAsync(() => sink.Count == 1);
        sink.Snapshot().Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.astrometry" &&
            record.Name == "astrometry_collector.profile_read_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Profile unavailable."));
    }

    [Fact]
    public async Task Start_WhenCalculatorThrows_PublishesHealthAndDoesNotThrow()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5))
        {
            ExceptionToThrow = new InvalidOperationException("Calculation failed."),
        };
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromHours(1));

        var act = () => collector.Start();

        act.Should().NotThrow();
        await WaitUntilAsync(() => sink.Count == 1);
        sink.Snapshot().Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == "nina.astrometry" &&
            record.Name == "astrometry_collector.calculation_failed" &&
            record.Priority == TelemetryPriority.Important &&
            Equals(record.Attributes["error_type"], nameof(InvalidOperationException)) &&
            Equals(record.Attributes["error_message"], "Calculation failed."));
    }

    [Fact]
    public async Task Start_WhenSinkThrows_DoesNotThrow()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            new ThrowingTelemetrySink(),
            TimeProvider.System,
            new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5)),
            TimeSpan.FromHours(1));

        var act = () => collector.Start();

        act.Should().NotThrow();
        await Task.Delay(50);
    }

    [Fact]
    public async Task Start_WhenCalculatorReturnsNonFiniteValues_PublishesOnlyFiniteMetrics()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        var calculator = new RecordingAltitudeCalculator(
            new AstrometricAltitudes(double.NaN, 23.5));
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            calculator,
            TimeSpan.FromHours(1));

        collector.Start();

        await WaitUntilAsync(() => calculator.Requests.Count == 1);
        await WaitUntilAsync(() => sink.Count == 1);
        sink.Snapshot().Should().ContainSingle().Which.Should().Match<TelemetryRecord>(record =>
            record.Signal == TelemetrySignal.Metric &&
            record.Name == "astro_moon_altitude" &&
            record.NumericValue == 23.5);
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndStopsPeriodicPublication()
    {
        var profileService = CreateProfileService(52.25, 5.75, 31.5);
        var sink = new RecordingTelemetrySink();
        using var collector = new AstrometricTelemetryCollector(
            profileService,
            sink,
            TimeProvider.System,
            new RecordingAltitudeCalculator(new AstrometricAltitudes(14.25, -31.5)),
            TimeSpan.FromMilliseconds(50));

        collector.Start();
        await WaitUntilAsync(() => sink.Count == 2);

        collector.Dispose();
        collector.Dispose();
        var countAfterDispose = sink.Count;
        await Task.Delay(150);

        sink.Count.Should().Be(countAfterDispose);
    }

    private static IProfileService CreateProfileService(
        double latitude,
        double longitude,
        double elevation) =>
        CreateProfileService(CreateProfile(CreateAstrometrySettings(latitude, longitude, elevation)));

    private static IProfileService CreateProfileService(IProfile? activeProfile)
    {
        var proxy = CreateProfileServiceProxy(out var profileService);
        proxy.ActiveProfile = activeProfile;
        return profileService;
    }

    private static ProfileServiceProxy CreateProfileServiceProxy(out IProfileService profileService)
    {
        profileService = DispatchProxy.Create<IProfileService, ProfileServiceProxy>();
        return (ProfileServiceProxy)(object)profileService;
    }

    private static IProfile CreateProfile(IAstrometrySettings? astrometrySettings)
    {
        var profile = DispatchProxy.Create<IProfile, ProfileProxy>();
        ((ProfileProxy)(object)profile).AstrometrySettings = astrometrySettings;
        return profile;
    }

    private static IAstrometrySettings CreateAstrometrySettings(
        double latitude,
        double longitude,
        double elevation)
    {
        var astrometrySettings = DispatchProxy.Create<IAstrometrySettings, AstrometrySettingsProxy>();
        var proxy = (AstrometrySettingsProxy)(object)astrometrySettings;
        proxy.Latitude = latitude;
        proxy.Longitude = longitude;
        proxy.Elevation = elevation;
        return astrometrySettings;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        while (stopwatch.Elapsed < limit)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    private sealed class RecordingAltitudeCalculator(
        AstrometricAltitudes altitudes) : IAstrometricAltitudeCalculator
    {
        public ConcurrentQueue<CalculationRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public AstrometricAltitudes Calculate(DateTime utcNow, ObserverInfo observerInfo)
        {
            Requests.Enqueue(new CalculationRequest(
                utcNow,
                observerInfo.Latitude,
                observerInfo.Longitude,
                observerInfo.Elevation));

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return altitudes;
        }
    }

    private sealed record CalculationRequest(
        DateTime UtcNow,
        double Latitude,
        double Longitude,
        double Elevation);

    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        private readonly object syncRoot = new();
        private readonly List<TelemetryRecord> records = [];

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return records.Count;
                }
            }
        }

        public bool TryPublish(TelemetryRecord record)
        {
            lock (syncRoot)
            {
                records.Add(record);
            }

            return true;
        }

        public IReadOnlyList<TelemetryRecord> Snapshot()
        {
            lock (syncRoot)
            {
                return records.ToList();
            }
        }
    }

    private sealed class ThrowingTelemetrySink : ITelemetrySink
    {
        public bool TryPublish(TelemetryRecord record) =>
            throw new InvalidOperationException("Sink unavailable.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    public class ProfileServiceProxy : DispatchProxy
    {
        public IProfile? ActiveProfile { get; set; }

        public bool ThrowOnActiveProfile { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            var methodName = targetMethod!.Name;

            if (methodName is "get_ActiveProfile")
            {
                if (ThrowOnActiveProfile)
                {
                    throw new InvalidOperationException("Profile unavailable.");
                }

                return ActiveProfile;
            }

            return DefaultReturnValue(this, targetMethod, args);
        }
    }

    public class ProfileProxy : DispatchProxy
    {
        public IAstrometrySettings? AstrometrySettings { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            var methodName = targetMethod!.Name;

            return methodName switch
            {
                "get_AstrometrySettings" => AstrometrySettings,
                "set_AstrometrySettings" => SetAstrometrySettings(args),
                _ => DefaultReturnValue(this, targetMethod, args),
            };
        }

        private object? SetAstrometrySettings(object?[]? args)
        {
            AstrometrySettings = args?[0] as IAstrometrySettings;
            return null;
        }
    }

    public class AstrometrySettingsProxy : DispatchProxy
    {
        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double Elevation { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod.Should().NotBeNull();
            var methodName = targetMethod!.Name;

            return methodName switch
            {
                "get_Latitude" => Latitude,
                "set_Latitude" => SetLatitude(args),
                "get_Longitude" => Longitude,
                "set_Longitude" => SetLongitude(args),
                "get_Elevation" => Elevation,
                "set_Elevation" => SetElevation(args),
                _ => DefaultReturnValue(this, targetMethod, args),
            };
        }

        private object? SetLatitude(object?[]? args)
        {
            Latitude = (double)args![0]!;
            return null;
        }

        private object? SetLongitude(object?[]? args)
        {
            Longitude = (double)args![0]!;
            return null;
        }

        private object? SetElevation(object?[]? args)
        {
            Elevation = (double)args![0]!;
            return null;
        }
    }

    private static object? DefaultReturnValue(
        object instance,
        MethodInfo targetMethod,
        object?[]? args)
    {
        var methodName = targetMethod.Name;
        var returnType = targetMethod.ReturnType;

        if (methodName is nameof(Equals))
        {
            return args?.Length > 0 && ReferenceEquals(instance, args[0]);
        }

        if (methodName is nameof(GetHashCode))
        {
            return RuntimeHelpers.GetHashCode(instance);
        }

        if (methodName is nameof(ToString))
        {
            return instance.GetType().Name;
        }

        if (returnType == typeof(bool))
        {
            return false;
        }

        if (returnType == typeof(string))
        {
            return string.Empty;
        }

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

        if (returnType == typeof(int))
        {
            return 0;
        }

        if (returnType == typeof(double))
        {
            return 0d;
        }

        if (returnType == typeof(Guid))
        {
            return Guid.Empty;
        }

        if (returnType == typeof(DateTime))
        {
            return DateTime.MinValue;
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
