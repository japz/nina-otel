using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Addons.PHD2;

public sealed class Phd2TelemetryAddon : ITelemetryAddon
{
    private const string DebugLogPathSetting = "DebugLogPath";
    private const string GuideLogPathSetting = "GuideLogPath";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan stopTimeout;
    private readonly object syncRoot = new();
    private List<Phd2LogTailer> tailers = [];
    private string? activeSettleSpanId;
    private readonly Dictionary<string, Phd2GuideSession> activeGuideSessions =
        new(StringComparer.OrdinalIgnoreCase);

    public Phd2TelemetryAddon()
        : this(DefaultPollInterval, DefaultStopTimeout)
    {
    }

    internal Phd2TelemetryAddon(TimeSpan pollInterval)
        : this(pollInterval, DefaultStopTimeout)
    {
    }

    internal Phd2TelemetryAddon(TimeSpan pollInterval, TimeSpan stopTimeout)
    {
        this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        this.stopTimeout = stopTimeout > TimeSpan.Zero ? stopTimeout : DefaultStopTimeout;
    }

    public AddonMetadata Metadata { get; } = new(
        "phd2",
        "PHD2",
        new Version(0, 1, 0),
        "PHD2");

    public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

    public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var configuredPaths = GetConfiguredPaths(context.Configuration);

        if (configuredPaths.Count == 0)
        {
            ReportWaiting(context, "Configure PHD2 log paths to collect guiding telemetry.");
            return Task.CompletedTask;
        }

        var startedTailers = new List<Phd2LogTailer>(configuredPaths.Count);
        foreach (var configuredPath in configuredPaths)
        {
            var tailer = new Phd2LogTailer(
                configuredPath.Path,
                pollInterval,
                (line, token) =>
                {
                    PublishLine(context, line, configuredPath);
                    return Task.CompletedTask;
                },
                missingPath => ReportWaiting(context, $"PHD2 log file not found: {missingPath}"));
            startedTailers.Add(tailer);
        }

        lock (syncRoot)
        {
            if (tailers.Count > 0)
            {
                return Task.CompletedTask;
            }

            tailers = startedTailers;
        }

        foreach (var tailer in startedTailers)
        {
            _ = tailer.StartAsync(cancellationToken);
        }

        context.ReportHealth(
            Metadata.Id,
            "running",
            "Collecting PHD2 log telemetry.",
            TelemetryPriority.Routine);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Phd2LogTailer> tailersToStop;
        lock (syncRoot)
        {
            tailersToStop = tailers;
            tailers = [];
            activeSettleSpanId = null;
            activeGuideSessions.Clear();
        }

