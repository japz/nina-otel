using Microsoft.Extensions.Logging;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using System.Diagnostics.Metrics;

namespace NinaOtel.Core.Pipeline;

public sealed class OtlpTelemetryExporter : ITelemetryExporter, IDisposable
{
    private const string LoggerCategoryName = "NinaOtel";
    private const string MeterName = "NinaOtel.Metrics";
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly Meter meter;
    private readonly OtlpMetricStateStore metricStateStore;
    private readonly OtlpPointInTimeMetricExporter pointInTimeMetricExporter;
    private readonly OtlpTraceExporter traceExporter;
    private readonly TrackingLogRecordExportProcessor logProcessor;
    private readonly TrackingMetricExporter metricExporter;
    private readonly BaseExportingMetricReader metricReader;
    private readonly MeterProvider meterProvider;
    private readonly int timeoutMilliseconds;

    public OtlpTelemetryExporter(NinaOtelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var otlpOptions = options.Otlp;
        timeoutMilliseconds = ToTimeoutMilliseconds(otlpOptions.Timeout);
        logProcessor = new TrackingLogRecordExportProcessor(
            new OtlpLogExporter(CreateExporterOptions(otlpOptions, "v1/logs")));
        loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = false;
                logging.ParseStateValues = true;
                logging.SetResourceBuilder(CreateResourceBuilder());
                logging.AddProcessor(logProcessor);
            });
        });
        logger = loggerFactory.CreateLogger(LoggerCategoryName);

        meter = new Meter(MeterName);
        metricStateStore = new OtlpMetricStateStore(meter);
        pointInTimeMetricExporter = new OtlpPointInTimeMetricExporter(otlpOptions);
        traceExporter = new OtlpTraceExporter(otlpOptions);
        metricExporter = new TrackingMetricExporter(
            new OtlpMetricExporter(CreateExporterOptions(otlpOptions, "v1/metrics")));
        metricReader = new BaseExportingMetricReader(metricExporter);
        meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(MeterName)
            .SetResourceBuilder(CreateResourceBuilder())
            .AddReader(metricReader)
            .Build();
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        if (records.Count == 0)
        {
            return;
        }

        logProcessor.ResetBatchStatus();
        metricExporter.ResetBatchStatus();
        await ExportPointInTimeMetricsAsync(records, cancellationToken).ConfigureAwait(false);
        var metricCount = metricStateStore.Apply(GetLiveMetricRecords(records));
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Signal != TelemetrySignal.Metric)
            {
                ExportAsLog(record);
            }
        }

        if (metricCount > 0 && !metricReader.Collect(timeoutMilliseconds))
        {
            throw new TelemetryExportException("OTLP metric reader failed to collect metrics.");
        }

        TelemetryExportException? batchFailure = null;
        var metricFailure = metricExporter.GetBatchFailure();
        if (metricFailure is not null)
        {
            batchFailure ??= metricFailure;
        }

        var logFailure = logProcessor.GetBatchFailure();
        if (logFailure is not null)
        {
            batchFailure ??= logFailure;
        }

        var traceFailure = await TryExportTracesAsync(records, cancellationToken).ConfigureAwait(false);
        if (traceFailure is not null)
        {
            batchFailure ??= traceFailure;
        }

        if (batchFailure is not null)
        {
            throw batchFailure;
        }

        return;
    }

    public void Dispose()
    {
        meterProvider.Dispose();
        traceExporter.Dispose();
        pointInTimeMetricExporter.Dispose();
        meter.Dispose();
        loggerFactory.Dispose();
    }

    private Task ExportPointInTimeMetricsAsync(
        IReadOnlyList<TelemetryRecord> records,
        CancellationToken cancellationToken) =>
        pointInTimeMetricExporter.ExportAsync(records, cancellationToken);

    private async Task<TelemetryExportException?> TryExportTracesAsync(
        IReadOnlyList<TelemetryRecord> records,
        CancellationToken cancellationToken)
    {
        try
        {
            await traceExporter.ExportAsync(records, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TelemetryExportException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            return new TelemetryExportException("OTLP trace exporter threw while exporting.", ex);
        }
    }

    private static IReadOnlyList<TelemetryRecord> GetLiveMetricRecords(IReadOnlyList<TelemetryRecord> records)
    {
        var liveRecords = new List<TelemetryRecord>(records.Count);
        foreach (var record in records)
        {
            if (record.Signal == TelemetrySignal.Metric &&
                NinaMetricCatalog.TryGetExportKind(record.Name, out var exportKind) &&
                exportKind == NinaMetricExportKind.LiveObservableGauge)
            {
                liveRecords.Add(record);
            }
        }

        return liveRecords;
    }

    private void ExportAsLog(TelemetryRecord record)
    {
        var payload = OtlpLogRecordMapper.Map(record);

        logger.Log(
            payload.Level,
            new EventId(0, record.Name),
            payload.Attributes,
            exception: null,
            static (state, _) => FormatLogState(state));
    }

    private static string FormatLogState(IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        for (var i = state.Count - 1; i >= 0; i--)
        {
            if (state[i].Key == "{OriginalFormat}")
            {
                return state[i].Value?.ToString() ?? string.Empty;
            }
        }

        return "NinaOtel telemetry record";
    }

    internal static OtlpExporterOptions CreateExporterOptions(OtlpOptions options, string httpSignalPath)
    {
        if (HasTlsConfiguration(options) && options.Protocol == OtlpProtocol.Grpc)
        {
            throw new NotSupportedException(
                "PEM TLS requires HTTP/protobuf because the OpenTelemetry .NET gRPC exporter does not use HttpClientFactory.");
        }

        var exporterOptions = new OtlpExporterOptions
        {
            Endpoint = CreateSignalEndpoint(options, httpSignalPath),
            Protocol = options.Protocol switch
            {
                OtlpProtocol.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.Grpc,
            },
            TimeoutMilliseconds = ToTimeoutMilliseconds(options.Timeout),
        };

        if (options.Headers.Count > 0)
        {
            exporterOptions.Headers = string.Join(",", options.Headers.Select(
                static header => $"{header.Key}={header.Value}"));
        }

        if (HasTlsConfiguration(options))
        {
            exporterOptions.HttpClientFactory = () => OtlpHttpClientFactory.CreateForSdkExporter(options);
        }

        return exporterOptions;
    }

    private static bool HasTlsConfiguration(OtlpOptions options) =>
        !string.IsNullOrWhiteSpace(options.Auth.CaCertificatePemPath) ||
        !string.IsNullOrWhiteSpace(options.Auth.ClientCertificatePemPath) ||
        !string.IsNullOrWhiteSpace(options.Auth.ClientPrivateKeyPemPath);

    internal static Uri CreateSignalEndpoint(OtlpOptions options, string httpSignalPath)
    {
        if (options.Protocol != OtlpProtocol.HttpProtobuf)
        {
            return options.Endpoint;
        }

        var builder = new UriBuilder(options.Endpoint);
        var path = builder.Path.TrimEnd('/');
        path = StripKnownOtlpSignalPath(path);
        builder.Path = string.IsNullOrEmpty(path)
            ? httpSignalPath
            : $"{path}/{httpSignalPath}";
        return builder.Uri;
    }

    private static string StripKnownOtlpSignalPath(string path)
    {
        var knownSignalPaths = new[] { "/v1/logs", "/v1/metrics", "/v1/traces" };
        foreach (var knownSignalPath in knownSignalPaths)
        {
            if (path.EndsWith(knownSignalPath, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^knownSignalPath.Length];
            }
        }

        return path;
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 1;
        }

        return timeout.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    private static ResourceBuilder CreateResourceBuilder() =>
        ResourceBuilder.CreateDefault()
            .AddService(serviceName: "NinaOtel")
            .AddAttributes(
            [
                new KeyValuePair<string, object>("service.namespace", "nina"),
                new KeyValuePair<string, object>("telemetry.source", "ninaotel"),
            ]);

    private sealed class TrackingLogRecordExportProcessor(BaseExporter<LogRecord> exporter)
        : BaseExportProcessor<LogRecord>(exporter)
    {
        private readonly object syncRoot = new();
        private TelemetryExportException? batchFailure;

        public void ResetBatchStatus()
        {
            lock (syncRoot)
            {
                batchFailure = null;
            }
        }

        public TelemetryExportException? GetBatchFailure()
        {
            lock (syncRoot)
            {
                return batchFailure;
            }
        }

        protected override void OnExport(LogRecord data)
        {
            lock (syncRoot)
            {
                try
                {
                    var result = exporter.Export(new Batch<LogRecord>(data));
                    if (result == ExportResult.Failure)
                    {
                        batchFailure ??= new TelemetryExportException("OTLP log exporter returned failure.");
                    }
                }
                catch (Exception ex)
                {
                    batchFailure ??= new TelemetryExportException("OTLP log exporter threw while exporting.", ex);
                }
            }
        }
    }

    private sealed class TrackingMetricExporter(BaseExporter<Metric> exporter) : BaseExporter<Metric>
    {
        private readonly object syncRoot = new();
        private TelemetryExportException? batchFailure;

        public void ResetBatchStatus()
        {
            lock (syncRoot)
            {
                batchFailure = null;
            }
        }

        public TelemetryExportException? GetBatchFailure()
        {
            lock (syncRoot)
            {
                return batchFailure;
            }
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            lock (syncRoot)
            {
                try
                {
                    var result = exporter.Export(batch);
                    if (result == ExportResult.Failure)
                    {
                        batchFailure ??= new TelemetryExportException("OTLP metric exporter returned failure.");
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    batchFailure ??= new TelemetryExportException("OTLP metric exporter threw while exporting.", ex);
                    return ExportResult.Failure;
                }
            }
        }

        protected override bool OnForceFlush(int timeoutMilliseconds) =>
            exporter.ForceFlush(timeoutMilliseconds);

        protected override bool OnShutdown(int timeoutMilliseconds) =>
            exporter.Shutdown(timeoutMilliseconds);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                exporter.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
