using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Logs;

public enum NinaLogEventKind
{
    Unknown,
    Warning,
    Error,
    Fatal,
    ApplicationStarted,
    ApplicationClosing,
    PluginLoaded,
    PluginLoadFailed,
    EquipmentConnected,
    EquipmentDisconnected,
    SequenceStarted,
    SequenceFinished,
    AutofocusStarted,
    AutofocusFinished,
    MeridianFlipStarted,
    MeridianFlipFinished,
    SafetyUnsafe,
    SafetySafe,
}

public sealed record NinaLogEvent(
    NinaLogEventKind Kind,
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Member,
    int LineNumber,
    string Message,
    string RawLine,
    TelemetrySeverity? Severity = null);
