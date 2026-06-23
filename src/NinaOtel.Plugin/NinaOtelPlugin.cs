using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reflection;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using NinaOtel.Core.Telemetry;
using NinaOtel.Plugin.Addons;
using NinaOtel.Plugin.Options;
using NinaOtel.Plugin.Telemetry;

namespace NinaOtel.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class NinaOtelPlugin : PluginBase
{
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly IProfileService profileService;
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly ReloadableTelemetryExporter exporter;
    private readonly TelemetryPipeline pipeline;
    private readonly ITelemetrySink telemetrySink;
    private readonly CoreTelemetryFilteringSink filteringTelemetrySink;
    private readonly AddonHost addonHost;
    private readonly object addonOptionsSyncRoot = new();
    private readonly CoreLifecycleTelemetryProducer lifecycleTelemetry;
    private readonly CameraTelemetryCollector cameraTelemetry;
    private readonly FocuserTelemetryCollector focuserTelemetry;
    private readonly RotatorTelemetryCollector rotatorTelemetry;
    private readonly FilterWheelTelemetryCollector filterWheelTelemetry;
    private readonly MountTelemetryCollector mountTelemetry;
    private readonly WeatherTelemetryCollector weatherTelemetry;
    private readonly AstrometricTelemetryCollector astrometricTelemetry;
    private readonly SwitchTelemetryCollector switchTelemetry;
    private readonly GuiderTelemetryCollector guiderTelemetry;
    private readonly SafetyMonitorTelemetryCollector safetyMonitorTelemetry;
    private readonly FlatDeviceTelemetryCollector flatDeviceTelemetry;
    private readonly DomeTelemetryCollector domeTelemetry;
    private readonly ImageTelemetryCollector imageTelemetry;
    private readonly NinaLogTelemetryCollector ninaLogTelemetry;
    private IReadOnlyDictionary<string, AddonConfiguration> addonConfigurations;
    private IReadOnlyDictionary<string, AddonConfiguration> appliedAddonConfigurations;
    private IReadOnlyDictionary<string, AddonConfiguration>? pendingAddonConfigurations;
    private Task? addonOptionsApplyTask;
    private bool addOnsStarted;
    private bool addonOptionsApplyRunning;

    [ImportingConstructor]
    public NinaOtelPlugin(
        IProfileService profileService,
        ICameraMediator cameraMediator,
        IFocuserMediator focuserMediator,
        IRotatorMediator rotatorMediator,
        IFilterWheelMediator filterWheelMediator,
        ITelescopeMediator telescopeMediator,
        IWeatherDataMediator weatherDataMediator,
        ISwitchMediator switchMediator,
        IGuiderMediator guiderMediator,
        ISafetyMonitorMediator safetyMonitorMediator,
        IFlatDeviceMediator flatDeviceMediator,
        IDomeMediator domeMediator,
        IImageSaveMediator imageSaveMediator)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(cameraMediator);
        ArgumentNullException.ThrowIfNull(focuserMediator);
        ArgumentNullException.ThrowIfNull(rotatorMediator);
        ArgumentNullException.ThrowIfNull(filterWheelMediator);
        ArgumentNullException.ThrowIfNull(telescopeMediator);
        ArgumentNullException.ThrowIfNull(weatherDataMediator);
        ArgumentNullException.ThrowIfNull(switchMediator);
        ArgumentNullException.ThrowIfNull(guiderMediator);
        ArgumentNullException.ThrowIfNull(safetyMonitorMediator);
        ArgumentNullException.ThrowIfNull(flatDeviceMediator);
        ArgumentNullException.ThrowIfNull(domeMediator);
        ArgumentNullException.ThrowIfNull(imageSaveMediator);

