using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Settings;
using FootballManagerRegenNotifier.Core.Tracking;
using FootballManagerRegenNotifier.Core.Triggers;
using Xunit;

namespace FootballManagerRegenNotifier.Core.Tests;

/// <summary>
/// Misread years, and players moving between saves: the cases where the date on
/// screen jumps a long way and the tracker has to decide which date to believe.
/// </summary>
public class ResyncTests
{
    private static DateTracker Tracker(params CountryRule[] rules) =>
        new(new TriggerCalendar(rules));

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>Holds a date on screen for as many confirmations as a re-sync needs.</summary>
    private static TrackerState SwitchTo(DateTracker tracker, TrackerState state, DateOnly date, List<TrackerEvent>? sink = null)
    {
        for (int i = 0; i < tracker.Config.ResyncConfirmations; i++)
        {
            state = TestData.Commit(tracker, state, date, sink: sink);
        }
        return state;
    }

    // ------------------------------------------------------- year-only misreads

    [Fact]
    public void OneYearMisread_Flickering_NeverFiresAYearOfAlerts()
    {
        // 2025 read as 2026 is only 365 days: inside the jump ceiling. Before, each
        // flicker committed it and fired every intake of the year skipped, and the
        // next correct read re-armed them all for the next flicker.
        var tracker = Tracker(TestData.England, TestData.Brazil, TestData.Mexico, TestData.SwedenLowerLeagues);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 3, 1));

        var events = new List<TrackerEvent>();
        for (int i = 0; i < 5; i++)
        {
            state = TestData.Commit(tracker, state, D(2026, 3, 1), sink: events);
            state = tracker.Step(state, TestData.Good(2025, 3, 1), _ => true).State;
        }

        Assert.Empty(events.Alerts());
        Assert.Equal(D(2025, 3, 1), state.LastSeen);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.JumpQuarantined
                                     && e.Message.Contains("same date in another year", StringComparison.Ordinal));
    }

    [Fact]
    public void OneYearMisread_OnTheDayAfterAContinue_IsStillHeldBack()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 8, 13));

        state = TestData.Commit(tracker, state, D(2026, 8, 14));

        Assert.Equal(D(2025, 8, 13), state.LastSeen);
    }

    [Fact]
    public void OrdinaryAdvanceOverNewYear_IsNotMistakenForAMisreadYear()
    {
        var tracker = Tracker(TestData.SwedenLowerLeagues);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 12, 20));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, D(2026, 1, 2), sink: events);

        Assert.Equal(D(2026, 1, 2), state.LastSeen);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockAdvanced);
        Assert.Single(events.Alerts());
    }

    [Fact]
    public void HolidayOfAboutAYear_IsReportedOnceItHasHeld()
    {
        // The honest case with the misread-year shape. It is held back, and once it
        // has held it comes out exactly as the jump would have: every intake
        // crossed, labelled with how late it is.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 3, 10));

        var events = new List<TrackerEvent>();
        state = SwitchTo(tracker, state, D(2026, 3, 16), events);

        Assert.Equal(D(2026, 3, 16), state.LastSeen);
        var alerts = events.Alerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Trigger!.Key.OccurrenceYear == 2025 && a.Timing == TriggerTiming.Missed);
        Assert.Contains(alerts, a => a.Trigger!.Key.OccurrenceYear == 2026 && a.Timing == TriggerTiming.Late);
    }

    // ------------------------------------------------- a misread that held and lost

    [Fact]
    public void MisreadYearOnAnIntakeDay_ThereAndBack_AlertsOnce()
    {
        // Sitting on England's opening day, already alerted; the year misreads as
        // 2013 for long enough to win, then reads correctly again. Nothing about
        // that is a new intake.
        var tracker = Tracker(TestData.England);
        var events = new List<TrackerEvent>();
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 3, 13));
        state = TestData.Commit(tracker, state, D(2025, 3, 14), sink: events);

        state = SwitchTo(tracker, state, D(2013, 3, 14), events);
        Assert.Equal(D(2013, 3, 14), state.LastSeen);

        state = SwitchTo(tracker, state, D(2025, 3, 14), events);

        Assert.Equal(D(2025, 3, 14), state.LastSeen);
        Assert.Single(events.Alerts());
    }

    [Fact]
    public void MisreadYearThatWon_ThenAContinueClick_StillReportsTheIntakeCrossed()
    {
        // The real clock moves on while the misread is the tracked date, and the
        // first correct reading afterwards is past England's opening day.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 3, 10));
        state = TestData.Commit(tracker, state, D(2025, 3, 11));
        state = SwitchTo(tracker, state, D(2013, 3, 11));

        var events = new List<TrackerEvent>();
        state = SwitchTo(tracker, state, D(2025, 3, 20), events);

        Assert.Equal(D(2025, 3, 20), state.LastSeen);
        var alert = Assert.Single(events.Alerts());
        Assert.Equal(new TriggerKey("ENG", 2025, TriggerKind.WindowOpen), alert.Trigger!.Key);
        Assert.Equal(TriggerTiming.Late, alert.Timing);
    }

    [Fact]
    public void MisreadYearThatKeepsUpWithTheClock_AlertsOnTheRightDayAndOnlyOnce()
    {
        // A misread that sticks while play continues still sees the right day and
        // month, so the intake fires on the right day with the wrong year. Coming
        // back must not announce it a second time.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2025, 3, 10));
        state = SwitchTo(tracker, state, D(2013, 3, 10));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, D(2013, 3, 14), sink: events);
        Assert.Equal(2013, Assert.Single(events.Alerts()).Trigger!.Key.OccurrenceYear);

        state = SwitchTo(tracker, state, D(2025, 3, 16), events);

        Assert.Equal(D(2025, 3, 16), state.LastSeen);
        Assert.Single(events.Alerts());
    }

    [Fact]
    public void GarbageDateThatWon_ThenTheRealClock_ReportsWhatItCrossedOnce()
    {
        // Something date-shaped that is not the clock (a tooltip, another panel)
        // held the region for twenty seconds while the user played on.
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2026, 3, 10));
        state = SwitchTo(tracker, state, D(2030, 6, 30));

        var events = new List<TrackerEvent>();
        state = SwitchTo(tracker, state, D(2026, 3, 16), events);

        Assert.Equal(D(2026, 3, 16), state.LastSeen);
        var alert = Assert.Single(events.Alerts());
        Assert.Equal(2026, alert.Trigger!.Key.OccurrenceYear);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockResynced
                                     && e.Message.Contains("10/03/2026", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------- saved state.json

    [Fact]
    public void MisreadSavedInStateJson_IsReplacedByTheFirstConfirmedReading()
    {
        // The reported state: an earlier version committed 2013 and saved it.
        var restored = SettingsStore.ToTrackerState(new RuntimeState { LastSeenInGameDate = D(2013, 8, 13) });
        Assert.True(restored.Unconfirmed);

        var tracker = Tracker(TestData.England);
        var events = new List<TrackerEvent>();
        var state = TestData.Commit(tracker, restored, D(2025, 11, 2), sink: events);

        Assert.Equal(D(2025, 11, 2), state.LastSeen);
        Assert.False(state.Unconfirmed);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.ClockResynced);
    }

    [Fact]
    public void SavedDate_ReadAgain_IsTrustedAgainstALaterMisread()
    {
        var restored = SettingsStore.ToTrackerState(new RuntimeState { LastSeenInGameDate = D(2025, 3, 10) });
        var tracker = Tracker(TestData.England);

        var state = tracker.Step(restored, TestData.Good(2025, 3, 10), _ => true).State;
        Assert.False(state.Unconfirmed);

        state = TestData.Commit(tracker, state, D(2013, 3, 10));
        Assert.Equal(D(2025, 3, 10), state.LastSeen);
    }

    [Fact]
    public void SavedDate_PlausibleMove_StillReportsWhatWasCrossedWhileTheAppWasClosed()
    {
        var restored = SettingsStore.ToTrackerState(new RuntimeState { LastSeenInGameDate = D(2025, 3, 10) });
        var tracker = Tracker(TestData.England);

        var events = new List<TrackerEvent>();
        var state = TestData.Commit(tracker, restored, D(2025, 3, 20), sink: events);

        Assert.Equal(D(2025, 3, 20), state.LastSeen);
        Assert.Equal(TriggerTiming.Late, Assert.Single(events.Alerts()).Timing);
    }

    [Fact]
    public void SavedDateOnAnotherSave_OpenWindowIsAnnouncedOnSwitching()
    {
        var restored = SettingsStore.ToTrackerState(new RuntimeState { LastSeenInGameDate = D(2029, 8, 1) });
        var tracker = Tracker(TestData.England);

        var events = new List<TrackerEvent>();
        TestData.Commit(tracker, restored, D(2026, 3, 20), sink: events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(new TriggerKey("ENG", 2026, TriggerKind.WindowOpen), alert.Trigger!.Key);
        Assert.Equal(TriggerTiming.Late, alert.Timing);
    }

    // ----------------------------------------------------------- switching saves

    [Fact]
    public void SwitchingBetweenTwoSavesYearsApart_BackAndForth_EachSaveAlertsOnce()
    {
        // Two careers, 2026 and 2031, played alternately in one session.
        var tracker = Tracker(TestData.England, TestData.Brazil);
        var events = new List<TrackerEvent>();

        var state = TestData.Commit(tracker, TrackerState.Initial, D(2026, 3, 10), sink: events);

        state = SwitchTo(tracker, state, D(2031, 9, 15), events);
        Assert.Equal(D(2031, 9, 15), state.LastSeen);
        state = TestData.Commit(tracker, state, D(2031, 9, 25), sink: events);

        state = SwitchTo(tracker, state, D(2026, 3, 10), events);
        Assert.Equal(D(2026, 3, 10), state.LastSeen);
        state = TestData.Commit(tracker, state, D(2026, 3, 15), sink: events);

        state = SwitchTo(tracker, state, D(2031, 9, 25), events);
        state = TestData.Commit(tracker, state, D(2031, 10, 1), sink: events);

        state = SwitchTo(tracker, state, D(2026, 3, 15), events);

        Assert.Equal(D(2026, 3, 15), state.LastSeen);
        var alerts = events.Alerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Trigger!.Key == new TriggerKey("BRA", 2031, TriggerKind.WindowOpen));
        Assert.Contains(alerts, a => a.Trigger!.Key == new TriggerKey("ENG", 2026, TriggerKind.WindowOpen));
        Assert.Equal(4, events.Count(e => e.Kind == TrackerEventKind.ClockResynced));
    }

    [Fact]
    public void SwitchingToASaveInsideAnOpenWindow_AnnouncesIt()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2029, 8, 1));

        var events = new List<TrackerEvent>();
        SwitchTo(tracker, state, D(2026, 3, 20), events);

        var alert = Assert.Single(events.Alerts());
        Assert.Equal(TriggerTiming.Late, alert.Timing);
        Assert.Contains("6 day(s) ago", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewCareerAfterALongSave_ReportsNothingForTheGapAndThenWorks()
    {
        var tracker = Tracker(TestData.England, TestData.Brazil);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2031, 5, 1));
        state = TestData.Commit(tracker, state, D(2031, 10, 1));
        Assert.NotEmpty(state.Fired);

        var events = new List<TrackerEvent>();
        state = SwitchTo(tracker, state, D(2025, 7, 1), events);
        Assert.Empty(events.Alerts());

        state = TestData.Commit(tracker, state, D(2025, 9, 22), sink: events);
        state = TestData.Commit(tracker, state, D(2026, 3, 14), sink: events);

        var alerts = events.Alerts();
        Assert.Equal(["BRA:2025:WindowOpen", "ENG:2026:WindowOpen"], alerts.Select(a => a.Trigger!.Key.ToString()));
        Assert.Equal(D(2026, 3, 14), state.LastSeen);
    }

    [Fact]
    public void SaveSwitchWithinAYear_EarlierSave_IsAReloadAtOnce()
    {
        var tracker = Tracker(TestData.England);
        var state = TestData.Commit(tracker, TrackerState.Initial, D(2026, 3, 1));
        state = TestData.Commit(tracker, state, D(2026, 6, 1));

        var events = new List<TrackerEvent>();
        state = TestData.Commit(tracker, state, D(2026, 2, 1), sink: events);

        Assert.Equal(D(2026, 2, 1), state.LastSeen);
        Assert.Contains(events, e => e.Kind == TrackerEventKind.TriggersReArmed);
    }

    // ------------------------------------------------------------ garbage years

    [Fact]
    public void ExtremeYears_NeverThrow()
    {
        var tracker = new DateTracker(new TriggerCalendar([TestData.England, TestData.SwedenLowerLeagues], leadDays: 7));
        DateOnly[] dates =
        [
            D(2026, 3, 1), DateOnly.MinValue, D(1, 1, 2), D(1, 12, 31), D(2, 1, 1),
            DateOnly.MaxValue, D(9999, 1, 1), D(9998, 12, 31), D(2026, 3, 14),
        ];

        foreach (var first in dates)
        {
            foreach (var second in dates)
            {
                var state = TestData.Commit(tracker, TrackerState.Initial, first);
                state = SwitchTo(tracker, state, second);
                state = TestData.Commit(tracker, state, second);
                Assert.Equal(second, state.LastSeen);
            }
        }

        var calendar = new TriggerCalendar([TestData.SwedenLowerLeagues], leadDays: 7);
        Assert.Null(calendar.Next(DateOnly.MaxValue));
        Assert.Empty(calendar.Crossings(DateOnly.MinValue, D(1, 12, 31)));
    }

    // ------------------------------------------------------------- properties

    private static readonly CountryRule[] AllRules =
        [TestData.England, TestData.Brazil, TestData.Mexico, TestData.SwedenLowerLeagues];

    /// <summary>A random but realistic-ish reading, weighted towards the awkward cases.</summary>
    internal static Observation RandomObservation(Random rng, DateOnly anchor)
    {
        int roll = rng.Next(100);
        if (roll < 8) return new Observation { Status = (SampleStatus)rng.Next(1, 5) };
        if (roll < 12) return Observation.Read(anchor, "x", 0.2, 0.1);

        DateOnly date = roll switch
        {
            < 45 => anchor.AddDays(rng.Next(0, 8)),
            < 60 => anchor.AddDays(-rng.Next(1, 60)),
            < 70 => anchor.AddDays(rng.Next(30, 400)),
            < 80 => D(Math.Clamp(anchor.Year + rng.Next(-15, 16), 1, 9999), anchor.Month, Math.Min(anchor.Day, 28)),
            < 90 => D(rng.Next(1990, 2070), rng.Next(1, 13), rng.Next(1, 29)),
            < 95 => rng.Next(2) == 0 ? DateOnly.MinValue : DateOnly.MaxValue,
            _ => anchor,
        };
        return TestData.Good(date);
    }

    /// <summary>
    /// Readings arrive in runs, as they do on screen: the game holds a date for a
    /// while, and a misread tends to repeat. Some runs are long enough to win a
    /// re-sync, so every path through it gets exercised.
    /// </summary>
    internal static IEnumerable<Observation> RandomSession(Random rng, int samples)
    {
        var anchor = D(2025, 7, 1);
        int produced = 0;
        while (produced < samples)
        {
            var observation = RandomObservation(rng, anchor);
            int run = rng.Next(10) == 0 ? rng.Next(18, 26) : rng.Next(1, 5);
            for (int i = 0; i < run && produced < samples; i++, produced++) yield return observation;

            if (observation.Date is { } d && d.Year is > 1900 and < 2100 && rng.Next(3) == 0) anchor = d;
        }
    }

    [Fact]
    public void Property_FromAnyReachableState_ASteadyDateIsAlwaysAdopted()
    {
        // The reported bug was a state the tracker could never leave. Whatever
        // happened before, holding one date on screen long enough must win.
        var tracker = Tracker(AllRules);
        int needed = tracker.Config.ConfirmSamples * (tracker.Config.ResyncConfirmations + 1);

        for (int seed = 0; seed < 400; seed++)
        {
            var rng = new Random(seed);
            var state = rng.Next(3) == 0
                ? SettingsStore.ToTrackerState(new RuntimeState { LastSeenInGameDate = D(rng.Next(2000, 2050), 6, 1) })
                : TrackerState.Initial;

            foreach (var observation in RandomSession(rng, 300))
            {
                state = tracker.Step(state, observation, _ => true).State;
            }

            var target = rng.Next(2) == 0 && state.LastSeen is { } seen && seen.Year < 9999
                ? seen.AddDays(rng.Next(0, 5))
                : D(rng.Next(1995, 2060), rng.Next(1, 13), rng.Next(1, 29));
            for (int i = 0; i < needed; i++)
            {
                state = tracker.Step(state, TestData.Good(target), _ => true).State;
            }

            Assert.True(target == state.LastSeen, $"seed {seed}: held {target} but tracker is at {state.LastSeen}");
        }
    }

    public enum Origin { Fresh, Held, YearShift, Return }

    [Theory]
    [InlineData(Origin.Fresh)]
    [InlineData(Origin.Held)]
    [InlineData(Origin.YearShift)]
    [InlineData(Origin.Return)]
    public void Property_AResyncIsExactlyAPlainTrackerRunFromItsOrigin(Origin kind)
    {
        // A re-sync claims to replay the candidate's readings from an origin as
        // Advance and Regress would have. Check that against a tracker with no
        // ceiling at all, started at that origin and fed the same readings. The
        // only difference allowed is announcing windows already open on arrival.
        var calendar = new TriggerCalendar(AllRules, leadDays: 3);
        var tracker = new DateTracker(calendar);
        var plain = new DateTracker(calendar, new TrackerConfig { MaxJumpDays = int.MaxValue, YearMisreadToleranceDays = -1 });
        Func<CountryRule, bool> armed = rule => rule.Code != "BRA";

        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed * 7919 + (int)kind);
            var home = D(2026, 1, 1).AddDays(rng.Next(0, 365));

            var state = TestData.Commit(tracker, TrackerState.Initial, home.AddDays(-rng.Next(30, 300)), armed);
            state = TestData.Commit(tracker, state, home, armed);
            var homeState = state;

            TrackerState origin;
            DateOnly first;
            DateOnly heldAround = default;
            switch (kind)
            {
                case Origin.Held:
                    int year = home.Year + (rng.Next(2) == 0 ? 1 : -1);
                    heldAround = D(year, home.Month, Math.Min(home.Day, DateTime.DaysInMonth(year, home.Month)));
                    first = heldAround.AddDays(rng.Next(-3, 4));
                    origin = new TrackerState { LastSeen = home, Fired = state.Fired };
                    break;
                case Origin.YearShift:
                    int years = rng.Next(2, 20) * (rng.Next(2) == 0 ? 1 : -1);
                    var shifted = D(home.Year + years, home.Month, Math.Min(home.Day, DateTime.DaysInMonth(home.Year + years, home.Month)));
                    first = shifted.AddDays(rng.Next(-3, 4));
                    origin = new TrackerState
                    {
                        LastSeen = shifted,
                        Fired = [.. state.Fired.Select(k => k with { OccurrenceYear = k.OccurrenceYear + years })],
                    };
                    break;
                case Origin.Return:
                    state = SwitchTo(tracker, state, home.AddDays(3000 + rng.Next(0, 200)));
                    first = home.AddDays(rng.Next(-40, 60));
                    origin = new TrackerState { LastSeen = home, Fired = homeState.Fired };
                    break;
                default:
                    // Clear of the same time of year, or it is a year shift instead.
                    do first = home.AddDays(2000 + rng.Next(0, 2000));
                    while (NearSameDateInAnotherYear(first, home));
                    origin = new TrackerState { LastSeen = first.AddDays(-1) };
                    break;
            }

            // The candidate's own moves: small steps, some reloads, and for a
            // candidate held only for its shape, never far enough to lose it.
            var readings = new List<DateOnly> { first };
            while (readings.Count < tracker.Config.ResyncConfirmations)
            {
                var last = readings[^1];
                readings.Add(kind == Origin.Held
                    ? heldAround.AddDays(rng.Next(-3, 4))
                    : rng.Next(4) == 0 ? last.AddDays(-rng.Next(1, 30)) : last.AddDays(rng.Next(0, 9)));
            }

            var ours = new List<TrackerEvent>();
            foreach (var d in readings) state = TestData.Commit(tracker, state, d, armed, ours);

            var reference = new List<TrackerEvent>();
            var expected = origin;
            foreach (var d in readings) expected = TestData.Commit(plain, expected, d, armed, reference);

            var resync = ours.FindLast(e => e.Kind == TrackerEventKind.ClockResynced);
            Assert.True(resync is not null, $"{kind} seed {seed}: no re-sync");
            string marker = kind switch
            {
                Origin.Held => "It looked like a misread year",
                Origin.YearShift => "Only the year differs",
                Origin.Return => "Picking up from",
                _ => "was a misread or a different save",
            };
            Assert.True(resync.Message.Contains(marker, StringComparison.Ordinal), $"{kind} seed {seed}: took another path: {resync.Message}");
            Assert.Equal(readings[^1], state.LastSeen);

            var floor = readings.Append(origin.LastSeen!.Value).Min();
            var extra = state.Fired.Except(expected.Fired);
            Assert.True(expected.Fired.IsSubsetOf(state.Fired), $"{kind} seed {seed}: lost {string.Join(",", expected.Fired.Except(state.Fired))}");
            foreach (var key in extra)
            {
                var t = calendar.Materialise([key.OccurrenceYear]).Single(m => m.Key == key);
                Assert.True(t.Key.Kind == TriggerKind.WindowOpen && t.FireOn <= floor && t.TimingAt(readings[^1]) == TriggerTiming.Late,
                    $"{kind} seed {seed}: unexpected {key} fireOn={t.FireOn} close={t.WindowClose} floor={floor} "
                    + $"latest={readings[^1]} origin={origin.LastSeen} readings={string.Join(" ", readings)} "
                    + $"events={string.Join(" | ", ours.Where(e => e.Kind is TrackerEventKind.ClockResynced or TrackerEventKind.AlertRaised).Select(e => e.Message))}");
            }

            var ourAlerts = ours.Alerts().Select(a => a.Trigger!.Key).ToHashSet();
            var refAlerts = reference.Alerts().Select(a => a.Trigger!.Key).ToHashSet();
            // A crossing the candidate undid by reloading before it is not reported:
            // it is re-armed, exactly as on the committed timeline, and fires when
            // the clock crosses it again. Everything still crossed must be there.
            var missing = refAlerts.Except(ourAlerts).ToList();
            Assert.True(missing.TrueForAll(k => !expected.Fired.Contains(k)),
                $"{kind} seed {seed}: lost {string.Join(",", missing)} readings={string.Join(" ", readings)}");
            Assert.True(ourAlerts.Except(refAlerts).All(extra.Contains), $"{kind} seed {seed}: alert not explained");
        }
    }

    private static bool NearSameDateInAnotherYear(DateOnly a, DateOnly b)
    {
        for (int year = a.Year - 1; year <= a.Year + 1; year++)
        {
            var shifted = D(year, b.Month, Math.Min(b.Day, DateTime.DaysInMonth(year, b.Month)));
            if (Math.Abs(a.DayNumber - shifted.DayNumber) <= 10) return true;
        }
        return false;
    }

    [Fact]
    public void Property_EveryAlertIsRecordedAndNoneRepeatsWithinAStep()
    {
        var tracker = new DateTracker(new TriggerCalendar(AllRules, leadDays: 5));

        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var state = TrackerState.Initial;

            foreach (var observation in RandomSession(rng, 400))
            {
                var result = tracker.Step(state, observation, rule => rule.Code != "MEX");
                state = result.State;

                var keys = result.Events.Alerts().Select(a => a.Trigger!.Key).ToList();
                Assert.Equal(keys.Count, keys.Distinct().Count());
                Assert.All(keys, k => Assert.Contains(k, state.Fired));
                Assert.DoesNotContain(keys, k => k.CountryCode == "MEX");
            }
        }
    }
}
