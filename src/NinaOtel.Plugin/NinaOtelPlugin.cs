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
    private readonly TelemetryPipeline pipeline;
    private readonly AddonHost addonHost;

    [ImportingConstructor]
    public NinaOtelPlugin(IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);

        var options = NinaOtelOptions.CreateDefault();
        NinaOtelOptionsViewModel = new NinaOtelOptionsViewModel(options);
        pipeline = new TelemetryPipeline(new NopTelemetryExporter(), options.Buffer.MemoryQueueCapacity);
        addonHost = new AddonHost(
            pipeline,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
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
        await shutdownCts.CancelAsync().ConfigureAwait(false);
        await addonHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        shutdownCts.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }

    private sealed class NopTelemetryExporter : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
