using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NinaOtel.Core.Tests")]

namespace NinaOtel.Addons.NightSummary;

internal enum NightSummaryLogEventKind
{
    SessionStarted,
    SessionEnded,
    CameraInfoStored,
    EquipmentCaptured,
    RoofOpen,
    RoofClosed,
    AutoFocusCompleted,
    MeridianFlip,
    TargetSchedulerGradingSynced,
    TargetSchedulerGradingFailed,
    ReportGenerating,
    ReportDelivering,
    ReportSaved,
    ReportDelivered,
    ReportFailed,
    Warning,
    Error,
}

internal sealed record NightSummaryLogEvent(
    NightSummaryLogEventKind Kind,
    DateTimeOffset Timestamp,
    string Source,
    string SourcePath,
    string OriginalLine,
    string Level,
    string Message,
    string? SessionId);
