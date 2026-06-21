using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NinaOtel.Core.Tests")]

namespace NinaOtel.Addons.PHD2;

internal enum Phd2LogEventKind
{
    GuidingStarted,
    GuidingStopped,
    Dither,
    SettleStarted,
    SettleCompleted,
    CaptureError,
}

internal sealed record Phd2LogEvent(
    Phd2LogEventKind Kind,
    DateTimeOffset Timestamp,
    string Source,
    string SourcePath,
    string OriginalLine);

internal sealed record Phd2GuideSample(
    DateTimeOffset Timestamp,
    double RaDistanceArcsec,
    double DecDistanceArcsec,
    string Source,
    string SourcePath,
    string OriginalLine);
