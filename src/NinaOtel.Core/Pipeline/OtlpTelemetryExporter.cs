using Microsoft.Extensions.Logging;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace NinaOtel.Core.Pipeline;

public sealed class OtlpTelemetryExporter : ITelemetryExporter, IDisposable
{
    private const string LoggerCategoryName = "NinaOtel";
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TrackingLogRecordExportProcessor processor;

    public OtlpTelemetryExporter(NinaOtelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var otlpOptions = options.Otlp;
        processor = new TrackingLogRecordExportProcessor(new OtlpLogExporter(CreateExporterOptions(otlpOptions)));
        loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = false;
                logging.ParseStateValues = true;
                logging.SetResourceBuilder(CreateResourceBuilder());
                logging.AddProcessor(processor);
            });
        });
        logger = loggerFactory.CreateLogger(LoggerCategoryName);
    }

    public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        processor.ResetBatchStatus();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportAsLog(record);
        }

        var failure = processor.GetBatchFailure();
        if (failure is not null)
        {
            throw failure;
        }

        return Task.CompletedTask;
    }

    public void Dispose() => loggerFactory.Dispose();

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

    private static OtlpExporterOptions CreateExporterOptions(OtlpOptions options)
    {
        var exporterOptions = new OtlpExporterOptions
        {
            Endpoint = CreateLogEndpoint(options),
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

    private static Uri CreateLogEndpoint(OtlpOptions options)
    {
        if (options.Protocol != OtlpProtocol.HttpProtobuf)
        {
            return options.Endpoint;
        }

        var builder = new UriBuilder(options.Endpoint);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{path}/v1/logs";
        return builder.Uri;
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
}