        if (tailersToStop.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(stopTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopTasks = new List<Task>(tailersToStop.Count);

        foreach (var tailer in tailersToStop)
        {
            try
            {
                stopTasks.Add(tailer.StopAsync(linked.Token));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        foreach (var stopTask in stopTasks)
        {
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static IReadOnlyList<ConfiguredPhd2LogPath> GetConfiguredPaths(AddonConfiguration configuration)
    {
        var paths = new List<ConfiguredPhd2LogPath>(capacity: 2);
        AddConfiguredPath(configuration, DebugLogPathSetting, Phd2LogSourceKind.DebugLog, paths);
        AddConfiguredPath(configuration, GuideLogPathSetting, Phd2LogSourceKind.GuideLog, paths);
        return paths;
    }

    private static void AddConfiguredPath(
        AddonConfiguration configuration,
        string settingKey,
        Phd2LogSourceKind sourceKind,
        List<ConfiguredPhd2LogPath> paths)
    {
        if (configuration.Settings.TryGetValue(settingKey, out var path) &&
            !string.IsNullOrWhiteSpace(path))
        {
            paths.Add(new ConfiguredPhd2LogPath(path, sourceKind));
        }
    }

    private void PublishLine(IAddonContext context, string line, ConfiguredPhd2LogPath configuredPath)
    {
        if (configuredPath.SourceKind == Phd2LogSourceKind.GuideLog)
        {
            PublishGuideLine(context, line, configuredPath.Path);
            return;
        }

        PublishDebugLine(context, line, configuredPath.Path);
    }

    private void PublishDebugLine(IAddonContext context, string line, string sourcePath)
    {
        if (!Phd2LogParser.TryParseDebugLine(line, sourcePath, context.TimeProvider, out var logEvent) ||
            logEvent is null)
        {
            return;
        }

        var record = CreateTelemetryRecord(logEvent, context.Configuration.RawForwardingEnabled);
        if (record is not null)
        {
            context.Sink.TryPublish(record);
        }
    }

    private void PublishGuideLine(IAddonContext context, string line, string sourcePath)
    {
        if (Phd2LogParser.TryParseDebugLine(line, sourcePath, context.TimeProvider, out var logEvent) &&
            logEvent is not null)
        {
            var summary = UpdateGuideSession(logEvent);
            var record = CreateTelemetryRecord(logEvent, context.Configuration.RawForwardingEnabled);
            if (record is not null)
            {
                context.Sink.TryPublish(record);
            }

            if (summary is not null)
            {
                foreach (var metric in CreateGuideSummaryMetrics(summary))
                {
                    context.Sink.TryPublish(metric);
                }
            }

            return;
        }

        IReadOnlyList<TelemetryRecord>? pulseMetrics = null;
        lock (syncRoot)
        {
            if (!activeGuideSessions.TryGetValue(sourcePath, out var session))
            {
                return;
            }

            if (Phd2LogParser.TryParseGuideSampleLine(
                    line,
                    session.StartedAt,
                    sourcePath,
                    out var sample) &&
                sample is not null)
            {
                if (session.Add(sample))
                {
                    pulseMetrics = CreateGuidePulseMetrics(session, sample).ToArray();
                }
            }
        }

        if (pulseMetrics is null)
        {
            return;
        }

        foreach (var metric in pulseMetrics)
        {
            context.Sink.TryPublish(metric);
        }
    }

    private Phd2GuideSummary? UpdateGuideSession(Phd2LogEvent logEvent)
    {
        lock (syncRoot)
        {
            if (logEvent.Kind == Phd2LogEventKind.GuidingStarted)
            {
                activeGuideSessions[logEvent.SourcePath] = new Phd2GuideSession(
                    logEvent.Timestamp,
                    logEvent.SourcePath);
                return null;
            }

            if (logEvent.Kind == Phd2LogEventKind.GuidingStopped &&
                activeGuideSessions.Remove(logEvent.SourcePath, out var session))
            {
                return session.Complete(logEvent.Timestamp);
            }

            return null;
        }
    }

    private static IEnumerable<TelemetryRecord> CreateGuideSummaryMetrics(Phd2GuideSummary summary)
    {
        if (summary.SampleCount == 0)
        {
            yield break;
        }

        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "phd2",
            ["source"] = "phd2",
            ["source.file"] = summary.SourcePath,
            ["guider_name"] = "PHD2",
            ["phd2.session_start"] = summary.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        };

        yield return CreateGuideMetric(summary, "phd2_guide_rms_ra_arcsec", summary.RaRmsArcsec, attributes);
        yield return CreateGuideMetric(summary, "phd2_guide_rms_dec_arcsec", summary.DecRmsArcsec, attributes);
        yield return CreateGuideMetric(summary, "phd2_guide_rms_total_arcsec", summary.TotalRmsArcsec, attributes);
        yield return CreateGuideMetric(summary, "phd2_guide_sample_count", summary.SampleCount, attributes);
    }

    private static IEnumerable<TelemetryRecord> CreateGuidePulseMetrics(
        Phd2GuideSession session,
        Phd2GuideSample sample)
    {
        if (sample.Pulse is not { } pulse)
        {
            yield break;
        }

        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "phd2",
            ["source"] = "phd2",
            ["source.file"] = sample.SourcePath,
            ["guider_name"] = "PHD2",
            ["phd2.session_start"] = session.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["phd2.ra_direction"] = pulse.RaDirection,
            ["phd2.dec_direction"] = pulse.DecDirection,
        };

        if (sample.Frame is { } frame)
        {
            attributes["phd2.frame"] = frame;
        }

        yield return CreateGuidePulseMetric(sample, "phd2_guide_ra_pulse_distance_arcsec", pulse.RaDistanceArcsec, attributes);
        yield return CreateGuidePulseMetric(sample, "phd2_guide_ra_pulse_duration_ms", pulse.RaDurationMs, attributes);
        yield return CreateGuidePulseMetric(sample, "phd2_guide_dec_pulse_distance_arcsec", pulse.DecDistanceArcsec, attributes);
        yield return CreateGuidePulseMetric(sample, "phd2_guide_dec_pulse_duration_ms", pulse.DecDurationMs, attributes);
    }

    private static TelemetryRecord CreateGuideMetric(
        Phd2GuideSummary summary,
        string name,
        double value,
        IReadOnlyDictionary<string, object?> attributes) =>
        TelemetryRecord.Metric(
            summary.EndedAt,
            "phd2",
            name,
            value,
            TelemetryPriority.Normal,
            attributes);

    private static TelemetryRecord CreateGuidePulseMetric(
        Phd2GuideSample sample,
        string name,
        double value,
        IReadOnlyDictionary<string, object?> attributes) =>
        TelemetryRecord.Metric(
            sample.Timestamp,
            "phd2",
            name,
            value,
            TelemetryPriority.Normal,
            attributes);

    private TelemetryRecord? CreateTelemetryRecord(
        Phd2LogEvent logEvent,
        bool rawForwardingEnabled) =>
        logEvent.Kind switch
        {
            Phd2LogEventKind.GuidingStarted => CreateLog(
                logEvent,
                "phd2.guiding_started",
                TelemetrySeverity.Information,
                "PHD2 guiding started.",
                TelemetryPriority.Normal,
                rawForwardingEnabled),
            Phd2LogEventKind.GuidingStopped => CreateLog(
                logEvent,
                "phd2.guiding_stopped",
                TelemetrySeverity.Information,
                "PHD2 guiding stopped.",
                TelemetryPriority.Routine,
                rawForwardingEnabled),
            Phd2LogEventKind.CaptureError => CreateLog(
                logEvent,
                "phd2.capture_error",
                TelemetrySeverity.Error,
                "PHD2 capture error.",
                TelemetryPriority.Important,
                rawForwardingEnabled),
            Phd2LogEventKind.Dither => CreateSpan(
                logEvent,
                "phd2.dither",
                SpanEventKind.Stop,
                CreateSpanId(logEvent),
                rawForwardingEnabled),
            Phd2LogEventKind.SettleStarted => CreateSettleSpan(
                logEvent,
                SpanEventKind.Start,
                rawForwardingEnabled),
            Phd2LogEventKind.SettleCompleted => CreateSettleSpan(
                logEvent,
                SpanEventKind.Stop,
                rawForwardingEnabled),
            _ => null,
        };

    private TelemetryRecord CreateLog(
        Phd2LogEvent logEvent,
        string name,
        TelemetrySeverity severity,
        string body,
        TelemetryPriority priority,
        bool rawForwardingEnabled) =>
        TelemetryRecord.Log(
            logEvent.Timestamp,
            logEvent.Source,
            severity,
            body,
            priority,
            CreateAttributes(logEvent, rawForwardingEnabled)) with
        {
            Name = name,
        };

    private TelemetryRecord CreateSpan(
        Phd2LogEvent logEvent,
        string name,
        SpanEventKind spanKind,
        string spanId,
        bool rawForwardingEnabled) =>
        TelemetryRecord.Span(
            logEvent.Timestamp,
            logEvent.Source,
            name,
            spanKind,
            spanId,
            TelemetryPriority.Normal,
            CreateAttributes(logEvent, rawForwardingEnabled));

    private TelemetryRecord CreateSettleSpan(
        Phd2LogEvent logEvent,
        SpanEventKind spanKind,
        bool rawForwardingEnabled)
    {
        string spanId;
        lock (syncRoot)
        {
            if (spanKind == SpanEventKind.Start)
            {
                activeSettleSpanId = CreateSpanId(logEvent);
                spanId = activeSettleSpanId;
            }
            else if (!string.IsNullOrWhiteSpace(activeSettleSpanId))
            {
                spanId = activeSettleSpanId;
                activeSettleSpanId = null;
            }
            else
            {
                spanId = CreateSpanId(logEvent);
            }
        }

        return CreateSpan(
            logEvent,
            "phd2.settle",
            spanKind,
            spanId,
            rawForwardingEnabled);
    }

    private static Dictionary<string, object?> CreateAttributes(
        Phd2LogEvent logEvent,
        bool rawForwardingEnabled)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "phd2",
            ["source"] = logEvent.Source,
            ["source.file"] = logEvent.SourcePath,
            ["event.kind"] = ToEventKindName(logEvent.Kind),
            ["message"] = CreateBoundedMessage(logEvent),
        };

        if (rawForwardingEnabled)
        {
            attributes["raw.line"] = logEvent.OriginalLine;
        }

        return attributes;
    }

    private static string CreateBoundedMessage(Phd2LogEvent logEvent) =>
        logEvent.Kind switch
        {
            Phd2LogEventKind.GuidingStarted => "Guiding started.",
            Phd2LogEventKind.GuidingStopped => "Guiding stopped.",
            Phd2LogEventKind.Dither => "Dither event.",
            Phd2LogEventKind.SettleCompleted => "Settle completed.",
            Phd2LogEventKind.CaptureError => "Capture error.",
            _ => "PHD2 event.",
        };

    private static string ToEventKindName(Phd2LogEventKind kind) =>
        kind switch
        {
            Phd2LogEventKind.GuidingStarted => "guiding_started",
            Phd2LogEventKind.GuidingStopped => "guiding_stopped",
            Phd2LogEventKind.Dither => "dither",
            Phd2LogEventKind.SettleStarted => "settle_started",
            Phd2LogEventKind.SettleCompleted => "settle_completed",
            Phd2LogEventKind.CaptureError => "capture_error",
            _ => kind.ToString(),
        };

    private static string CreateSpanId(Phd2LogEvent logEvent)
    {
        var input = $"{logEvent.Timestamp:O}|{logEvent.SourcePath}|{logEvent.OriginalLine}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private void ReportWaiting(IAddonContext context, string message) =>
        context.ReportHealth(
            Metadata.Id,
            "waiting",
            message,
            TelemetryPriority.Routine);

    private enum Phd2LogSourceKind
    {
        DebugLog,
        GuideLog,
    }

    private sealed record ConfiguredPhd2LogPath(string Path, Phd2LogSourceKind SourceKind);

    private sealed class Phd2GuideSession
    {
        private double raSquaredSum;
        private double decSquaredSum;

        public Phd2GuideSession(DateTimeOffset startedAt, string sourcePath)
        {
            StartedAt = startedAt;
            SourcePath = sourcePath;
        }

        public DateTimeOffset StartedAt { get; }

        public string SourcePath { get; }

        public int SampleCount { get; private set; }

        public bool Add(Phd2GuideSample sample)
        {
            var raSquare = sample.RaDistanceArcsec * sample.RaDistanceArcsec;
            var decSquare = sample.DecDistanceArcsec * sample.DecDistanceArcsec;
            var nextRaSquaredSum = raSquaredSum + raSquare;
            var nextDecSquaredSum = decSquaredSum + decSquare;
            var nextTotalSquaredSum = nextRaSquaredSum + nextDecSquaredSum;

            if (!double.IsFinite(raSquare) ||
                !double.IsFinite(decSquare) ||
                !double.IsFinite(nextRaSquaredSum) ||
                !double.IsFinite(nextDecSquaredSum) ||
                !double.IsFinite(nextTotalSquaredSum))
            {
                return false;
            }

            raSquaredSum = nextRaSquaredSum;
            decSquaredSum = nextDecSquaredSum;
            SampleCount++;
            return true;
        }

        public Phd2GuideSummary? Complete(DateTimeOffset endedAt)
        {
            if (SampleCount == 0)
            {
                return null;
            }

            return new Phd2GuideSummary(
                StartedAt,
                endedAt,
                SourcePath,
                SampleCount,
                Math.Sqrt(raSquaredSum / SampleCount),
                Math.Sqrt(decSquaredSum / SampleCount),
                Math.Sqrt((raSquaredSum + decSquaredSum) / SampleCount));
        }
    }

    private sealed record Phd2GuideSummary(
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        string SourcePath,
        int SampleCount,
        double RaRmsArcsec,
        double DecRmsArcsec,
        double TotalRmsArcsec);
}