        this.profileService = profileService;
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(
            new ProfilePluginSettingsStore(profileService, Guid.Parse(Identifier)));
        var options = NinaOtelOptionsViewModel.Options;
        exporter = new ReloadableTelemetryExporter(CreateCollectorExporter(options));
        pipeline = new TelemetryPipeline(exporter, options.Buffer.MemoryQueueCapacity);
        filteringTelemetrySink = new CoreTelemetryFilteringSink(
            new ProfileHostTelemetrySink(pipeline, profileService),
            options.CoreTelemetry);
        telemetrySink = filteringTelemetrySink;
        cameraTelemetry = new CameraTelemetryCollector(cameraMediator, telemetrySink, timeProvider);
        focuserTelemetry = new FocuserTelemetryCollector(focuserMediator, telemetrySink, timeProvider);
        rotatorTelemetry = new RotatorTelemetryCollector(rotatorMediator, telemetrySink, timeProvider);
        filterWheelTelemetry = new FilterWheelTelemetryCollector(filterWheelMediator, telemetrySink, timeProvider);
        mountTelemetry = new MountTelemetryCollector(telescopeMediator, telemetrySink, timeProvider);
        weatherTelemetry = new WeatherTelemetryCollector(weatherDataMediator, telemetrySink, timeProvider);
        astrometricTelemetry = new AstrometricTelemetryCollector(profileService, telemetrySink, timeProvider);
        switchTelemetry = new SwitchTelemetryCollector(switchMediator, telemetrySink, timeProvider);
        guiderTelemetry = new GuiderTelemetryCollector(guiderMediator, telemetrySink, timeProvider);
        safetyMonitorTelemetry = new SafetyMonitorTelemetryCollector(safetyMonitorMediator, telemetrySink, timeProvider);
        flatDeviceTelemetry = new FlatDeviceTelemetryCollector(flatDeviceMediator, telemetrySink, timeProvider);
        domeTelemetry = new DomeTelemetryCollector(domeMediator, telemetrySink, timeProvider);
        imageTelemetry = new ImageTelemetryCollector(imageSaveMediator, telemetrySink, timeProvider);
        ninaLogTelemetry = new NinaLogTelemetryCollector(options.CoreTelemetry, telemetrySink, timeProvider);
        lifecycleTelemetry = new CoreLifecycleTelemetryProducer(
            telemetrySink,
            timeProvider,
            options,
            ResolvePluginVersion());
        addonConfigurations = CreateAddonConfigurations(options.Addons);
        appliedAddonConfigurations = addonConfigurations;
        addonHost = new AddonHost(
            telemetrySink,
            timeProvider,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            addonConfigurations,
            NinaOtelOptionsViewModel.UpdateAddonHealth);
        profileService.ProfileChanged += ProfileService_ProfileChanged;
        NinaOtelOptionsViewModel.PropertyChanged += NinaOtelOptionsViewModel_PropertyChanged;
    }

    public NinaOtelOptionsViewModel NinaOtelOptionsViewModel { get; }

    private static string? ResolvePluginVersion()
    {
        var assembly = typeof(NinaOtelPlugin).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString();
    }

    public override async Task Initialize()
    {
        await pipeline.StartAsync(shutdownCts.Token).ConfigureAwait(false);
        cameraTelemetry.Start();
        focuserTelemetry.Start();
        rotatorTelemetry.Start();
        filterWheelTelemetry.Start();
        mountTelemetry.Start();
        weatherTelemetry.Start();
        astrometricTelemetry.Start();
        switchTelemetry.Start();
        guiderTelemetry.Start();
        safetyMonitorTelemetry.Start();
        flatDeviceTelemetry.Start();
        domeTelemetry.Start();
        imageTelemetry.Start();
        ninaLogTelemetry.Start();
        lifecycleTelemetry.PluginInitialized();
        IReadOnlyDictionary<string, AddonConfiguration> initialAddonConfigurations;
        lock (addonOptionsSyncRoot)
        {
            initialAddonConfigurations = addonConfigurations;
        }

        await addonHost.StartAsync(FirstPartyAddonCatalog.CreateAll(), initialAddonConfigurations, shutdownCts.Token)
            .ConfigureAwait(false);

        lock (addonOptionsSyncRoot)
        {
            addOnsStarted = true;
            if (AddonConfigurationsEqual(addonConfigurations, initialAddonConfigurations))
            {
                appliedAddonConfigurations = initialAddonConfigurations;
            }
            else
            {
                pendingAddonConfigurations = addonConfigurations;
                if (!addonOptionsApplyRunning)
                {
                    addonOptionsApplyRunning = true;
                    StartAddonOptionsApplyLoop();
                }
            }
        }

        Logger.Info("NinaOtel foundation initialized.");
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileService_ProfileChanged;
        NinaOtelOptionsViewModel.PropertyChanged -= NinaOtelOptionsViewModel_PropertyChanged;
        Task? applyTask;
        lock (addonOptionsSyncRoot)
        {
            addOnsStarted = false;
            pendingAddonConfigurations = null;
            applyTask = addonOptionsApplyTask;
        }

        await shutdownCts.CancelAsync().ConfigureAwait(false);
        if (applyTask is not null)
        {
            try
            {
                await applyTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        cameraTelemetry.Dispose();
        focuserTelemetry.Dispose();
        rotatorTelemetry.Dispose();
        filterWheelTelemetry.Dispose();
        mountTelemetry.Dispose();
        weatherTelemetry.Dispose();
        astrometricTelemetry.Dispose();
        switchTelemetry.Dispose();
        guiderTelemetry.Dispose();
        safetyMonitorTelemetry.Dispose();
        flatDeviceTelemetry.Dispose();
        domeTelemetry.Dispose();
        imageTelemetry.Dispose();
        ninaLogTelemetry.Dispose();
        lifecycleTelemetry.PluginStopping();
        await addonHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleTelemetry.PluginStopped();
        await pipeline.DisposeAsync().ConfigureAwait(false);
        shutdownCts.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }

    private void ProfileService_ProfileChanged(object? sender, EventArgs e) =>
        NinaOtelOptionsViewModel.Reload();

    private void NinaOtelOptionsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NinaOtelOptionsViewModel.Options))
        {
            return;
        }

        var options = NinaOtelOptionsViewModel.Options;
        exporter.Update(CreateCollectorExporter(options));
        filteringTelemetrySink.UpdateOptions(options.CoreTelemetry);
        ninaLogTelemetry.UpdateOptions(options.CoreTelemetry);
        lifecycleTelemetry.ProfileChanged(options);
        ApplyAddonOptions(options);
        Logger.Info("NinaOtel exporter settings applied.");
    }

    private void ApplyAddonOptions(NinaOtelOptions options)
    {
        var updatedConfigurations = CreateAddonConfigurations(options.Addons);

        lock (addonOptionsSyncRoot)
        {
            var desiredUnchanged = AddonConfigurationsEqual(addonConfigurations, updatedConfigurations);
            addonConfigurations = updatedConfigurations;

            if (!addOnsStarted)
            {
                return;
            }

            if (desiredUnchanged && AddonConfigurationsEqual(appliedAddonConfigurations, updatedConfigurations))
            {
                return;
            }

            pendingAddonConfigurations = updatedConfigurations;
            if (!addonOptionsApplyRunning)
            {
                addonOptionsApplyRunning = true;
                StartAddonOptionsApplyLoop();
            }
        }
    }

    private void StartAddonOptionsApplyLoop()
    {
        addonOptionsApplyTask = Task.Run(ApplyAddonOptionsAsync);
        _ = addonOptionsApplyTask;
    }

    private async Task ApplyAddonOptionsAsync()
    {
        while (true)
        {
            IReadOnlyDictionary<string, AddonConfiguration>? configurations;
            lock (addonOptionsSyncRoot)
            {
                configurations = pendingAddonConfigurations;
                pendingAddonConfigurations = null;
                if (configurations is null)
                {
                    addonOptionsApplyRunning = false;
                    return;
                }
            }

            try
            {
                await addonHost.RestartAsync(FirstPartyAddonCatalog.CreateAll(), configurations, shutdownCts.Token)
                    .ConfigureAwait(false);
                lock (addonOptionsSyncRoot)
                {
                    appliedAddonConfigurations = configurations;
                }

                Logger.Info("NinaOtel add-on settings applied.");
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                lock (addonOptionsSyncRoot)
                {
                    addonOptionsApplyRunning = false;
                    pendingAddonConfigurations = null;
                }

                return;
            }
            catch (Exception ex)
            {
                Logger.Info($"NinaOtel add-on settings apply failed: {ex.Message}");
            }
        }
    }

    private static IReadOnlyDictionary<string, AddonConfiguration> CreateAddonConfigurations(
        IReadOnlyDictionary<string, AddonOptions> addonOptions)
    {
        var configurations = new Dictionary<string, AddonConfiguration>(StringComparer.Ordinal);
        foreach (var descriptor in FirstPartyAddonCatalog.Descriptors)
        {
            var options = addonOptions.TryGetValue(descriptor.Id, out var configuredOptions)
                ? configuredOptions
                : new AddonOptions();

            configurations[descriptor.Id] = new AddonConfiguration(
                rawForwardingEnabled: options.RawForwardingEnabled,
                settings: options.Settings,
                enabled: options.Enabled);
        }

        return configurations;
    }

    private static bool AddonConfigurationsEqual(
        IReadOnlyDictionary<string, AddonConfiguration> left,
        IReadOnlyDictionary<string, AddonConfiguration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (addonId, leftConfiguration) in left)
        {
            if (!right.TryGetValue(addonId, out var rightConfiguration) ||
                leftConfiguration.ConfigVersion != rightConfiguration.ConfigVersion ||
                leftConfiguration.RawForwardingEnabled != rightConfiguration.RawForwardingEnabled ||
                leftConfiguration.Enabled != rightConfiguration.Enabled ||
                !AddonSettingsEqual(leftConfiguration.Settings, rightConfiguration.Settings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AddonSettingsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) ||
                !string.Equals(leftValue, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private ITelemetryExporter CreateCollectorExporter(NinaOtelOptions options)
    {
        var collectorExporter = new CollectorHealthReportingExporter(
            new OtlpTelemetryExporter(options),
            NinaOtelOptionsViewModel.UpdateCollectorHealth,
            options.Otlp.Endpoint,
            options.Otlp.Protocol,
            timeProvider);

        if (!options.Buffer.DiskOnFailureEnabled)
        {
            return collectorExporter;
        }

        return new DurableTelemetryExporter(
            collectorExporter,
            new DiskTelemetrySpool(
                options.Buffer.SpoolPath,
                options.Buffer.MaxSpoolBytes,
                options.Buffer.MaxSpoolAge),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(5),
            NinaOtelOptionsViewModel.UpdateCollectorHealth,
            options.Otlp.Endpoint,
            options.Otlp.Protocol,
            timeProvider);
    }
}
