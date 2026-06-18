using FluentAssertions;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpTraceSerializerTests
{
    [Fact]
    public void Serialize_PreservesSpanStartAndEndTimestamps()
    {
        var started = DateTimeOffset.UnixEpoch.AddSeconds(42).AddTicks(1234567);
        var stopped = started.AddSeconds(9);
        var span = new OtlpCompletedSpan(
            "nina.sequence",
            "nina.sequence.target",
            "target-1",
            null,
            started,
            stopped,
            new Dictionary<string, object?>
            {
                ["target.name"] = "M42",
            },
            [],
            "00112233445566778899aabbccddeeff");

        var payload = OtlpTraceSerializer.Serialize([span]);

        payload.Should().NotBeEmpty();
        ProtobufFieldScanner.FindBytes(payload, "1.2.2.1")
            .Should()
            .ContainEquivalentOf(Convert.FromHexString("00112233445566778899aabbccddeeff"));
        ProtobufFieldScanner.FindFixed64(payload, "1.2.2.7")
            .Should()
            .Contain(ToUnixNanoseconds(started));
        ProtobufFieldScanner.FindFixed64(payload, "1.2.2.8")
            .Should()
            .Contain(ToUnixNanoseconds(stopped));
        ProtobufFieldScanner.FindStrings(payload, "1.2.2.5")
            .Should()
            .Contain("nina.sequence.target");
        ProtobufFieldScanner.FindStrings(payload, "1.2.2.9.1")
            .Should()
            .Contain("target.name");
    }

    [Fact]
    public void Serialize_WritesSpanEventsWithOriginalTimestamps()
    {
        var eventTime = DateTimeOffset.UnixEpoch.AddSeconds(45);
        var span = new OtlpCompletedSpan(
            "nina.capture",
            "nina.capture.exposure",
            "exp-1",
            null,
            DateTimeOffset.UnixEpoch.AddSeconds(40),
            DateTimeOffset.UnixEpoch.AddSeconds(50),
            new Dictionary<string, object?>(),
            [
                new OtlpCompletedSpanEvent(
                    eventTime,
                    "download",
                    new Dictionary<string, object?>
                    {
                        ["file.name"] = "M42_L_001.fit",
                    }),
            ]);

        var payload = OtlpTraceSerializer.Serialize([span]);

        ProtobufFieldScanner.FindFixed64(payload, "1.2.2.11.1")
            .Should()
            .Contain(ToUnixNanoseconds(eventTime));
        ProtobufFieldScanner.FindStrings(payload, "1.2.2.11.2")
            .Should()
            .Contain("download");
        ProtobufFieldScanner.FindStrings(payload, "1.2.2.11.3.1")
            .Should()
            .Contain("file.name");
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset timestamp) =>
        (ulong)((timestamp.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100);
}
