using Microsoft.Extensions.Logging;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
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
        metricExporter = new TrackingMetricExporter(
            new OtlpMetricExporter(CreateExporterOptions(otlpOptions, "v1/metrics")));
        metricReader = new BaseExportingMetricReader(metricExporter);
        meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(MeterName)
            .SetResourceBuilder(CreateResourceBuilder())
            .AddReader(metricReader)
            .Build();
    }

    public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        logProcessor.ResetBatchStatus();
        metricExporter.ResetBatchStatus();
        var metricCount = metricStateStore.Apply(records);
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

        var metricFailure = metricExporter.GetBatchFailure();
        if (metricFailure is not null)
        {
            throw metricFailure;
        }

        var logFailure = logProcessor.GetBatchFailure();
        if (logFailure is not null)
        {
            throw logFailure;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        meterProvider.Dispose();
        meter.Dispose();
        loggerFactory.Dispose();
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

    private static OtlpExporterOptions CreateExporterOptions(OtlpOptions options, string httpSignalPath)
    {
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

        return exporterOptions;
    }

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
