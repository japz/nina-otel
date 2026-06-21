using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace NinaOtel.Addons.OnStepX;

internal interface IOnStepXConnectionFactory
{
    Task<IOnStepXConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken);
}

internal interface IOnStepXConnection : IAsyncDisposable
{
    Task<string> SendCommandAsync(
        string command,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken);
}

public sealed class OnStepXTelemetryAddon : ITelemetryAddon
{
    private const string HostSetting = "Host";
    private const string PortSetting = "Port";
    private const string PollingIntervalSecondsSetting = "PollingIntervalSeconds";
    private const string CommandTimeoutMillisecondsSetting = "CommandTimeoutMilliseconds";
    private const int DefaultPort = 9999;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    private readonly IOnStepXConnectionFactory connectionFactory;
    private readonly TimeSpan defaultPollInterval;
    private readonly TimeSpan stopTimeout;
    private readonly object syncRoot = new();
    private CancellationTokenSource? workerCancellation;
    private Task? worker;

    public OnStepXTelemetryAddon()
        : this(new TcpOnStepXConnectionFactory(), DefaultPollInterval, DefaultStopTimeout)
    {
    }

    internal OnStepXTelemetryAddon(IOnStepXConnectionFactory connectionFactory, TimeSpan pollInterval)
        : this(connectionFactory, pollInterval, DefaultStopTimeout)
    {
    }

