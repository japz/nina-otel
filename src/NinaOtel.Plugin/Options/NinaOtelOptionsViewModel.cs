using NinaOtel.Core.Options;

namespace NinaOtel.Plugin.Options;

public sealed class NinaOtelOptionsViewModel
{
    public NinaOtelOptionsViewModel(NinaOtelOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public NinaOtelOptions Options { get; }
    public string CollectorEndpoint => Options.Otlp.Endpoint.ToString();
    public string CollectorProtocol => Options.Otlp.Protocol.ToString();
    public bool DiskOnFailureEnabled => Options.Buffer.DiskOnFailureEnabled;
    public string Status => "NinaOtel foundation loaded";
}
