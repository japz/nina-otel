using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NinaOtel.Core.Tests")]

namespace NinaOtel.Addons.TargetScheduler;

internal enum TargetSchedulerLogEventKind
{
    PlanningStarted,
    PlanningCompleted,
    TargetSelected,
    PlanStarted,
    PlanStopped,
    ImageGraded,
    Warning,
    Error,
}

internal sealed record TargetSchedulerLogEvent(
    TargetSchedulerLogEventKind Kind,
    DateTimeOffset Timestamp,
    string Source,
    string SourcePath,
    string OriginalLine,
    string Level,
    string Message);