    internal OnStepXTelemetryAddon(
        IOnStepXConnectionFactory connectionFactory,
        TimeSpan pollInterval,
        TimeSpan stopTimeout)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        defaultPollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        this.stopTimeout = stopTimeout > TimeSpan.Zero ? stopTimeout : DefaultStopTimeout;
    }

    public AddonMetadata Metadata { get; } = new(
        "onstepx",
        "OnStepX",
        new Version(0, 1, 0),
        "OnStepX");

    public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

    public async Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var options = CreateOptions(context.Configuration);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            await StopCurrentWorkerAsync(cancellationToken).ConfigureAwait(false);
            context.ReportHealth(
                Metadata.Id,
                "waiting",
                "Configure the OnStepX host to collect controller telemetry.",
                TelemetryPriority.Routine);
            return;
        }

        if (!options.IsValid)
        {
            await StopCurrentWorkerAsync(cancellationToken).ConfigureAwait(false);
            context.ReportHealth(
                Metadata.Id,
                "waiting",
                options.ValidationMessage,
                TelemetryPriority.Routine);
            return;
        }

        await StopCurrentWorkerAsync(cancellationToken).ConfigureAwait(false);

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.ShutdownToken);
        var nextWorker = Task.Run(
            () => RunPollLoopAsync(context, options, cancellation.Token),
            CancellationToken.None);

        lock (syncRoot)
        {
            workerCancellation = cancellation;
            worker = nextWorker;
        }

        context.ReportHealth(Metadata.Id, "connecting", "Connecting to OnStepX controller.", TelemetryPriority.Routine);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopCurrentWorkerAsync(cancellationToken);

    private async Task StopCurrentWorkerAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task? workerToStop;
        lock (syncRoot)
        {
            cancellation = workerCancellation;
            workerCancellation = null;
            workerToStop = worker;
            worker = null;
        }

        if (workerToStop is null)
        {
            cancellation?.Dispose();
            return;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
        }

        using var timeout = new CancellationTokenSource(stopTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await workerToStop.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task RunPollLoopAsync(
        IAddonContext context,
        OnStepXOptions options,
        CancellationToken cancellationToken)
    {
        var reportedLoss = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await connectionFactory.ConnectAsync(
                    options.Host,
                    options.Port,
                    DefaultConnectTimeout,
                    cancellationToken).ConfigureAwait(false);

                context.ReportHealth(
                    Metadata.Id,
                    "connected",
                    $"Connected to OnStepX at {options.Host}:{options.Port}.",
                    TelemetryPriority.Routine);
                if (reportedLoss)
                {
                    PublishLog(
                        context,
                        "onstepx.communication_recovered",
                        TelemetrySeverity.Information,
                        "OnStepX communication recovered.",
                        TelemetryPriority.Normal,
                        options,
                        attributes: null);
                    reportedLoss = false;
                }

                await PublishIdentityAsync(context, connection, options, cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    await PollOnceAsync(context, connection, options, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                context.ReportHealth(
                    Metadata.Id,
                    "disconnected",
                    $"OnStepX connection failed: {ex.Message}",
                    TelemetryPriority.Routine);
                if (!reportedLoss)
                {
                    PublishLog(
                        context,
                        "onstepx.communication_lost",
                        TelemetrySeverity.Warning,
                        $"OnStepX communication failed: {ex.Message}",
                        TelemetryPriority.Important,
                        options,
                        attributes: null);
                    reportedLoss = true;
                }
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PublishIdentityAsync(
        IAddonContext context,
        IOnStepXConnection connection,
        OnStepXOptions options,
        CancellationToken cancellationToken)
    {
        var productName = NormalizeReply(await SendOptionalCommandAsync(
            connection,
            ":GVP#",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false));
        var firmwareVersion = NormalizeReply(await SendOptionalCommandAsync(
            connection,
            ":GVN#",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false));

        if (string.IsNullOrWhiteSpace(productName) && string.IsNullOrWhiteSpace(firmwareVersion))
        {
            return;
        }

        var attributes = CreateCommonAttributes(options);
        if (!string.IsNullOrWhiteSpace(productName))
        {
            attributes["product.name"] = productName;
        }

        if (!string.IsNullOrWhiteSpace(firmwareVersion))
        {
            attributes["firmware.version"] = firmwareVersion;
        }

        context.Sink.TryPublish(TelemetryRecord.Log(
            context.TimeProvider.GetUtcNow(),
            "onstepx",
            TelemetrySeverity.Information,
            "OnStepX controller identity.",
            TelemetryPriority.Routine,
            attributes) with
        {
            Name = "onstepx.controller_identity",
        });
    }

    private async Task PollOnceAsync(
        IAddonContext context,
        IOnStepXConnection connection,
        OnStepXOptions options,
        CancellationToken cancellationToken)
    {
        var statusReply = await SendRequiredCommandAsync(
            connection,
            ":GU#",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var status = NormalizeReply(statusReply);
        if (!string.IsNullOrWhiteSpace(status))
        {
            PublishStatus(context, options, status, statusReply);
        }

        var trackingRateReply = await SendOptionalCommandAsync(
            connection,
            ":GT#",
            options.CommandTimeout,
            cancellationToken,
            treatUnsupportedReplyAsNull: false).ConfigureAwait(false);
        var trackingRate = NormalizeReply(trackingRateReply);
        if (TryParseDoubleReply(trackingRate, allowBareNumericReply: true, out var trackingRateValue))
        {
            PublishMetric(context, options, "onstepx_tracking_rate_hz", trackingRateValue, command: ":GT#", rawReply: trackingRateReply);
        }

        var altitudeReply = await SendOptionalCommandAsync(
            connection,
            ":GAH#",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var altitude = NormalizeReply(altitudeReply);
        if (TryParseSexagesimalDegrees(altitude, out var altitudeValue))
        {
            PublishMetric(context, options, "mount_altitude", altitudeValue, command: ":GAH#", rawReply: altitudeReply);
        }

        var azimuthReply = await SendOptionalCommandAsync(
            connection,
            ":GZH#",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var azimuth = NormalizeReply(azimuthReply);
        if (TryParseSexagesimalDegrees(azimuth, out var azimuthValue))
        {
            PublishMetric(context, options, "mount_azimuth", azimuthValue, command: ":GZH#", rawReply: azimuthReply);
        }
    }

    private static async Task<string?> SendOptionalCommandAsync(
        IOnStepXConnection connection,
        string command,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken,
        bool treatUnsupportedReplyAsNull = true)
    {
        try
        {
            var reply = await connection.SendCommandAsync(command, commandTimeout, cancellationToken).ConfigureAwait(false);
            var normalized = NormalizeReply(reply);
            return treatUnsupportedReplyAsNull && IsUnsupportedReply(normalized) ? null : reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> SendRequiredCommandAsync(
        IOnStepXConnection connection,
        string command,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        var reply = await connection.SendCommandAsync(command, commandTimeout, cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeReply(reply);
        if (string.IsNullOrWhiteSpace(normalized) || IsUnsupportedReply(normalized))
        {
            throw new InvalidOperationException($"OnStepX command {command} returned no usable data.");
        }

        return reply;
    }

    private void PublishStatus(IAddonContext context, OnStepXOptions options, string status, string rawReply)
    {
        var statusAttributes = CreateStatusAttributes(options, status, rawReply, ":GU#");
        var trackingEnabled = status.Contains('n', StringComparison.Ordinal) ? 0d : 1d;
        var gotoActive = status.Contains('N', StringComparison.Ordinal) ? 0d : 1d;
        var parked = status.Contains('P', StringComparison.Ordinal) ? 1d : 0d;
        var parking = status.Contains('I', StringComparison.Ordinal) ? 1d : 0d;
        var parkFailed = status.Contains('F', StringComparison.Ordinal) ? 1d : 0d;
        var home = status.Contains('H', StringComparison.Ordinal) ? 1d : 0d;
        var homing = status.Contains('h', StringComparison.Ordinal) ? 1d : 0d;
        var errorCode = TryGetStatusErrorCode(status, out var code) ? code : 0;

        PublishMetric(context, "onstepx_tracking_enabled", trackingEnabled, statusAttributes);
        PublishMetric(context, "onstepx_goto_active", gotoActive, statusAttributes);
        PublishMetric(context, "onstepx_parked", parked, statusAttributes);
        PublishMetric(context, "onstepx_parking", parking, statusAttributes);
        PublishMetric(context, "onstepx_park_failed", parkFailed, statusAttributes);
        PublishMetric(context, "onstepx_home", home, statusAttributes);
        PublishMetric(context, "onstepx_homing", homing, statusAttributes);
        PublishMetric(context, "onstepx_error_code", errorCode, statusAttributes);

        if (errorCode > 0)
        {
            PublishLog(
                context,
                "onstepx.limit_error",
                TelemetrySeverity.Warning,
                $"OnStepX status reported error code {errorCode}.",
                TelemetryPriority.Important,
                options,
                statusAttributes);
        }
    }

    private static bool TryGetStatusErrorCode(string status, out int code)
    {
        for (var index = status.Length - 1; index >= 0; index--)
        {
            if (char.IsDigit(status[index]))
            {
                code = status[index] - '0';
                return true;
            }
        }

        code = 0;
        return false;
    }

    private static Dictionary<string, object?> CreateStatusAttributes(
        OnStepXOptions options,
        string status,
        string rawReply,
        string command)
    {
        var attributes = CreateCommonAttributes(options);
        attributes["command"] = command;
        if (options.RawForwardingEnabled)
        {
            attributes["raw.reply"] = rawReply;
        }

        var mountType = FirstMatchingChar(status, ['E', 'K', 'A', 'L']);
        if (mountType.HasValue)
        {
            attributes["mount.type"] = mountType.Value.ToString();
        }

        var pierSide = FirstMatchingChar(status, ['o', 'T', 'W']);
        if (pierSide.HasValue)
        {
            attributes["pier.side"] = pierSide.Value.ToString();
        }

        return attributes;
    }

    private static char? FirstMatchingChar(string value, char[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void PublishMetric(
        IAddonContext context,
        OnStepXOptions options,
        string metricName,
        double value,
        string command,
        string? rawReply)
    {
        var attributes = CreateCommonAttributes(options);
        attributes["command"] = command;
        if (options.RawForwardingEnabled && !string.IsNullOrWhiteSpace(rawReply))
        {
            attributes["raw.reply"] = rawReply;
        }

        PublishMetric(context, metricName, value, attributes);
    }

    private static void PublishMetric(
        IAddonContext context,
        string metricName,
        double value,
        IReadOnlyDictionary<string, object?> attributes)
    {
        context.Sink.TryPublish(TelemetryRecord.Metric(
            context.TimeProvider.GetUtcNow(),
            "onstepx",
            metricName,
            value,
            TelemetryPriority.Routine,
            attributes));
    }

    private static void PublishLog(
        IAddonContext context,
        string name,
        TelemetrySeverity severity,
        string body,
        TelemetryPriority priority,
        OnStepXOptions options,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        var mergedAttributes = CreateCommonAttributes(options);
        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

        context.Sink.TryPublish(TelemetryRecord.Log(
            context.TimeProvider.GetUtcNow(),
            "onstepx",
            severity,
            body,
            priority,
            mergedAttributes) with
        {
            Name = name,
        });
    }

    private static Dictionary<string, object?> CreateCommonAttributes(OnStepXOptions options) =>
        new()
        {
            ["addon.id"] = "onstepx",
            ["source"] = "onstepx",
            ["mount_name"] = "OnStepX",
            ["onstepx.host"] = options.Host,
            ["onstepx.port"] = options.Port,
        };

    private OnStepXOptions CreateOptions(AddonConfiguration configuration)
    {
        var host = GetSetting(configuration, HostSetting);
        var portSetting = GetSetting(configuration, PortSetting);
        var pollIntervalSetting = GetSetting(configuration, PollingIntervalSecondsSetting);
        var commandTimeoutSetting = GetSetting(configuration, CommandTimeoutMillisecondsSetting);

        if (!string.IsNullOrWhiteSpace(host) && !IsValidHost(host))
        {
            return OnStepXOptions.Invalid(
                host,
                DefaultPort,
                defaultPollInterval,
                DefaultCommandTimeout,
                "OnStepX host must be a DNS name or IP address without a scheme or port.");
        }

        if (!TryCreatePort(portSetting, out var port))
        {
            return OnStepXOptions.Invalid(host, DefaultPort, defaultPollInterval, DefaultCommandTimeout, "OnStepX port must be between 1 and 65535.");
        }

        if (!TryCreatePollInterval(pollIntervalSetting, out var pollInterval))
        {
            return OnStepXOptions.Invalid(host, port, defaultPollInterval, DefaultCommandTimeout, "OnStepX poll seconds must be greater than 0.");
        }

        if (!TryCreateCommandTimeout(commandTimeoutSetting, out var commandTimeout))
        {
            return OnStepXOptions.Invalid(host, port, pollInterval, DefaultCommandTimeout, "OnStepX timeout ms must be greater than 0.");
        }

        return new OnStepXOptions(
            host,
            port,
            pollInterval,
            commandTimeout,
            configuration.RawForwardingEnabled,
            IsValid: true,
            ValidationMessage: string.Empty);
    }

    private bool TryCreatePort(string setting, out int port)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            port = DefaultPort;
            return true;
        }

        if (!TryParsePositiveInt(setting, out port))
        {
            return false;
        }

        if (port is < 1 or > 65535)
        {
            return false;
        }

        return true;
    }

    private bool TryCreatePollInterval(string setting, out TimeSpan pollInterval)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            pollInterval = defaultPollInterval;
            return true;
        }

        if (TryParsePositiveDouble(setting, out var pollSeconds))
        {
            try
            {
                pollInterval = TimeSpan.FromSeconds(pollSeconds);
                return pollInterval > TimeSpan.Zero;
            }
            catch (OverflowException)
            {
            }
        }

        pollInterval = defaultPollInterval;
        return false;
    }

    private static bool TryCreateCommandTimeout(string setting, out TimeSpan commandTimeout)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            commandTimeout = DefaultCommandTimeout;
            return true;
        }

        if (TryParsePositiveInt(setting, out var timeoutMilliseconds))
        {
            commandTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
            return true;
        }

        commandTimeout = DefaultCommandTimeout;
        return false;
    }

    private static string GetSetting(AddonConfiguration configuration, string key) =>
        configuration.Settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static bool IsValidHost(string host) =>
        !host.Contains("://", StringComparison.Ordinal) &&
        !host.Contains('/', StringComparison.Ordinal) &&
        Uri.CheckHostName(host) != UriHostNameType.Unknown;

    private static bool TryParsePositiveInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    private static bool TryParsePositiveDouble(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
        parsed > 0 &&
        double.IsFinite(parsed);

    private static string NormalizeReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return string.Empty;
        }

        return reply.Trim().TrimEnd('#').Trim();
    }

    private static bool IsUnsupportedReply(string reply) => reply is "0" or "1";

    private static bool TryParseDoubleReply(string reply, bool allowBareNumericReply, out double value)
    {
        value = 0;
        return (allowBareNumericReply || !IsUnsupportedReply(reply)) &&
            double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value);
    }

    private static bool TryParseSexagesimalDegrees(string reply, out double degrees)
    {
        degrees = 0;
        if (string.IsNullOrWhiteSpace(reply) || IsUnsupportedReply(reply))
        {
            return false;
        }

        var normalized = reply.Trim();
        var sign = 1d;
        if (normalized[0] == '+')
        {
            normalized = normalized[1..];
        }
        else if (normalized[0] == '-')
        {
            sign = -1d;
            normalized = normalized[1..];
        }

        var degreeIndex = normalized.IndexOf('*', StringComparison.Ordinal);
        var minuteIndex = normalized.IndexOf('\'', StringComparison.Ordinal);
        if (degreeIndex <= 0 || minuteIndex <= degreeIndex + 1)
        {
            return false;
        }

        var degreePart = normalized[..degreeIndex];
        var minutePart = normalized[(degreeIndex + 1)..minuteIndex];
        var secondsPart = normalized[(minuteIndex + 1)..].TrimEnd('"');

        if (!double.TryParse(degreePart, NumberStyles.Float, CultureInfo.InvariantCulture, out var wholeDegrees) ||
            !double.TryParse(minutePart, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(secondsPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        degrees = sign * (wholeDegrees + (minutes / 60d) + (seconds / 3600d));
        return double.IsFinite(degrees);
    }

    private sealed record OnStepXOptions(
        string Host,
        int Port,
        TimeSpan PollInterval,
        TimeSpan CommandTimeout,
        bool RawForwardingEnabled,
        bool IsValid,
        string ValidationMessage)
    {
        public static OnStepXOptions Invalid(
            string host,
            int port,
            TimeSpan pollInterval,
            TimeSpan commandTimeout,
            string message) =>
            new(host, port, pollInterval, commandTimeout, RawForwardingEnabled: false, IsValid: false, message);
    }

    private sealed class TcpOnStepXConnectionFactory : IOnStepXConnectionFactory
    {
        public async Task<IOnStepXConnection> ConnectAsync(
            string host,
            int port,
            TimeSpan connectTimeout,
            CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(connectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return new TcpOnStepXConnection(client);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new TimeoutException($"Timed out connecting to {host}:{port}.");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private sealed class TcpOnStepXConnection : IOnStepXConnection
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        public TcpOnStepXConnection(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
        }

        public async Task<string> SendCommandAsync(
            string command,
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(commandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var payload = Encoding.ASCII.GetBytes(command);
            await stream.WriteAsync(payload, linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);

            var buffer = new byte[1];
            var reply = new StringBuilder();
            while (!linked.Token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("OnStepX TCP connection closed.");
                }

                var character = (char)buffer[0];
                reply.Append(character);
                if (character == '#')
                {
                    return reply.ToString();
                }
            }

            throw new TimeoutException($"Timed out waiting for OnStepX reply to {command}.");
        }

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
