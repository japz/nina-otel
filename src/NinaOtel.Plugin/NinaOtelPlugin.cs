using System.ComponentModel;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using NinaOtel.Core.Telemetry;
using NinaOtel.Plugin.Options;

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

    [ImportingConstructor]
    public NinaOtelPlugin(IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);

        this.profileService = profileService;
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(
            new ProfilePluginSettingsStore(profileService, Guid.Parse(Identifier)));
        var options = NinaOtelOptionsViewModel.Options;
        exporter = new ReloadableTelemetryExporter(CreateCollectorExporter(options));
        pipeline = new TelemetryPipeline(exporter, options.Buffer.MemoryQueueCapacity);
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
        lifecycleTelemetry.PluginInitialized();
        await addonHost.StartAsync(Array.Empty<ITelemetryAddon>(), shutdownCts.Token).ConfigureAwait(false);
        Logger.Info("NinaOtel foundation initialized.");
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileService_ProfileChanged;
        NinaOtelOptionsViewModel.PropertyChanged -= NinaOtelOptionsViewModel_PropertyChanged;
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
