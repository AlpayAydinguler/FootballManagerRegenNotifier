using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Tracking;
using FootballManagerRegenNotifier.Core.Triggers;
using Xunit;

namespace FootballManagerRegenNotifier.Core.Tests;

public class DateTrackerTests
{
    private static DateTracker Tracker(params CountryRule[] rules) =>
        new(new TriggerCalendar(rules));

    private static DateTracker Tracker(TrackerConfig config, params CountryRule[] rules) =>
        new(new TriggerCalendar(rules), config);

    // ---------------------------------------------------------------- cold start

    [Fact]
    public void ColdStart_AdoptsDateWithoutFiringAnything()
    {
        var tracker = Tracker(TestData.England);
        var events = new List<TrackerEvent>();

        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 6, 1), sink: events);

        Assert.Equal(new DateOnly(2026, 6, 1), state.LastSeen);
        Assert.Empty(events.Alerts());
        Assert.Contains(events, e => e.Kind == TrackerEventKind.DateAdopted);
    }

    [Fact]
    public void ColdStart_DoesNotAnnounceIntakesAlreadyPassedThisYear()
    {
        // Starting the app in June must not dump every March intake into the log.
        var tracker = Tracker(TestData.England, TestData.Brazil);
        var events = new List<TrackerEvent>();

        TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 6, 1), sink: events);

        Assert.Empty(events.Alerts());
    }

    // ------------------------------------------------------------ normal advance

    [Fact]
    public void SingleDayAdvanceOntoWindowOpen_Fires()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal("ENG", alert.Trigger!.Key.CountryCode);
        Assert.Equal(TriggerTiming.OnTime, alert.Timing);
        Assert.Equal(new DateOnly(2026, 3, 14), state.LastSeen);
    }

    [Fact]
    public void RepeatedIdenticalReads_DoNotRefire()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14));

        var events = new List<TrackerEvent>();
        for (int i = 0; i < 10; i++)
        {
            var r = tracker.Step(state, TestData.Good(2026, 3, 14), _ => true);
            state = r.State;
            events.AddRange(r.Events);
        }

        Assert.Empty(events.Alerts());
    }

    [Fact]
    public void UncheckedCountry_NeverFires()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13), armed: _ => false);

        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), armed: _ => false, sink: events);

        Assert.Empty(events.Alerts());
    }

    // ----------------------------------------------------- the missed-day problem

    [Fact]
    public void HolidayJump_FiresEveryCrossedTriggerExactlyOnce()
    {
        // The core scenario. Holiday from 1 January to 30 June 2026 in one move:
        // England (14 Mar) and Mexico (28 Feb) are both crossed and never rendered.
        var tracker = Tracker(TestData.England, TestData.Mexico, TestData.Brazil);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 1, 1));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2026, 6, 30), sink: events);

        var alerts = events.Alerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Trigger!.Key.CountryCode == "ENG");
        Assert.Contains(alerts, a => a.Trigger!.Key.CountryCode == "MEX");
        // Brazil opens in September - not crossed yet.
        Assert.DoesNotContain(alerts, a => a.Trigger!.Key.CountryCode == "BRA");

        // Every one of them is reported as missed, since both windows shut before
        // the clock was seen again.
        Assert.All(alerts, a => Assert.Equal(TriggerTiming.Missed, a.Timing));
    }

    [Fact]
    public void JumpLandingInsideWindow_ReportsLateNotMissed()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 10));

        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2026, 3, 20), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(TriggerTiming.Late, alert.Timing);
        Assert.Equal(6, alert.Trigger!.DaysLate(new DateOnly(2026, 3, 20)));
        Assert.Contains("6 day(s) ago", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JumpAcrossAFullYear_FiresEachOccurrenceOnce()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 1, 1));

        var events = new List<TrackerEvent>();
        // 348 days: crosses March 2026 only (March 2027 is beyond). Kept clear of
        // a whole year, which lands on the misread-year shape and is held back.
        TestData.Commit(tracker, state, new DateOnly(2026, 12, 15), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(2026, alert.Trigger!.Key.OccurrenceYear);
    }

    [Fact]
    public void ImplausibleJump_IsQuarantinedAndDoesNotCommit()
    {
        // A mangled year field ("2026" read as "2062") must not poison LastSeen.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 1));

        var events = new List<TrackerEvent>();
        var after = TestData.Commit(tracker, state, new DateOnly(2062, 3, 1), sink: events);

        Assert.Equal(new DateOnly(2026, 3, 1), after.LastSeen);
        Assert.Empty(events.Alerts());
        Assert.Contains(events, e => e.Kind == TrackerEventKind.JumpQuarantined);
    }

    [Fact]
    public void SystematicMisread_InterleavedWithTheTrueDate_NeverCommits()
    {
        // Commit-on-confirm cannot catch a misread that repeats identically, which
        // is exactly why the plausibility ceiling exists as a separate guard. One
        // read of the committed date in each run is enough to keep it out.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 1));

        for (int i = 0; i < 50; i++)
        {
            for (int j = 0; j < 2 * (tracker.Config.ResyncConfirmations - 1); j++)
            {
                state = tracker.Step(state, TestData.Good(2062, 3, 1), _ => true).State;
            }
            state = tracker.Step(state, TestData.Good(2026, 3, 1), _ => true).State;
        }

        Assert.Equal(new DateOnly(2026, 3, 1), state.LastSeen);
        Assert.Null(state.Candidate);
    }

    // ---------------------------------------------------------- misread recovery

    [Fact]
    public void BackwardMisread_IsQuarantinedRatherThanTreatedAsAReload()
    {
        // The reported failure: 2025 read as 2013 twice in a row went through as a
        // save reload, was written to state.json, and re-armed every alert since.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2025, 8, 13));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2013, 8, 13), sink: events);

        Assert.Equal(new DateOnly(2025, 8, 13), state.LastSeen);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.JumpQuarantined);
        Assert.DoesNotContain(events, e => e.Kind == TrackerEventKind.ClockRegressed);
    }

    [Fact]
    public void BackwardMisread_ThenTheTrueDate_CarriesOnAsIfNothingHappened()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 10));
        state = TestData.Commit(tracker, state, new DateOnly(2013, 3, 10));

        // Reading correctly again: the committed date drops the candidate...
        state = tracker.Step(state, TestData.Good(2026, 3, 10), _ => true).State;
        Assert.Null(state.Candidate);

        // ...and the next intake still fires on time.
        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(TriggerTiming.OnTime, alert.Timing);
    }

    [Fact]
    public void StuckOnAMisreadDate_SwitchesToTheTrueDateOnceItPersists()
    {
        // A misread already in state.json from an earlier version, or one that
        // persisted long enough to win. Every correct reading is now "too far",
        // and before this fix the app refused them all, forever.
        var tracker = Tracker(TestData.England);
        var state = new TrackerState { LastSeen = new DateOnly(2013, 3, 10) };

        var events = new List<TrackerEvent>();
        for (int i = 1; i < tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 10), sink: events);
            Assert.Equal(new DateOnly(2013, 3, 10), state.LastSeen);
        }

        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 10), sink: events);

        Assert.Equal(new DateOnly(2026, 3, 10), state.LastSeen);
        Assert.Null(state.Candidate);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockResynced);
        Assert.Empty(events.Alerts());

        // And it works normally from there.
        var after = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: after);
        Assert.Equal("ENG", Assert.Single(after.Alerts()).Trigger!.Key.CountryCode);
    }

    [Fact]
    public void Resync_ReportsIntakesCrossedWhileWaitingToSwitch()
    {
        // The user keeps pressing Continue while the true date is being confirmed.
        // England opens on 14 March in the middle of that; it must not be lost.
        var tracker = Tracker(TestData.England);
        var state = new TrackerState { LastSeen = new DateOnly(2013, 3, 10) };

        int needed = tracker.Config.ResyncConfirmations;
        var events = new List<TrackerEvent>();
        for (int i = 0; i < needed; i++)
        {
            var date = i < needed / 2 ? new DateOnly(2026, 3, 10) : new DateOnly(2026, 3, 16);
            state = TestData.Commit(tracker, state, date, sink: events);
        }

        Assert.Equal(new DateOnly(2026, 3, 16), state.LastSeen);
        var alert = Assert.Single(events.Alerts());
        Assert.Equal(new TriggerKey("ENG", 2026, TriggerKind.WindowOpen), alert.Trigger!.Key);
        Assert.Equal(TriggerTiming.Late, alert.Timing);
        Assert.Contains(alert.Trigger.Key, state.Fired);
    }

    [Fact]
    public void Resync_IncludesAnIntakeOpeningOnTheFirstDayItSaw()
    {
        var tracker = Tracker(TestData.England);
        var state = new TrackerState { LastSeen = new DateOnly(2013, 8, 13) };

        var events = new List<TrackerEvent>();
        for (int i = 0; i < tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: events);
        }

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(TriggerTiming.OnTime, alert.Timing);
    }

    [Fact]
    public void Resync_ReloadInsideTheWait_OnlyReportsWhatIsStillCrossed()
    {
        // While the candidate is being confirmed the user goes past 14 March,
        // reloads to the 12th, then advances to the 13th. Net: nothing crossed.
        var tracker = Tracker(TestData.England);
        var state = new TrackerState { LastSeen = new DateOnly(2013, 3, 1) };

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 13), sink: events);
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 20), sink: events);
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 12), sink: events);
        for (int i = 3; i < tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 13), sink: events);
        }

        Assert.Equal(new DateOnly(2026, 3, 13), state.LastSeen);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockResynced);
        Assert.Empty(events.Alerts());
        Assert.Empty(state.Fired);
    }

    [Fact]
    public void LoadingASaveSeasonsEarlier_SwitchesAndReArmsThatSeason()
    {
        // A different career, or an old autosave. Alerts fired on the abandoned
        // timeline must not stay marked as fired for the one being switched to.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2025, 3, 13));
        state = TestData.Commit(tracker, state, new DateOnly(2025, 3, 14));
        state = TestData.Commit(tracker, state, new DateOnly(2026, 2, 1));
        state = TestData.Commit(tracker, state, new DateOnly(2027, 1, 1));
        Assert.Contains(state.Fired, k => k.OccurrenceYear == 2025);

        for (int i = 0; i < tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, new DateOnly(2025, 3, 1));
        }
        Assert.Equal(new DateOnly(2025, 3, 1), state.LastSeen);

        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2025, 3, 14), sink: events);
        Assert.Equal(2025, Assert.Single(events.Alerts()).Trigger!.Key.OccurrenceYear);
    }

    [Fact]
    public void FarReadingsThatDisagreeWithEachOther_NeverSwitch()
    {
        // Two different garbage years, each confirmed, alternating. Neither one
        // is a consistent timeline, so neither may take over.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 1));

        for (int i = 0; i < 5 * tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, i % 2 == 0 ? new DateOnly(2062, 3, 1) : new DateOnly(1990, 3, 1));
        }

        Assert.Equal(new DateOnly(2026, 3, 1), state.LastSeen);
        Assert.Equal(1, state.Candidate!.Confirmations);
    }

    [Fact]
    public void ConfirmedPlausibleReading_DropsTheCandidate()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 1));

        state = TestData.Commit(tracker, state, new DateOnly(2062, 3, 1));
        Assert.NotNull(state.Candidate);

        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 2));
        Assert.Null(state.Candidate);
        Assert.Equal(new DateOnly(2026, 3, 2), state.LastSeen);
    }

    [Fact]
    public void GarbageYearOne_DoesNotThrow()
    {
        var tracker = Tracker(new TrackerConfig { ResyncConfirmations = 1 }, TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 1));

        state = TestData.Commit(tracker, state, DateOnly.MinValue);

        Assert.Equal(DateOnly.MinValue, state.LastSeen);
    }

    // ------------------------------------------------------------- commit-on-confirm

    [Fact]
    public void SingleMisreadBetweenGoodReads_IsDiscarded()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 10));

        // One rogue frame claiming 17 March, then back to reality.
        var rogue = tracker.Step(state, TestData.Good(2026, 3, 17), _ => true);
        Assert.Empty(rogue.Events.Alerts());
        Assert.Equal(new DateOnly(2026, 3, 10), rogue.State.LastSeen);

        var back = tracker.Step(rogue.State, TestData.Good(2026, 3, 10), _ => true);
        Assert.Equal(new DateOnly(2026, 3, 10), back.State.LastSeen);
        Assert.Null(back.State.Pending);
    }

    [Fact]
    public void AlternatingFlicker_NeverAccumulatesEnoughConfirmationsToCommit()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 10));

        for (int i = 0; i < 20; i++)
        {
            state = tracker.Step(state, TestData.Good(2026, 3, 17), _ => true).State;
            state = tracker.Step(state, TestData.Good(2026, 3, 10), _ => true).State;
        }

        Assert.Equal(new DateOnly(2026, 3, 10), state.LastSeen);
    }

    // --------------------------------------------------------------- confidence

    [Theory]
    [InlineData(0.54, false)]
    [InlineData(0.56, true)]
    public void MeanConfidenceFloor_IsAppliedOnAZeroToOneScale(double confidence, bool shouldAccept)
    {
        // Guards against the 0..1 vs 0..100 mix-up, which rejects every sample
        // forever and presents as "the app just never does anything".
        var tracker = Tracker(new TrackerConfig { ConfirmSamples = 1 }, TestData.England);
        var observation = Observation.Read(new DateOnly(2026, 3, 14), "14/03/2026", confidence, 0.99);

        var result = tracker.Step(TrackerState.Initial, observation, _ => true);

        Assert.Equal(shouldAccept, result.State.LastSeen is not null);
        Assert.Equal(!shouldAccept, result.Events.Any(e => e.Kind == TrackerEventKind.SampleRejected));
    }

    [Fact]
    public void LowSingleSymbolConfidence_IsRejectedEvenWhenTheMeanLooksFine()
    {
        var tracker = Tracker(new TrackerConfig { ConfirmSamples = 1 }, TestData.England);
        var observation = Observation.Read(new DateOnly(2026, 3, 14), "14/03/2026", 0.95, 0.40);

        var result = tracker.Step(TrackerState.Initial, observation, _ => true);

        Assert.Null(result.State.LastSeen);
        Assert.Contains(result.Events, e => e.Kind == TrackerEventKind.SampleRejected);
    }

    [Theory]
    [InlineData(SampleStatus.GameNotRunning)]
    [InlineData(SampleStatus.CaptureFailed)]
    [InlineData(SampleStatus.NoDatePresent)]
    [InlineData(SampleStatus.Unreadable)]
    public void NonOkSamples_NeverTouchState(SampleStatus status)
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));

        var result = tracker.Step(state, new Observation { Status = status }, _ => true);

        Assert.Equal(state, result.State);
        Assert.Empty(result.Events.Alerts());
    }

    // ------------------------------------------------------------ save reloading

    [Fact]
    public void Regression_ReArmsCrossedTriggersAndFiresNothing()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14));
        Assert.Single(state.Fired);

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 13), sink: events);

        Assert.Empty(state.Fired);
        Assert.Empty(events.Alerts());
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockRegressed);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.TriggersReArmed);
    }

    [Fact]
    public void SaveScumLoop_ReFiresOnEveryReAdvance()
    {
        // Reload-to-reroll is the headline use case: the alert must survive it.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));

        for (int lap = 0; lap < 3; lap++)
        {
            var events = new List<TrackerEvent>();
            state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: events);
            Assert.Single(events.Alerts());

            state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 13));
        }
    }

    [Fact]
    public void Regression_LeavesUncrossedTriggersAlone()
    {
        var tracker = Tracker(TestData.England, TestData.Brazil);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14));

        // Step back a single day. Brazil (September) was never fired, so nothing
        // about it should change.
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 13));

        Assert.DoesNotContain(state.Fired, k => k.CountryCode == "BRA");
    }

    // ------------------------------------------------------- calendar edge cases

    [Fact]
    public void YearBoundaryWindow_FiresOnceInTheYearItOpens()
    {
        // Sweden's lower leagues open 25 December and close 5 January.
        var tracker = Tracker(TestData.SwedenLowerLeagues);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 12, 20));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2027, 1, 3), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(2026, alert.Trigger!.Key.OccurrenceYear);
        Assert.Equal(new DateOnly(2026, 12, 25), alert.Trigger.WindowOpen);
        Assert.Equal(new DateOnly(2027, 1, 5), alert.Trigger.WindowClose);
        // Landing on 3 January, the window is still open.
        Assert.Equal(TriggerTiming.Late, alert.Timing);
    }

    [Fact]
    public void LeapYear_MexicoWindowArmsOn28FebruaryAndSpans29th()
    {
        // 2028 is a leap year; Mexico runs 28 Feb to 2 Mar.
        var tracker = Tracker(TestData.Mexico);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2028, 2, 27));

        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2028, 2, 29), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(new DateOnly(2028, 2, 28), alert.Trigger!.WindowOpen);
        Assert.Equal(TriggerTiming.Late, alert.Timing);
    }

    [Fact]
    public void ConsecutiveYears_EachFireSeparately()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, new DateOnly(2026, 3, 13));

        var first = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, new DateOnly(2026, 3, 14), sink: first);
        Assert.Single(first.Alerts());

        state = TestData.Commit(tracker, state, new DateOnly(2026, 12, 1));
        state = TestData.Commit(tracker, state, new DateOnly(2027, 3, 13));

        var second = new List<TrackerEvent>();
        TestData.Commit(tracker, state, new DateOnly(2027, 3, 14), sink: second);
        var alert = Assert.Single(second.Alerts());
        Assert.Equal(2027, alert.Trigger!.Key.OccurrenceYear);
    }
}
