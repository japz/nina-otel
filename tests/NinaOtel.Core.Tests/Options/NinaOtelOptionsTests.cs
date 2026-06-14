using FluentAssertions;
using NinaOtel.Core.Options;
using Xunit;

namespace NinaOtel.Core.Tests.Options;

public sealed class NinaOtelOptionsTests
{
    [Fact]
    public void CreateDefault_UsesMemoryFirstDiskOnFailureDefaults()
    {
        var options = NinaOtelOptions.CreateDefault();

        options.Buffer.DiskOnFailureEnabled.Should().BeTrue();
        options.Buffer.SpoolsDuringHealthyExport.Should().BeFalse();
        options.Buffer.MaxSpoolBytes.Should().Be(1L * 1024 * 1024 * 1024);
        options.Buffer.MaxSpoolAge.Should().Be(TimeSpan.FromDays(7));
        options.Otlp.Protocol.Should().Be(OtlpProtocol.Grpc);
    }
}
