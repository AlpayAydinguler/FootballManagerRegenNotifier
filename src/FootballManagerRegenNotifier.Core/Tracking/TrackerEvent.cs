using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.Core.Tracking;

public enum TrackerEventKind
{
    DateConfirmed,
    DateAdopted,
    ClockAdvanced,
    ClockRegressed,
    TriggersReArmed,
    JumpQuarantined,
    SampleRejected,
    AwaitingConfirmation,
    AlertRaised,
}

/// <summary>
/// Something the tracker decided. These are the Activity Log's event stream and
/// the alert service's input — the tracker itself neither logs nor notifies.
/// </summary>
public sealed record TrackerEvent
{
    public required TrackerEventKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>Set on <see cref="TrackerEventKind.AlertRaised"/>.</summary>
    public MaterialisedTrigger? Trigger { get; init; }

    public TriggerTiming Timing { get; init; } = TriggerTiming.OnTime;

    public DateOnly? Date { get; init; }

    public bool IsAlert => Kind == TrackerEventKind.AlertRaised;

    public static TrackerEvent Info(TrackerEventKind kind, string message, DateOnly? date = null) =>
        new() { Kind = kind, Message = message, Date = date };
}
