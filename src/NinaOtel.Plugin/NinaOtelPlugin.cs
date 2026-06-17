using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Addons;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using NinaOtel.Plugin.Options;

namespace NinaOtel.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class NinaOtelPlugin : PluginBase
{
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly IProfileService profileService;
    private readonly TelemetryPipeline pipeline;
    private readonly AddonHost addonHost;

    [ImportingConstructor]
    public NinaOtelPlugin(IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);

        this.profileService = profileService;
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(
            new ProfilePluginSettingsStore(profileService, Guid.Parse(Identifier)));
        var options = NinaOtelOptionsViewModel.Options;
        pipeline = new TelemetryPipeline(new NopTelemetryExporter(), options.Buffer.MemoryQueueCapacity);
        addonHost = new AddonHost(
            pipeline,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        profileService.ProfileChanged += ProfileService_ProfileChanged;
    }

    public NinaOtelOptionsViewModel NinaOtelOptionsViewModel { get; }

    public override async Task Initialize()
    {
        await pipeline.StartAsync(shutdownCts.Token).ConfigureAwait(false);
        await addonHost.StartAsync(Array.Empty<ITelemetryAddon>(), shutdownCts.Token).ConfigureAwait(false);
        Logger.Info("NinaOtel foundation initialized.");
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileService_ProfileChanged;
        await shutdownCts.CancelAsync().ConfigureAwait(false);
        await addonHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        shutdownCts.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }

    private void ProfileService_ProfileChanged(object? sender, EventArgs e) =>
        NinaOtelOptionsViewModel.Reload();

    private sealed class NopTelemetryExporter : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
