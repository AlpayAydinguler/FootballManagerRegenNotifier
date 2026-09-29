using System.Collections.Immutable;
using System.Globalization;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Triggers;

namespace FootballManagerRegenNotifier.Core.Tracking;

/// <summary>
/// Decides what the on-screen clock moving means. Pure: <see cref="Step"/> is a
/// function of (state, observation) and returns the next state plus what
/// happened. No timers, no screen, no I/O.
/// </summary>
/// <remarks>
/// <para>
/// The central design point is that alerts fire on <em>interval crossing</em>,
/// never on date equality.
/// </para>
/// <para>
/// Football Manager's Continue button advances to the next event rather than by
/// one day, so a single click can move the clock from 2 June to 8 June, and the
/// intervening days are never drawn on screen at all. An equality test therefore
/// misses intakes no matter how fast the screen is sampled; polling faster is a
/// misdiagnosis that costs battery and fixes nothing. Asking instead "did any
/// armed trigger fall between where the clock was and where it is now" is
/// correct at any sampling rate, including one sample per second.
/// </para>
/// </remarks>
public sealed class DateTracker(TriggerCalendar calendar, TrackerConfig? config = null)
{
    private readonly TriggerCalendar _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
    private readonly TrackerConfig _config = config ?? TrackerConfig.Default;

    public TrackerConfig Config => _config;

    /// <summary>
    /// Advances the state machine by one reading.
    /// </summary>
    /// <param name="state">Current state.</param>
    /// <param name="observation">The latest sample.</param>
    /// <param name="isArmed">
    /// Whether the user has this country selected. Evaluated at fire time rather
    /// than at materialisation time so that toggling a country takes effect
    /// immediately without rebuilding the calendar.
    /// </param>
    public StepResult Step(TrackerState state, Observation observation, Func<CountryRule, bool> isArmed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(isArmed);

        // Only a clean read may touch state. A failed capture is not "the date
        // did not change" - conflating the two lets an occluded screen or a
        // closed game look like a stationary clock.
        if (observation.Status != SampleStatus.Ok || observation.Date is not { } date)
        {
            return new StepResult(state, []);
        }

        if (observation.MeanConfidence < _config.MinMeanConfidence)
        {
            return new StepResult(state, [TrackerEvent.Info(
                TrackerEventKind.SampleRejected,
                $"Rejected \"{observation.RawText}\" - mean confidence {observation.MeanConfidence:P0} below {_config.MinMeanConfidence:P0}.")]);
        }

        if (observation.MinSymbolConfidence < _config.MinSymbolConfidence)
        {
            return new StepResult(state, [TrackerEvent.Info(
                TrackerEventKind.SampleRejected,
                $"Rejected \"{observation.RawText}\" - weakest character {observation.MinSymbolConfidence:P0} below {_config.MinSymbolConfidence:P0}.")]);
        }

        // Already committed to this date; nothing to do. Clear any stale pending
        // candidate so a flicker cannot accumulate confirmations over time, and
        // any re-sync candidate: the committed date is evidently still on screen.
        if (state.LastSeen == date)
        {
            return new StepResult(state with { Pending = null, PendingCount = 0, Candidate = null }, []);
        }

        // Commit-on-confirm.
        int count = state.Pending == date ? state.PendingCount + 1 : 1;
        if (count < _config.ConfirmSamples)
        {
            return new StepResult(
                state with { Pending = date, PendingCount = count },
                [TrackerEvent.Info(TrackerEventKind.AwaitingConfirmation,
                    $"Saw {Fmt(date)} ({count}/{_config.ConfirmSamples}) - awaiting confirmation.", date)]);
        }

        var committed = state with { Pending = null, PendingCount = 0 };
        return state.LastSeen is not { } previous
            ? ColdStart(committed, date)
            : Advance(committed, previous, date, isArmed);
    }

