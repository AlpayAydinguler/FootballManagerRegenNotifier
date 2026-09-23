namespace FootballManagerRegenNotifier.Core.Model;

public enum TriggerKind
{
    /// <summary>The day the nation's intake window opens. The primary alert.</summary>
    WindowOpen = 0,

    /// <summary>An optional early heads-up, fired <c>LeadDays</c> before the window opens.</summary>
    Lead = 1,
}

/// <summary>
/// Identity of a single alertable occurrence. This is the anti-spam key.
/// </summary>
/// <remarks>
/// Keyed on the occurrence (country + the in-game year the window opens + kind)
/// rather than on "country + the day we happened to notice".
///
/// A literal "once per in-game day per country" key looks equivalent but is not:
/// a single Continue can advance the clock by months, legitimately crossing the
/// windows of forty nations at once. A per-day key would fire the first and
/// swallow the other thirty-nine. Real-time repeat suppression is handled
/// separately, at the alert channel, where it belongs.
/// </remarks>
public readonly record struct TriggerKey(string CountryCode, int OccurrenceYear, TriggerKind Kind)
{
    public override string ToString() => $"{CountryCode}:{OccurrenceYear}:{Kind}";
}

/// <summary>A <see cref="CountryRule"/> resolved to concrete dates for one in-game year.</summary>
public sealed record MaterialisedTrigger
{
    public required TriggerKey Key { get; init; }

    /// <summary>The date that actually arms this alert.</summary>
    public required DateOnly FireOn { get; init; }

    public required DateOnly WindowOpen { get; init; }

    public required DateOnly WindowClose { get; init; }

    public required CountryRule Rule { get; init; }

    /// <summary>
    /// How late the alert is relative to the intake window, given where the clock
    /// has actually landed. Drives the wording of the alert: arriving on the day
    /// and arriving four months after the window shut are different messages.
    /// </summary>
    public TriggerTiming TimingAt(DateOnly observed)
    {
        if (observed <= FireOn) return TriggerTiming.OnTime;
        if (observed <= WindowClose) return TriggerTiming.Late;
        return TriggerTiming.Missed;
    }

    public int DaysLate(DateOnly observed) => Math.Max(0, observed.DayNumber - FireOn.DayNumber);
}

public enum TriggerTiming
{
    /// <summary>Caught on the day it armed.</summary>
    OnTime = 0,

    /// <summary>Crossed during a jump, but the intake window is still open.</summary>
    Late = 1,

    /// <summary>The whole window closed before we saw the clock again.</summary>
    Missed = 2,
}
