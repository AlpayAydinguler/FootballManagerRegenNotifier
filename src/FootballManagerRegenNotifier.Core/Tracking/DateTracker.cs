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
            return new StepResult(
                state with { Pending = null, PendingCount = 0, Candidate = null, Unconfirmed = false }, []);
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
            ? ColdStart(committed, date, isArmed)
            : Advance(committed, previous, date, isArmed);
    }

    /// <summary>
    /// First confirmed reading of a session. Adopt the date and arm, but fire
    /// nothing: everything before this point is unknown, and alerting on it would
    /// mean announcing every intake of the past year the moment the app starts.
    /// </summary>
    /// <remarks>
    /// After <c>Reset date</c> the player has just switched saves on purpose, so
    /// windows open at the adopted date are announced, exactly as an automatic
    /// switch does. A first-ever launch stays silent.
    /// </remarks>
    private StepResult ColdStart(TrackerState state, DateOnly date, Func<CountryRule, bool> isArmed)
    {
        var events = new List<TrackerEvent>
        {
            TrackerEvent.Info(TrackerEventKind.DateAdopted, $"Now tracking from {Fmt(date)}.", date),
        };

        var fired = state.Fired;
        if (state.AnnounceOpenWindows)
        {
            foreach (var trigger in StillOpen(date, date))
            {
                if (!isArmed(trigger.Rule)) continue;
                if (fired.Contains(trigger.Key)) continue;

                fired = fired.Add(trigger.Key);
                events.Add(Alert(trigger, date));
            }
        }

        return new StepResult(state with { LastSeen = date, Fired = fired, AnnounceOpenWindows = false }, events);
    }

    private StepResult Advance(TrackerState state, DateOnly previous, DateOnly current, Func<CountryRule, bool> isArmed)
    {
        int delta = current.DayNumber - previous.DayNumber;

        // Checked before the direction, and in both directions. A year misread as
        // 2013 used to go through as a save reload, get committed, and then turn
        // every correct reading afterwards into an implausible forward jump. A
        // year misread by one (2025 as 2026) is inside the ceiling, so its shape
        // is checked as well.
        if (Math.Abs(delta) > _config.MaxJumpDays || YearsShifted(previous, current) is not null)
        {
            return Quarantine(state, previous, current, isArmed);
        }

        // A reading that carries on from the candidate rather than from the
        // committed date belongs to the candidate. Loading a save a year back and
        // pressing Continue walks out of the misread-year shape within a week;
        // committing that reading against the old date would skip the days the
        // candidate had already covered, and the intakes in them.
        if (state.Candidate is { } held && Math.Abs(current.DayNumber - held.Latest.DayNumber) < Math.Abs(delta))
        {
            return Quarantine(state, previous, current, isArmed);
        }

        // A plausible reading means the committed timeline is alive.
        state = state with { Candidate = null, Unconfirmed = false };

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
    /// A confirmed reading the committed date cannot account for.
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
        var existing = state.Candidate is { } c && Math.Abs(current.DayNumber - c.Latest.DayNumber) <= _config.MaxJumpDays
            ? c
            : null;
        var candidate = existing is { } e
            ? e with { Floor = current < e.Floor ? current : e.Floor, Latest = current, Confirmations = e.Confirmations + 1 }
            : new ResyncCandidate(current, current, current, 1);

        // A date restored from disk was never seen this session, so a reading
        // beyond the ceiling wins at once: the game is most likely on another save.
        // Not one held only for its misread-year shape - believing that at once
        // would fire a year of alerts from two samples of a misread digit.
        bool beyondCeiling = Math.Abs(candidate.First.DayNumber - previous.DayNumber) > _config.MaxJumpDays;
        int needed = state.Unconfirmed && beyondCeiling ? 1 : _config.ResyncConfirmations;
        if (candidate.Confirmations >= needed)
        {
            return Resync(state, previous, candidate, isArmed);
        }

        int delta = current.DayNumber - previous.DayNumber;
        string message;
        if (Math.Abs(delta) <= _config.MaxJumpDays && YearsShifted(previous, current) is null && existing is { } follows)
        {
            message = $"Saw {Fmt(current)}, which carries on from {Fmt(follows.Latest)} rather than {Fmt(previous)}.";
        }
        else
        {
            string why = YearsShifted(previous, current) is not null
                ? "it lands on the same date in another year, which is almost always a misread year"
                : "likely a misread";
            message = $"Ignored implausible jump {Fmt(previous)} to {Fmt(current)} ({delta} days): {why}.";
        }

        return new StepResult(state with { Candidate = candidate }, [TrackerEvent.Info(
            TrackerEventKind.JumpQuarantined,
            $"{message} Will switch to it if it keeps reading that way ({candidate.Confirmations}/{needed}).",
            current)]);
    }

    /// <summary>
    /// Abandons the committed date for a candidate that has outlasted it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The new timeline carries on from an origin, and the candidate's own moves
    /// are replayed from there exactly as <see cref="Advance"/> and
    /// <see cref="Regress"/> would have handled them: re-arm down to the lowest
    /// date it visited, then fire everything from there to where it is now. So a
    /// window that opened during the wait is reported late rather than lost. The
    /// origin is, in order of preference:
    /// </para>
    /// <list type="bullet">
    /// <item>The timeline the last re-sync left, when the candidate comes back to
    /// it. A misread that won, or a save switched away from, is picked up again
    /// where it was left, with the alerts it had given, so the intakes it crossed
    /// meanwhile are reported once and nothing is repeated. "Back" means from
    /// anywhere up to the jump ceiling before where it was left (an older
    /// autosave) to <see cref="TrackerConfig.ReturnWindowDays"/> after it (the
    /// clock kept moving). A restored date gets the whole ceiling forwards,
    /// since the game may have been played on while the app was closed.</item>
    /// <item>The committed timeline itself, when the candidate is within the jump
    /// ceiling of it and was held back only for its shape. That makes a holiday of
    /// about a year come out exactly as it would have if accepted at once, only
    /// later.</item>
    /// <item>Otherwise the day before the candidate's first reading, with nothing
    /// alerted: a genuinely new timeline, such as another save.</item>
    /// </list>
    /// <para>
    /// Alerts never carry over between timelines by any other route. A save at the
    /// same time of year in another year looks exactly like a misread year, and
    /// carrying the other one's alerts across would silently suppress an open
    /// window in it; a misread year that wins may repeat an intake instead.
    /// Windows already open at the new date are announced unless the origin had
    /// alerted them, because a re-sync must never leave an open window unmentioned.
    /// </para>
    /// </remarks>
    private StepResult Resync(TrackerState state, DateOnly previous, ResyncCandidate candidate, Func<CountryRule, bool> isArmed)
    {
        var current = candidate.Latest;

        Timeline origin;
        string how;
        if (state.Abandoned is { } back && ComesBackTo(back, candidate.First))
        {
            origin = back;
            how = $"Picking up from {Fmt(back.LastSeen)}, where this timeline was left; {Fmt(previous)} was a misread or another save.";
        }
        else if (Math.Abs(candidate.First.DayNumber - previous.DayNumber) <= _config.MaxJumpDays)
        {
            // Close enough to have been accepted, and held back only for looking
            // like a misread year. Having held this long, it is a holiday or a
            // reload of about a year, and is handled as one would have been.
            origin = new Timeline(previous, state.Fired);
            how = $"It looked like a misread year, but it has held, so it is treated as a move from {Fmt(previous)}.";
        }
        else
        {
            origin = new Timeline(DayBefore(candidate.First), []);
            how = state.Unconfirmed
                ? $"The saved date {Fmt(previous)} is not what is on screen: another save was loaded, or it was a misread."
                : $"Either {Fmt(previous)} was a misread or a different save was loaded.";
        }

        var events = new List<TrackerEvent>
        {
            TrackerEvent.Info(TrackerEventKind.ClockResynced,
                $"Switched to {Fmt(current)} after {candidate.Confirmations} consistent reading(s). {how}", current),
        };

        var floor = candidate.Floor < origin.LastSeen ? candidate.Floor : origin.LastSeen;
        var fired = origin.Fired.Except(_calendar.Crossings(floor, origin.LastSeen).Select(t => t.Key));

        foreach (var trigger in _calendar.Crossings(floor, current).Concat(StillOpen(current, floor)))
        {
            if (!isArmed(trigger.Rule)) continue;
            if (fired.Contains(trigger.Key)) continue;

            fired = fired.Add(trigger.Key);
            events.Add(Alert(trigger, current));
        }

        return new StepResult(state with
        {
            LastSeen = current,
            Fired = fired,
            Candidate = null,
            Unconfirmed = false,
            Abandoned = new Timeline(previous, state.Fired, Restored: state.Unconfirmed),
        }, events);
    }

    private bool ComesBackTo(Timeline left, DateOnly first)
    {
        int gap = first.DayNumber - left.LastSeen.DayNumber;
        int ahead = left.Restored ? _config.MaxJumpDays : _config.ReturnWindowDays;
        return gap >= -_config.MaxJumpDays && gap <= ahead;
    }

    /// <summary>
    /// Windows open on <paramref name="at"/> that opened on or before
    /// <paramref name="openedBy"/>, which is where a crossing test stops seeing them.
    /// </summary>
    private IEnumerable<MaterialisedTrigger> StillOpen(DateOnly at, DateOnly openedBy) =>
        _calendar.Materialise([at.Year - 1, at.Year])
            .Where(t => t.Key.Kind == TriggerKind.WindowOpen
                        && t.FireOn <= openedBy
                        && t.FireOn <= at
                        && at <= t.WindowClose)
            .OrderBy(t => t.FireOn)
            .ThenBy(t => t.Key.CountryCode, StringComparer.Ordinal);

    /// <summary>
    /// Whole years that <paramref name="to"/> is away from <paramref name="from"/>'s
    /// calendar date, when that is the best explanation of the jump; null otherwise.
    /// </summary>
    /// <remarks>
    /// The neighbouring year counts too, so a misread landing just across
    /// 1 January is still recognised. Zero years never counts: that is ordinary
    /// play, and it wins whenever it is the closer fit.
    /// </remarks>
    private int? YearsShifted(DateOnly from, DateOnly to)
    {
        int years = to.Year - from.Year;
        int? best = null;
        int bestGap = int.MaxValue;
        for (int k = years - 1; k <= years + 1; k++)
        {
            if (ShiftYears(from, k) is not { } shifted) continue;

            int gap = Math.Abs(to.DayNumber - shifted.DayNumber);
            if (gap < bestGap)
            {
                best = k;
                bestGap = gap;
            }
        }

        return best is { } b && b != 0 && bestGap <= _config.YearMisreadToleranceDays ? b : null;
    }

    /// <summary>The same calendar date <paramref name="years"/> away; 29 February becomes the 28th.</summary>
    private static DateOnly? ShiftYears(DateOnly date, int years)
    {
        int year = date.Year + years;
        if (year < DateOnly.MinValue.Year || year > DateOnly.MaxValue.Year) return null;
        return new DateOnly(year, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month)));
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