    /// <summary>
    /// First confirmed reading of a session. Adopt the date and arm, but fire
    /// nothing: everything before this point is unknown, and alerting on it would
    /// mean announcing every intake of the past year the moment the app starts.
    /// </summary>
    private static StepResult ColdStart(TrackerState state, DateOnly date) =>
        new(state with { LastSeen = date },
            [TrackerEvent.Info(TrackerEventKind.DateAdopted,
                $"Now tracking from {Fmt(date)}.", date)]);

    private StepResult Advance(TrackerState state, DateOnly previous, DateOnly current, Func<CountryRule, bool> isArmed)
    {
        int delta = current.DayNumber - previous.DayNumber;

        // Checked before the direction, and in both directions. A year misread as
        // 2013 used to go through as a save reload, get committed, and then turn
        // every correct reading afterwards into an implausible forward jump.
        if (Math.Abs(delta) > _config.MaxJumpDays) return Quarantine(state, previous, current, isArmed);

        // A plausible reading means the committed timeline is alive.
        state = state with { Candidate = null };

        if (delta < 0) return Regress(state, previous, current);

        var events = new List<TrackerEvent>
        {
            TrackerEvent.Info(TrackerEventKind.ClockAdvanced,
                delta == 1
                    ? $"Date advanced to {Fmt(current)}."
                    : $"Date advanced {delta} days to {Fmt(current)}.",
                current),
        };

        var fired = state.Fired;
        foreach (var trigger in _calendar.Crossings(previous, current))
        {
            if (!isArmed(trigger.Rule)) continue;
            if (fired.Contains(trigger.Key)) continue;

            fired = fired.Add(trigger.Key);
            events.Add(Alert(trigger, current));
        }

        return new StepResult(state with { LastSeen = current, Fired = fired }, events);
    }

    /// <summary>
    /// A confirmed reading too far from the committed date to believe.
    /// </summary>
    /// <remarks>
    /// Do not commit it: a year field misread as 2062 would otherwise poison
    /// LastSeen and fire every trigger in between. But do not drop it either,
    /// because the refusal is symmetric - if LastSeen is the misread, the truth
    /// looks exactly this implausible. Readings that agree with each other build
    /// up a <see cref="ResyncCandidate"/>, and a candidate that lasts wins.
    /// </remarks>
    private StepResult Quarantine(TrackerState state, DateOnly previous, DateOnly current, Func<CountryRule, bool> isArmed)
    {
        var candidate = state.Candidate is { } c && Math.Abs(current.DayNumber - c.Latest.DayNumber) <= _config.MaxJumpDays
            ? new ResyncCandidate(current < c.Since ? current : c.Since, current, c.Confirmations + 1)
            : new ResyncCandidate(DayBefore(current), current, 1);

        if (candidate.Confirmations >= _config.ResyncConfirmations)
        {
            return Resync(state, previous, candidate, isArmed);
        }

        int delta = current.DayNumber - previous.DayNumber;
        return new StepResult(state with { Candidate = candidate }, [TrackerEvent.Info(
            TrackerEventKind.JumpQuarantined,
            $"Ignored implausible jump {Fmt(previous)} to {Fmt(current)} ({delta} days). Likely a misread - "
            + $"will switch to it if it keeps reading that way ({candidate.Confirmations}/{_config.ResyncConfirmations}).",
            current)]);
    }

