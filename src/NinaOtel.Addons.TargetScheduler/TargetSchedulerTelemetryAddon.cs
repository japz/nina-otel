using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Addons.TargetScheduler;

public sealed class TargetSchedulerTelemetryAddon : ITelemetryAddon
{
    private const string LogPathSetting = "LogPath";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan stopTimeout;
    private readonly object syncRoot = new();
    private TargetSchedulerLogTailer? tailer;
    private string? activePlanningSpanId;

    public TargetSchedulerTelemetryAddon()
        : this(DefaultPollInterval, DefaultStopTimeout)
    {
    }

    internal TargetSchedulerTelemetryAddon(TimeSpan pollInterval)
        : this(pollInterval, DefaultStopTimeout)
    {
    }

    internal TargetSchedulerTelemetryAddon(TimeSpan pollInterval, TimeSpan stopTimeout)
    {
        this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        this.stopTimeout = stopTimeout > TimeSpan.Zero ? stopTimeout : DefaultStopTimeout;
    }

    public AddonMetadata Metadata { get; } = new(
        "target-scheduler",
        "Target Scheduler",
        new Version(0, 1, 0),
        "Target Scheduler");

    public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

    public async Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var logPath = GetConfiguredPath(context.Configuration);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            await StopCurrentTailerAsync(cancellationToken).ConfigureAwait(false);
            ReportWaiting(context, "Configure the Target Scheduler log path to collect source telemetry.");
            return;
        }

        await StopCurrentTailerAsync(cancellationToken).ConfigureAwait(false);

        var nextTailer = new TargetSchedulerLogTailer(
            logPath,
            pollInterval,
            (line, token) =>
            {
                PublishLine(context, line, logPath);
                return Task.CompletedTask;
            },
            missingPath => ReportWaiting(context, $"Target Scheduler log file not found: {missingPath}"));

        lock (syncRoot)
        {
            tailer = nextTailer;
            activePlanningSpanId = null;
        }

        _ = nextTailer.StartAsync(cancellationToken);
        context.ReportHealth(
            Metadata.Id,
            "running",
            "Collecting Target Scheduler log telemetry.",
            TelemetryPriority.Routine);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopCurrentTailerAsync(cancellationToken);

    private async Task StopCurrentTailerAsync(CancellationToken cancellationToken)
    {
        TargetSchedulerLogTailer? tailerToStop;
        lock (syncRoot)
        {
            tailerToStop = tailer;
            tailer = null;
        }

        if (tailerToStop is null)
        {
            lock (syncRoot)
            {
                activePlanningSpanId = null;
            }

            return;
        }

        using var timeout = new CancellationTokenSource(stopTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await tailerToStop.StopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (syncRoot)
            {
                activePlanningSpanId = null;
            }
        }
    }

    private static string? GetConfiguredPath(AddonConfiguration configuration)
    {
        if (configuration.Settings.TryGetValue(LogPathSetting, out var path) &&
            !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return null;
    }

    private void PublishLine(IAddonContext context, string line, string sourcePath)
    {
        if (!TargetSchedulerLogParser.TryParse(line, sourcePath, context.TimeProvider, out var logEvent) ||
            logEvent is null)
        {
            return;
        }

        foreach (var record in CreateTelemetryRecords(logEvent, context.Configuration.RawForwardingEnabled))
        {
            context.Sink.TryPublish(record);
        }
    }

    private IReadOnlyList<TelemetryRecord> CreateTelemetryRecords(
        TargetSchedulerLogEvent logEvent,
        bool rawForwardingEnabled) =>
        logEvent.Kind switch
        {
            TargetSchedulerLogEventKind.PlanningStarted =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.planning_started",
                    TelemetrySeverity.Information,
                    TelemetryPriority.Normal,
                    rawForwardingEnabled),
                CreatePlanningSpan(logEvent, SpanEventKind.Start, rawForwardingEnabled),
                CreateMetric(logEvent, "target_scheduler_planning_run_count", 1),
            ],
            TargetSchedulerLogEventKind.PlanningCompleted =>
            [
                CreatePlanningSpan(logEvent, SpanEventKind.Stop, rawForwardingEnabled),
                CreateMetric(logEvent, "target_scheduler_planning_run_completed_count", 1),
            ],
            TargetSchedulerLogEventKind.TargetSelected =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.target_selected",
                    TelemetrySeverity.Information,
                    TelemetryPriority.Normal,
                    rawForwardingEnabled),
                CreateMetric(logEvent, "target_scheduler_target_selected_count", 1),
                CreateMetric(logEvent, "target_scheduler_current_target", 1),
            ],
            TargetSchedulerLogEventKind.PlanStarted =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.plan_started",
                    TelemetrySeverity.Information,
                    TelemetryPriority.Normal,
                    rawForwardingEnabled),
                CreateMetric(logEvent, "target_scheduler_plan_started_count", 1),
                CreateMetric(logEvent, "target_scheduler_current_target", 1),
            ],
            TargetSchedulerLogEventKind.PlanStopped =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.plan_stopped",
                    TelemetrySeverity.Information,
                    TelemetryPriority.Routine,
                    rawForwardingEnabled),
                CreateMetric(logEvent, "target_scheduler_plan_stopped_count", 1),
            ],
            TargetSchedulerLogEventKind.ImageGraded =>
                CreateImageGradedRecords(logEvent, rawForwardingEnabled),
            TargetSchedulerLogEventKind.Warning =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.warning",
                    TelemetrySeverity.Warning,
                    TelemetryPriority.Important,
                    rawForwardingEnabled),
            ],
            TargetSchedulerLogEventKind.Error =>
            [
                CreateLog(
                    logEvent,
                    "target_scheduler.error",
                    TelemetrySeverity.Error,
                    TelemetryPriority.Important,
                    rawForwardingEnabled),
            ],
            _ => [],
        };

    private IReadOnlyList<TelemetryRecord> CreateImageGradedRecords(
        TargetSchedulerLogEvent logEvent,
        bool rawForwardingEnabled)
    {
        var records = new List<TelemetryRecord>
        {
            CreateLog(
                logEvent,
                "target_scheduler.image_graded",
                TelemetrySeverity.Information,
                TelemetryPriority.Routine,
                rawForwardingEnabled),
            CreateMetric(logEvent, "target_scheduler_image_graded_count", 1),
        };

        if (logEvent.GradeScore is { } score)
        {
            records.Add(CreateMetric(logEvent, "target_scheduler_image_grade_score", score));
        }

        return records;
    }

    private TelemetryRecord CreateLog(
        TargetSchedulerLogEvent logEvent,
        string name,
        TelemetrySeverity severity,
        TelemetryPriority priority,
        bool rawForwardingEnabled) =>
        TelemetryRecord.Log(
            logEvent.Timestamp,
            logEvent.Source,
            severity,
            logEvent.Message,
            priority,
            CreateAttributes(logEvent, rawForwardingEnabled)) with
        {
            Name = name,
        };

    private TelemetryRecord CreatePlanningSpan(
        TargetSchedulerLogEvent logEvent,
        SpanEventKind spanKind,
        bool rawForwardingEnabled)
    {
        string spanId;
        lock (syncRoot)
        {
            if (spanKind == SpanEventKind.Start)
            {
                activePlanningSpanId = CreateSpanId(logEvent);
                spanId = activePlanningSpanId;
            }
            else if (!string.IsNullOrWhiteSpace(activePlanningSpanId))
            {
                spanId = activePlanningSpanId;
                activePlanningSpanId = null;
            }
            else
            {
                spanId = CreateSpanId(logEvent);
            }
        }

        return TelemetryRecord.Span(
            logEvent.Timestamp,
            logEvent.Source,
            "target_scheduler.planning",
            spanKind,
            spanId,
            TelemetryPriority.Normal,
            CreateAttributes(logEvent, rawForwardingEnabled));
    }

    private static TelemetryRecord CreateMetric(
        TargetSchedulerLogEvent logEvent,
        string name,
        double value) =>
        TelemetryRecord.Metric(
            logEvent.Timestamp,
            logEvent.Source,
            name,
            value,
            TelemetryPriority.Normal,
            CreateMetricAttributes(logEvent));

    private static Dictionary<string, object?> CreateAttributes(
        TargetSchedulerLogEvent logEvent,
        bool rawForwardingEnabled)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "target-scheduler",
            ["source"] = logEvent.Source,
            ["source.file"] = logEvent.SourcePath,
            ["event.kind"] = ToEventKindName(logEvent.Kind),
            ["message"] = logEvent.Message,
        };

        if (rawForwardingEnabled)
        {
            attributes["raw.line"] = logEvent.OriginalLine;
        }

        AddStructuredAttributes(logEvent, attributes, includeGradeScore: true);
        return attributes;
    }

    private static Dictionary<string, object?> CreateMetricAttributes(TargetSchedulerLogEvent logEvent)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "target-scheduler",
            ["source"] = logEvent.Source,
            ["source.file"] = logEvent.SourcePath,
            ["event.kind"] = ToEventKindName(logEvent.Kind),
        };

        AddStructuredAttributes(logEvent, attributes, includeGradeScore: false);
        return attributes;
    }

    private static void AddStructuredAttributes(
        TargetSchedulerLogEvent logEvent,
        Dictionary<string, object?> attributes,
        bool includeGradeScore)
    {
        if (!string.IsNullOrWhiteSpace(logEvent.TargetName))
        {
            attributes["target.name"] = logEvent.TargetName;
        }

        if (!string.IsNullOrWhiteSpace(logEvent.FilterName))
        {
            attributes["filter.name"] = logEvent.FilterName;
        }

        if (!string.IsNullOrWhiteSpace(logEvent.GradeStatus))
        {
            attributes["grade.status"] = logEvent.GradeStatus;
        }

        if (includeGradeScore && logEvent.GradeScore is { } score)
        {
            attributes["grade.score"] = score;
        }

        if (!string.IsNullOrWhiteSpace(logEvent.StopReason))
        {
            attributes["stop.reason"] = logEvent.StopReason;
        }
    }

    private static string ToEventKindName(TargetSchedulerLogEventKind kind) =>
        kind switch
        {
            TargetSchedulerLogEventKind.PlanningStarted => "planning_started",
            TargetSchedulerLogEventKind.PlanningCompleted => "planning_completed",
            TargetSchedulerLogEventKind.TargetSelected => "target_selected",
            TargetSchedulerLogEventKind.PlanStarted => "plan_started",
            TargetSchedulerLogEventKind.PlanStopped => "plan_stopped",
            TargetSchedulerLogEventKind.ImageGraded => "image_graded",
            TargetSchedulerLogEventKind.Warning => "warning",
            TargetSchedulerLogEventKind.Error => "error",
            _ => kind.ToString(),
        };

    private static string CreateSpanId(TargetSchedulerLogEvent logEvent)
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
}
