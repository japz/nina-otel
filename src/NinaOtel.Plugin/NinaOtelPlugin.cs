using System.ComponentModel;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using NinaOtel.Core.Telemetry;
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
    private readonly AddonHost addonHost;
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
    private readonly ImageTelemetryCollector imageTelemetry;

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
        ArgumentNullException.ThrowIfNull(imageSaveMediator);

        this.profileService = profileService;
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(
            new ProfilePluginSettingsStore(profileService, Guid.Parse(Identifier)));
        var options = NinaOtelOptionsViewModel.Options;
        exporter = new ReloadableTelemetryExporter(CreateCollectorExporter(options));
        pipeline = new TelemetryPipeline(exporter, options.Buffer.MemoryQueueCapacity);
        cameraTelemetry = new CameraTelemetryCollector(cameraMediator, pipeline, timeProvider);
        focuserTelemetry = new FocuserTelemetryCollector(focuserMediator, pipeline, timeProvider);
        rotatorTelemetry = new RotatorTelemetryCollector(rotatorMediator, pipeline, timeProvider);
        filterWheelTelemetry = new FilterWheelTelemetryCollector(filterWheelMediator, pipeline, timeProvider);
        mountTelemetry = new MountTelemetryCollector(telescopeMediator, pipeline, timeProvider);
        weatherTelemetry = new WeatherTelemetryCollector(weatherDataMediator, pipeline, timeProvider);
        astrometricTelemetry = new AstrometricTelemetryCollector(profileService, pipeline, timeProvider);
        switchTelemetry = new SwitchTelemetryCollector(switchMediator, pipeline, timeProvider);
        guiderTelemetry = new GuiderTelemetryCollector(guiderMediator, pipeline, timeProvider);
        safetyMonitorTelemetry = new SafetyMonitorTelemetryCollector(safetyMonitorMediator, pipeline, timeProvider);
        imageTelemetry = new ImageTelemetryCollector(imageSaveMediator, pipeline, timeProvider);
        lifecycleTelemetry = new CoreLifecycleTelemetryProducer(pipeline, timeProvider, options);
        addonHost = new AddonHost(
            pipeline,
            timeProvider,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        profileService.ProfileChanged += ProfileService_ProfileChanged;
        NinaOtelOptionsViewModel.PropertyChanged += NinaOtelOptionsViewModel_PropertyChanged;
    }

    public NinaOtelOptionsViewModel NinaOtelOptionsViewModel { get; }

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
        imageTelemetry.Start();
        lifecycleTelemetry.PluginInitialized();
        await addonHost.StartAsync(Array.Empty<ITelemetryAddon>(), shutdownCts.Token).ConfigureAwait(false);
        Logger.Info("NinaOtel foundation initialized.");
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileService_ProfileChanged;
        NinaOtelOptionsViewModel.PropertyChanged -= NinaOtelOptionsViewModel_PropertyChanged;
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
        imageTelemetry.Dispose();
        lifecycleTelemetry.PluginStopping();
        await addonHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleTelemetry.PluginStopped();
        await pipeline.DisposeAsync().ConfigureAwait(false);
        await shutdownCts.CancelAsync().ConfigureAwait(false);
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
        lifecycleTelemetry.ProfileChanged(options);
        Logger.Info("NinaOtel exporter settings applied.");
    }

    private ITelemetryExporter CreateCollectorExporter(NinaOtelOptions options) =>
        new CollectorHealthReportingExporter(
            new OtlpTelemetryExporter(options),
            NinaOtelOptionsViewModel.UpdateCollectorHealth,
            options.Otlp.Endpoint,
            options.Otlp.Protocol,
            timeProvider);
}