    /// <summary>
    /// Abandons the committed date for a candidate that has outlasted it.
    /// </summary>
    /// <remarks>
    /// The old <see cref="TrackerState.Fired"/> set belongs to the abandoned
    /// timeline and is dropped: kept, a reload to an earlier season would leave
    /// that season's intakes marked as already alerted. Its replacement is what
    /// the candidate crossed while it was being confirmed, fired now, so that a
    /// window which opened during the wait is reported late rather than lost.
    /// </remarks>
    private StepResult Resync(TrackerState state, DateOnly previous, ResyncCandidate candidate, Func<CountryRule, bool> isArmed)
    {
        var current = candidate.Latest;
        var events = new List<TrackerEvent>
        {
            TrackerEvent.Info(TrackerEventKind.ClockResynced,
                $"Switched to {Fmt(current)} from {Fmt(previous)} after {candidate.Confirmations} consistent readings. "
                + $"{Fmt(previous)} was probably a misread, or a different save was loaded.",
                current),
        };

        var fired = ImmutableHashSet<TriggerKey>.Empty;
        foreach (var trigger in _calendar.Crossings(candidate.Since, current))
        {
            if (!isArmed(trigger.Rule)) continue;

            fired = fired.Add(trigger.Key);
            events.Add(Alert(trigger, current));
        }

        return new StepResult(state with { LastSeen = current, Fired = fired, Candidate = null }, events);
    }

    /// <summary>
    /// The clock ran backwards, which almost always means a save was reloaded.
    /// </summary>
    /// <remarks>
    /// Re-arm rather than suppress. Reloading the day before an intake to reroll
    /// the crop is the single most common reason anyone wants this tool at all,
    /// so a backwards jump is the expected path, not an anomaly, and an alert
    /// that fired once and then stayed silent through the re-run would be useless
    /// exactly when it is most wanted. Repeat spam from a tight reload loop is
    /// suppressed at the alert channel on a real-time cooldown, not here.
    /// </remarks>
    private StepResult Regress(TrackerState state, DateOnly previous, DateOnly current)
    {
        var fired = state.Fired;
        int rearmed = 0;
        foreach (var trigger in _calendar.Crossings(current, previous))
        {
            if (!fired.Contains(trigger.Key)) continue;
            fired = fired.Remove(trigger.Key);
            rearmed++;
        }

        var events = new List<TrackerEvent>
        {
            TrackerEvent.Info(TrackerEventKind.ClockRegressed,
                $"Clock moved back to {Fmt(current)} from {Fmt(previous)} - save reloaded.", current),
        };

        if (rearmed > 0)
        {
            events.Add(TrackerEvent.Info(TrackerEventKind.TriggersReArmed,
                $"Re-armed {rearmed} alert(s) that had already fired.", current));
        }

        return new StepResult(state with { LastSeen = current, Fired = fired }, events);
    }

    private static TrackerEvent Alert(MaterialisedTrigger trigger, DateOnly observed)
    {
        var timing = trigger.TimingAt(observed);
        return new TrackerEvent
        {
            Kind = TrackerEventKind.AlertRaised,
            Trigger = trigger,
            Timing = timing,
            Date = observed,
            Message = Describe(trigger, timing, observed),
        };
    }

    /// <summary>
    /// The exclusive bound that makes a crossing test include <paramref name="date"/>
    /// itself. Clamped rather than thrown: a garbage year 0001 is still a reading.
    /// </summary>
    private static DateOnly DayBefore(DateOnly date) =>
        date == DateOnly.MinValue ? date : date.AddDays(-1);

    private static string Describe(MaterialisedTrigger t, TriggerTiming timing, DateOnly observed)
    {
        string who = t.Rule.Country;
        string kind = t.Key.Kind == TriggerKind.Lead
            ? "youth intake window opens soon"
            : "youth intake window is open";

        return timing switch
        {
            TriggerTiming.OnTime =>
                $"{who}: {kind} ({Fmt(t.WindowOpen)} to {Fmt(t.WindowClose)}).",
            TriggerTiming.Late =>
                $"{who}: intake window opened {t.DaysLate(observed)} day(s) ago on {Fmt(t.WindowOpen)} - you are now at {Fmt(observed)}. Still open until {Fmt(t.WindowClose)}.",
            _ =>
                $"{who}: intake window ({Fmt(t.WindowOpen)} to {Fmt(t.WindowClose)}) closed before this was seen - you are now at {Fmt(observed)}.",
        };
    }

    private static string Fmt(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}

public sealed record StepResult(TrackerState State, IReadOnlyList<TrackerEvent> Events);
