using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Addons.NightSummary;

public sealed class NightSummaryTelemetryAddon : ITelemetryAddon
{
    private const string LogPathSetting = "LogPath";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan stopTimeout;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, NightSummarySpanContext> activeReportSpans =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> activeSessionIds =
        new(StringComparer.OrdinalIgnoreCase);
    private NightSummaryLogTailer? tailer;

    public NightSummaryTelemetryAddon()
        : this(DefaultPollInterval, DefaultStopTimeout)
    {
    }

    internal NightSummaryTelemetryAddon(TimeSpan pollInterval)
        : this(pollInterval, DefaultStopTimeout)
    {
    }

    internal NightSummaryTelemetryAddon(TimeSpan pollInterval, TimeSpan stopTimeout)
    {
        this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        this.stopTimeout = stopTimeout > TimeSpan.Zero ? stopTimeout : DefaultStopTimeout;
    }

    public AddonMetadata Metadata { get; } = new(
        "night-summary",
        "Night Summary",
        new Version(0, 1, 0),
        "Night Summary");

    public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

    public async Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var logPath = GetConfiguredPath(context.Configuration);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            await StopCurrentTailerAsync(cancellationToken).ConfigureAwait(false);
            ReportDegraded(context, "Configure the Night Summary/NINA log path to collect source telemetry.");
            return;
        }

        await StopCurrentTailerAsync(cancellationToken).ConfigureAwait(false);

        var nextTailer = new NightSummaryLogTailer(
            logPath,
            pollInterval,
            (line, token) =>
            {
                PublishLine(context, line, logPath);
                return Task.CompletedTask;
            },
            missingPath => ReportDegraded(context, $"Night Summary/NINA log file not found: {missingPath}"));

        lock (syncRoot)
        {
            tailer = nextTailer;
        }

        if (File.Exists(logPath))
        {
            context.ReportHealth(
                Metadata.Id,
                "running",
                "Collecting Night Summary log telemetry.",
                TelemetryPriority.Routine);
        }
        else
        {
            ReportDegraded(context, $"Night Summary/NINA log file not found: {logPath}");
        }

        _ = nextTailer.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopCurrentTailerAsync(cancellationToken);

    private async Task StopCurrentTailerAsync(CancellationToken cancellationToken)
    {
        NightSummaryLogTailer? tailerToStop;
        lock (syncRoot)
        {
            tailerToStop = tailer;
            tailer = null;
        }

        if (tailerToStop is null)
        {
            ClearCorrelationState();
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
            ClearCorrelationState();
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
        if (!NightSummaryLogParser.TryParse(line, sourcePath, context.TimeProvider, out var logEvent) ||
            logEvent is null)
        {
            return;
        }

        PublishSafely(context, CreateTelemetryRecord(logEvent, context.Configuration.RawForwardingEnabled));
        foreach (var record in CreateStructuredRecords(logEvent))
        {
            PublishSafely(context, record);
        }
    }

    private IEnumerable<TelemetryRecord> CreateStructuredRecords(NightSummaryLogEvent logEvent)
    {
        var eventKind = ToEventKindName(logEvent.Kind);
        var effectiveSessionId = ResolveSessionId(logEvent);

        if (TryCreateSpan(logEvent, eventKind, effectiveSessionId, out var span) &&
            span is not null)
        {
            yield return span;
        }

        if (TryGetMetricName(logEvent.Kind, out var metricName))
        {
            yield return TelemetryRecord.Metric(
                logEvent.Timestamp,
                logEvent.Source,
                metricName,
                1,
                TelemetryPriority.Normal,
                CreateAttributes(logEvent, rawForwardingEnabled: false, eventKind, effectiveSessionId));
        }

        if (logEvent.Kind == NightSummaryLogEventKind.SessionEnded)
        {
            lock (syncRoot)
            {
                activeSessionIds.Remove(logEvent.SourcePath);
            }
        }
    }

    private string? ResolveSessionId(NightSummaryLogEvent logEvent)
    {
        if (!string.IsNullOrWhiteSpace(logEvent.SessionId))
        {
            if (logEvent.Kind == NightSummaryLogEventKind.SessionStarted)
            {
                lock (syncRoot)
                {
                    activeSessionIds[logEvent.SourcePath] = logEvent.SessionId;
                }
            }

            return logEvent.SessionId;
        }

        lock (syncRoot)
        {
            if (activeReportSpans.TryGetValue(logEvent.SourcePath, out var reportContext) &&
                !string.IsNullOrWhiteSpace(reportContext.SessionId))
            {
                return reportContext.SessionId;
            }

            if (IsReportTerminal(logEvent.Kind))
            {
                return null;
            }

            return activeSessionIds.TryGetValue(logEvent.SourcePath, out var sessionId)
                ? sessionId
                : null;
        }
    }

    private bool TryCreateSpan(
        NightSummaryLogEvent logEvent,
        string eventKind,
        string? effectiveSessionId,
        out TelemetryRecord? span)
    {
        span = null;
        return logEvent.Kind switch
        {
            NightSummaryLogEventKind.SessionStarted => TryCreateSimpleSpan(
                logEvent,
                "night_summary.session",
                SpanEventKind.Start,
                CreateSpanId("night_summary.session", logEvent, effectiveSessionId),
                eventKind,
                effectiveSessionId,
                out span),
            NightSummaryLogEventKind.SessionEnded => TryCreateSimpleSpan(
                logEvent,
                "night_summary.session",
                SpanEventKind.Stop,
                CreateSpanId("night_summary.session", logEvent, effectiveSessionId),
                eventKind,
                effectiveSessionId,
                out span),
            NightSummaryLogEventKind.ReportGenerating => TryCreateReportStartSpan(
                logEvent,
                eventKind,
                effectiveSessionId,
                out span),
            NightSummaryLogEventKind.ReportDelivered or NightSummaryLogEventKind.ReportFailed => TryCreateReportStopSpan(
                logEvent,
                eventKind,
                effectiveSessionId,
                out span),
            _ => false,
        };
    }

    private static bool TryCreateSimpleSpan(
        NightSummaryLogEvent logEvent,
        string name,
        SpanEventKind spanKind,
        string spanId,
        string eventKind,
        string? effectiveSessionId,
        out TelemetryRecord span)
    {
        span = TelemetryRecord.Span(
            logEvent.Timestamp,
            logEvent.Source,
            name,
            spanKind,
            spanId,
            TelemetryPriority.Normal,
            CreateAttributes(logEvent, rawForwardingEnabled: false, eventKind, effectiveSessionId));
        return true;
    }

    private bool TryCreateReportStartSpan(
        NightSummaryLogEvent logEvent,
        string eventKind,
        string? effectiveSessionId,
        out TelemetryRecord span)
    {
        var spanId = CreateSpanId("night_summary.report", logEvent, effectiveSessionId);
        lock (syncRoot)
        {
            activeReportSpans[logEvent.SourcePath] = new NightSummarySpanContext(spanId, effectiveSessionId);
        }

        return TryCreateSimpleSpan(
            logEvent,
            "night_summary.report",
            SpanEventKind.Start,
            spanId,
            eventKind,
            effectiveSessionId,
            out span);
    }

    private bool TryCreateReportStopSpan(
        NightSummaryLogEvent logEvent,
        string eventKind,
        string? effectiveSessionId,
        out TelemetryRecord span)
    {
        NightSummarySpanContext? activeReport;
        lock (syncRoot)
        {
            activeReportSpans.Remove(logEvent.SourcePath, out activeReport);
        }

        var spanId = activeReport?.SpanId ?? CreateSpanId("night_summary.report", logEvent, effectiveSessionId);
        var sessionId = effectiveSessionId ?? activeReport?.SessionId;
        return TryCreateSimpleSpan(
            logEvent,
            "night_summary.report",
            SpanEventKind.Stop,
            spanId,
            eventKind,
            sessionId,
            out span);
    }

    private static bool TryGetMetricName(NightSummaryLogEventKind kind, out string metricName)
    {
        metricName = kind switch
        {
            NightSummaryLogEventKind.SessionStarted => "night_summary_session_started_count",
            NightSummaryLogEventKind.SessionEnded => "night_summary_session_ended_count",
            NightSummaryLogEventKind.ReportGenerating => "night_summary_report_started_count",
            NightSummaryLogEventKind.ReportDelivered => "night_summary_report_delivered_count",
            NightSummaryLogEventKind.ReportFailed => "night_summary_report_failed_count",
            NightSummaryLogEventKind.AutoFocusCompleted => "night_summary_autofocus_completed_count",
            NightSummaryLogEventKind.MeridianFlip => "night_summary_meridian_flip_count",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(metricName);
    }

    private void ClearCorrelationState()
    {
        lock (syncRoot)
        {
            activeReportSpans.Clear();
            activeSessionIds.Clear();
        }
    }

    private static bool IsReportTerminal(NightSummaryLogEventKind kind) =>
        kind is NightSummaryLogEventKind.ReportDelivered or NightSummaryLogEventKind.ReportFailed;

    private static TelemetryRecord CreateTelemetryRecord(
        NightSummaryLogEvent logEvent,
        bool rawForwardingEnabled) =>
        TelemetryRecord.Log(
            logEvent.Timestamp,
            logEvent.Source,
            GetSeverity(logEvent),
            logEvent.Message,
            GetPriority(logEvent),
            CreateAttributes(logEvent, rawForwardingEnabled)) with
        {
            Name = "night_summary.log_event",
        };

    private static void PublishSafely(IAddonContext context, TelemetryRecord record)
    {
        try
        {
            context.Sink.TryPublish(record);
        }
        catch
        {
            // Night Summary telemetry must never interfere with NINA log handling.
        }
    }

    private static TelemetrySeverity GetSeverity(NightSummaryLogEvent logEvent) =>
        logEvent.Kind switch
        {
            NightSummaryLogEventKind.Warning => TelemetrySeverity.Warning,
            NightSummaryLogEventKind.Error => TelemetrySeverity.Error,
            NightSummaryLogEventKind.ReportFailed => TelemetrySeverity.Error,
            NightSummaryLogEventKind.TargetSchedulerGradingFailed => TelemetrySeverity.Error,
            _ => TelemetrySeverity.Information,
        };

    private static TelemetryPriority GetPriority(NightSummaryLogEvent logEvent) =>
        logEvent.Kind switch
        {
            NightSummaryLogEventKind.Warning => TelemetryPriority.Important,
            NightSummaryLogEventKind.Error => TelemetryPriority.Important,
            NightSummaryLogEventKind.ReportFailed => TelemetryPriority.Important,
            NightSummaryLogEventKind.TargetSchedulerGradingFailed => TelemetryPriority.Important,
            _ => TelemetryPriority.Normal,
        };

    private static Dictionary<string, object?> CreateAttributes(
        NightSummaryLogEvent logEvent,
        bool rawForwardingEnabled) =>
        CreateAttributes(logEvent, rawForwardingEnabled, ToEventKindName(logEvent.Kind), logEvent.SessionId);

    private static Dictionary<string, object?> CreateAttributes(
        NightSummaryLogEvent logEvent,
        bool rawForwardingEnabled,
        string eventKind,
        string? sessionId)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = "night-summary",
            ["source"] = logEvent.Source,
            ["source.file"] = logEvent.SourcePath,
            ["event.kind"] = eventKind,
            ["message"] = logEvent.Message,
        };

        if (TryGetWorkflowKind(logEvent.Kind, out var workflowKind))
        {
            attributes["workflow.kind"] = workflowKind;
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            attributes["session.id"] = sessionId;
        }

        if (rawForwardingEnabled)
        {
            attributes["raw.line"] = logEvent.OriginalLine;
        }

        return attributes;
    }

    private static bool TryGetWorkflowKind(NightSummaryLogEventKind kind, out string workflowKind)
    {
        workflowKind = kind switch
        {
            NightSummaryLogEventKind.SessionStarted or
            NightSummaryLogEventKind.SessionEnded or
            NightSummaryLogEventKind.ReportGenerating or
            NightSummaryLogEventKind.ReportDelivering or
            NightSummaryLogEventKind.ReportSaved or
            NightSummaryLogEventKind.ReportDelivered or
            NightSummaryLogEventKind.ReportFailed => "session_summary",
            NightSummaryLogEventKind.AutoFocusCompleted => "autofocus",
            NightSummaryLogEventKind.MeridianFlip => "meridian_flip",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(workflowKind);
    }

    private static string CreateSpanId(
        string spanName,
        NightSummaryLogEvent logEvent,
        string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"{spanName}|{logEvent.SourcePath}|{sessionId}";
        }

        var input = string.Join(
            "|",
            [
                spanName,
                logEvent.SourcePath,
                ToEventKindName(logEvent.Kind),
                logEvent.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                logEvent.Message,
            ]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"{spanName}|{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }

    private static string ToEventKindName(NightSummaryLogEventKind kind) =>
        kind switch
        {
            NightSummaryLogEventKind.SessionStarted => "session_started",
            NightSummaryLogEventKind.SessionEnded => "session_ended",
            NightSummaryLogEventKind.CameraInfoStored => "camera_info_stored",
            NightSummaryLogEventKind.EquipmentCaptured => "equipment_captured",
            NightSummaryLogEventKind.RoofOpen => "roof_open",
            NightSummaryLogEventKind.RoofClosed => "roof_closed",
            NightSummaryLogEventKind.AutoFocusCompleted => "autofocus_completed",
            NightSummaryLogEventKind.MeridianFlip => "meridian_flip",
            NightSummaryLogEventKind.TargetSchedulerGradingSynced => "ts_grading_synced",
            NightSummaryLogEventKind.TargetSchedulerGradingFailed => "ts_grading_failed",
            NightSummaryLogEventKind.ReportGenerating => "report_generating",
            NightSummaryLogEventKind.ReportDelivering => "report_delivering",
            NightSummaryLogEventKind.ReportSaved => "report_saved",
            NightSummaryLogEventKind.ReportDelivered => "report_delivered",
            NightSummaryLogEventKind.ReportFailed => "report_failed",
            NightSummaryLogEventKind.Warning => "warning",
            NightSummaryLogEventKind.Error => "error",
            _ => kind.ToString(),
        };

    private void ReportDegraded(IAddonContext context, string message) =>
        context.ReportHealth(
            Metadata.Id,
            "degraded",
            message,
            TelemetryPriority.Routine);

    private sealed record NightSummarySpanContext(string SpanId, string? SessionId);
}
