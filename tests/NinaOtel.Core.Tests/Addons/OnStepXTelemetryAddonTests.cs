using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Addons.OnStepX;
using NinaOtel.Core.Addons;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class OnStepXTelemetryAddonTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StartAsync_WhenHostIsMissing_ReportsWaitingAndDoesNotConnect()
    {
        var sink = new RecordingSink();
        var factory = new FakeOnStepXConnectionFactory();
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        var context = CreateContext(sink, CancellationToken.None);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "onstepx") &&
            Equals(record.Attributes["status"], "waiting")).Subject;
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("OnStepX host");
        factory.Attempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Port", "not-a-port", "OnStepX port must be between 1 and 65535.")]
    [InlineData("Port", "0", "OnStepX port must be between 1 and 65535.")]
    [InlineData("PollingIntervalSeconds", "not-a-number", "OnStepX poll seconds must be greater than 0.")]
    [InlineData("PollingIntervalSeconds", "0", "OnStepX poll seconds must be greater than 0.")]
    [InlineData("PollingIntervalSeconds", "1E100", "OnStepX poll seconds must be greater than 0.")]
    [InlineData("CommandTimeoutMilliseconds", "not-a-number", "OnStepX timeout ms must be greater than 0.")]
    [InlineData("CommandTimeoutMilliseconds", "0", "OnStepX timeout ms must be greater than 0.")]
    public async Task StartAsync_WhenConnectionSettingIsInvalid_ReportsWaitingAndDoesNotConnect(
        string settingName,
        string settingValue,
        string expectedMessage)
    {
        var sink = new RecordingSink();
        var factory = new FakeOnStepXConnectionFactory();
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        var context = CreateContextWithSetting(sink, settingName, settingValue);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "onstepx") &&
            Equals(record.Attributes["status"], "waiting")).Subject;
        health.Attributes["message"].Should().Be(expectedMessage);
        factory.Attempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://mount.local")]
    [InlineData("mount.local:9999")]
    public async Task StartAsync_WhenHostIsInvalid_ReportsWaitingAndDoesNotConnect(string host)
    {
        var sink = new RecordingSink();
        var factory = new FakeOnStepXConnectionFactory();
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        var context = CreateContext(sink, CancellationToken.None, host: host);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var health = sink.Records.Should().ContainSingle(record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "onstepx") &&
            Equals(record.Attributes["status"], "waiting")).Subject;
        health.Attributes["message"].Should().Be("OnStepX host must be a DNS name or IP address without a scheme or port.");
        factory.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Poller_WhenConfigured_PublishesStatusPositionAndTrackingMetrics()
    {
        var sink = new RecordingSink();
        var connection = new ScriptedOnStepXConnection(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [":GVP#"] = "OnStepX#",
            [":GVN#"] = "10.25#",
            [":GU#"] = "NpEo123#",
            [":GT#"] = "1.002730#",
            [":GAH#"] = "+45*30'00.000#",
            [":GZH#"] = "123*15'00.000#",
        });
        var factory = FakeOnStepXConnectionFactory.Returning(connection);
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            host: "mount.local",
            port: "9999",
            pollingIntervalSeconds: "1",
            commandTimeoutMilliseconds: "500",
            rawForwardingEnabled: true);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var tracking = await WaitForRecordAsync(sink, record => record.Name == "onstepx_tracking_enabled");
        tracking.NumericValue.Should().Be(1);
        tracking.Source.Should().Be("onstepx");
        tracking.Attributes["onstepx.host"].Should().Be("mount.local");
        tracking.Attributes["onstepx.port"].Should().Be(9999);
        tracking.Attributes["raw.reply"].Should().Be("NpEo123#");

        var gotoActive = await WaitForRecordAsync(sink, record => record.Name == "onstepx_goto_active");
        gotoActive.NumericValue.Should().Be(0);

        var errorCode = await WaitForRecordAsync(sink, record => record.Name == "onstepx_error_code");
        errorCode.NumericValue.Should().Be(3);

        var trackingRate = await WaitForRecordAsync(sink, record => record.Name == "onstepx_tracking_rate_hz");
        trackingRate.NumericValue.Should().BeApproximately(1.002730, 0.000001);
        trackingRate.Attributes["raw.reply"].Should().Be("1.002730#");

        var altitude = await WaitForRecordAsync(sink, record => record.Name == "mount_altitude");
        altitude.NumericValue.Should().BeApproximately(45.5, 0.000001);
        altitude.Attributes["mount_name"].Should().Be("OnStepX");
        altitude.Attributes["raw.reply"].Should().Be("+45*30'00.000#");

        var azimuth = await WaitForRecordAsync(sink, record => record.Name == "mount_azimuth");
        azimuth.NumericValue.Should().BeApproximately(123.25, 0.000001);

        sink.Records.Should().Contain(record =>
            record.Name == "onstepx.controller_identity" &&
            Equals(record.Attributes["product.name"], "OnStepX") &&
            Equals(record.Attributes["firmware.version"], "10.25"));
        factory.Attempts.Should().ContainSingle().Which.Should().Be(("mount.local", 9999, TimeSpan.FromSeconds(2)));
        connection.Commands.Should().Contain([":GVP#", ":GVN#", ":GU#", ":GT#", ":GAH#", ":GZH#"]);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Poller_WhenTrackingRateIsZero_PublishesZeroTrackingRateMetric()
    {
        var sink = new RecordingSink();
        var connection = new ScriptedOnStepXConnection(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [":GU#"] = "nN0#",
            [":GT#"] = "0#",
        });
        var factory = FakeOnStepXConnectionFactory.Returning(connection);
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, host: "mount.local", rawForwardingEnabled: true);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var trackingRate = await WaitForRecordAsync(sink, record => record.Name == "onstepx_tracking_rate_hz");
        trackingRate.NumericValue.Should().Be(0);
        trackingRate.Attributes["raw.reply"].Should().Be("0#");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Poller_WhenTcpReplyIsSplitAfterFirstDigit_ReadsFullHashTerminatedReply()
    {
        var sink = new RecordingSink();
        using var shutdown = new CancellationTokenSource();
        await using var server = await LoopbackOnStepXServer.StartAsync(new Dictionary<string, Func<NetworkStream, CancellationToken, Task>>(StringComparer.Ordinal)
        {
            [":GVP#"] = (stream, token) => WriteReplyAsync(stream, "OnStepX#", token),
            [":GVN#"] = (stream, token) => WriteReplyAsync(stream, "10.25#", token),
            [":GU#"] = (stream, token) => WriteReplyAsync(stream, "NpEo123#", token),
            [":GT#"] = async (stream, token) =>
            {
                await WriteReplyAsync(stream, "1", token);
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                await WriteReplyAsync(stream, ".002730#", token);
            },
            [":GAH#"] = (stream, token) => WriteReplyAsync(stream, "+45*30'00.000#", token),
            [":GZH#"] = (stream, token) => WriteReplyAsync(stream, "123*15'00.000#", token),
        });
        var addon = new OnStepXTelemetryAddon();
        var context = CreateContext(
            sink,
            shutdown.Token,
            host: IPAddress.Loopback.ToString(),
            port: server.Port.ToString(),
            pollingIntervalSeconds: "60",
            commandTimeoutMilliseconds: "500",
            rawForwardingEnabled: true);

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var trackingRate = await WaitForRecordAsync(sink, record => record.Name == "onstepx_tracking_rate_hz");
        trackingRate.NumericValue.Should().BeApproximately(1.002730, 0.000001);
        trackingRate.Attributes["raw.reply"].Should().Be("1.002730#");

        var altitude = await WaitForRecordAsync(sink, record => record.Name == "mount_altitude");
        altitude.NumericValue.Should().BeApproximately(45.5, 0.000001);

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Poller_WhenConnectionFails_ReportsDisconnectedWithoutThrowing()
    {
        var sink = new RecordingSink();
        var factory = FakeOnStepXConnectionFactory.Throwing(new TimeoutException("connect timed out"));
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(
            sink,
            shutdown.Token,
            host: "mount.local",
            port: "9999",
            commandTimeoutMilliseconds: "100");

        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        var health = await WaitForRecordAsync(sink, record =>
            record.Signal == TelemetrySignal.Health &&
            Equals(record.Attributes["addon.id"], "onstepx") &&
            Equals(record.Attributes["status"], "disconnected"));
        health.Priority.Should().Be(TelemetryPriority.Routine);
        health.Attributes["message"].Should().BeOfType<string>().Which.Should().Contain("connect timed out");

        await addon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WhenCallerTokenIsCanceled_DoesNotThrowIntoHost()
    {
        var sink = new RecordingSink();
        var connection = new ScriptedOnStepXConnection(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [":GU#"] = "nN0#",
        });
        var factory = FakeOnStepXConnectionFactory.Returning(connection);
        var addon = new OnStepXTelemetryAddon(factory, PollInterval);
        using var shutdown = new CancellationTokenSource();
        var context = CreateContext(sink, shutdown.Token, host: "mount.local");
        await addon.StartAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var exception = await Record.ExceptionAsync(() => addon.StopAsync(canceled.Token));

        exception.Should().BeNull();
    }

    private static AddonContext CreateContext(
        ITelemetrySink sink,
        CancellationToken shutdownToken,
        string? host = null,
        string? port = null,
        string? pollingIntervalSeconds = null,
        string? commandTimeoutMilliseconds = null,
        bool rawForwardingEnabled = false)
    {
        var settings = new Dictionary<string, string>();
        AddSetting(settings, "Host", host);
        AddSetting(settings, "Port", port);
        AddSetting(settings, "PollingIntervalSeconds", pollingIntervalSeconds);
        AddSetting(settings, "CommandTimeoutMilliseconds", commandTimeoutMilliseconds);

        return new AddonContext(
            sink,
            TimeProvider.System,
            shutdownToken,
            new AddonConfiguration(rawForwardingEnabled: rawForwardingEnabled, settings: settings));
    }

    private static AddonContext CreateContextWithSetting(
        ITelemetrySink sink,
        string settingName,
        string settingValue)
    {
        var settings = new Dictionary<string, string>
        {
            ["Host"] = "mount.local",
            [settingName] = settingValue,
        };

        return new AddonContext(
            sink,
            TimeProvider.System,
            CancellationToken.None,
            new AddonConfiguration(rawForwardingEnabled: false, settings: settings));
    }

    private static void AddSetting(Dictionary<string, string> settings, string name, string? value)
    {
        if (value is not null)
        {
            settings[name] = value;
        }
    }

    private static async Task<TelemetryRecord> WaitForRecordAsync(
        RecordingSink sink,
        Func<TelemetryRecord, bool> predicate)
    {
        var stopAt = DateTimeOffset.UtcNow + WaitTimeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            var record = sink.Records.FirstOrDefault(predicate);
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException("Expected OnStepX telemetry record was not published before timeout.");
    }

    private sealed class FakeOnStepXConnectionFactory : IOnStepXConnectionFactory
    {
        private readonly Func<CancellationToken, Task<IOnStepXConnection>> connect;
        private readonly List<(string Host, int Port, TimeSpan ConnectTimeout)> attempts = [];

        public FakeOnStepXConnectionFactory()
            : this(_ => throw new InvalidOperationException("Unexpected OnStepX connection attempt."))
        {
        }

        private FakeOnStepXConnectionFactory(Func<CancellationToken, Task<IOnStepXConnection>> connect)
        {
            this.connect = connect;
        }

        public IReadOnlyList<(string Host, int Port, TimeSpan ConnectTimeout)> Attempts => attempts;

        public static FakeOnStepXConnectionFactory Returning(IOnStepXConnection connection) =>
            new(_ => Task.FromResult(connection));

        public static FakeOnStepXConnectionFactory Throwing(Exception exception) =>
            new(_ => Task.FromException<IOnStepXConnection>(exception));

        public Task<IOnStepXConnection> ConnectAsync(
            string host,
            int port,
            TimeSpan connectTimeout,
            CancellationToken cancellationToken)
        {
            attempts.Add((host, port, connectTimeout));
            return connect(cancellationToken);
        }
    }

    private sealed class ScriptedOnStepXConnection : IOnStepXConnection
    {
        private readonly IReadOnlyDictionary<string, string> replies;
        private readonly List<string> commands = [];

        public ScriptedOnStepXConnection(IReadOnlyDictionary<string, string> replies)
        {
            this.replies = replies;
        }

        public IReadOnlyList<string> Commands => commands;

        public Task<string> SendCommandAsync(
            string command,
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            commands.Add(command);
            return replies.TryGetValue(command, out var reply)
                ? Task.FromResult(reply)
                : Task.FromResult("0#");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LoopbackOnStepXServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly IReadOnlyDictionary<string, Func<NetworkStream, CancellationToken, Task>> replies;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task serverTask;

        private LoopbackOnStepXServer(
            TcpListener listener,
            IReadOnlyDictionary<string, Func<NetworkStream, CancellationToken, Task>> replies)
        {
            this.listener = listener;
            this.replies = replies;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            serverTask = Task.Run(RunAsync);
        }

        public int Port { get; }

        public static Task<LoopbackOnStepXServer> StartAsync(
            IReadOnlyDictionary<string, Func<NetworkStream, CancellationToken, Task>> replies)
        {
            var listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start();
            return Task.FromResult(new LoopbackOnStepXServer(listener, replies));
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();

            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }

            cancellation.Dispose();
        }

        private async Task RunAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            while (!cancellation.Token.IsCancellationRequested)
            {
                var command = await ReadCommandAsync(stream, cancellation.Token).ConfigureAwait(false);
                if (replies.TryGetValue(command, out var reply))
                {
                    await reply(stream, cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    await WriteReplyAsync(stream, "0#", cancellation.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<string> ReadCommandAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var command = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Client disconnected.");
            }

            var character = (char)buffer[0];
            command.Append(character);
            if (character == '#')
            {
                return command.ToString();
            }
        }
    }

    private static async Task WriteReplyAsync(NetworkStream stream, string reply, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(reply);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        private readonly object syncRoot = new();
        private readonly List<TelemetryRecord> records = [];

        public IReadOnlyList<TelemetryRecord> Records
        {
            get
            {
                lock (syncRoot)
                {
                    return records.ToArray();
                }
            }
        }

        public bool TryPublish(TelemetryRecord record)
        {
            lock (syncRoot)
            {
                records.Add(record);
            }

            return true;
        }
    }
}
