using System.Collections.Immutable;
using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.Core.Tracking;

/// <summary>
/// Everything the tracker remembers between samples. Immutable: <c>Step</c>
/// returns a new instance rather than mutating, which is what makes the whole
/// state machine testable without a screen, a timer or a game.
/// </summary>
public sealed record TrackerState
{
    /// <summary>The last date we actually committed to. Null before the first confirmed read.</summary>
    public DateOnly? LastSeen { get; init; }

    /// <summary>
    /// <see cref="LastSeen"/> came from state.json and has not been read on screen
    /// since. Such a date is only a hint: the game may have been left on another
    /// save, or the file may hold a misread from an earlier session, so the first
    /// confirmed reading that disagrees with it by more than the jump ceiling wins
    /// at once instead of waiting out <see cref="TrackerConfig.ResyncConfirmations"/>.
    /// A reading held back only for its misread-year shape still waits.
    /// </summary>
    public bool Unconfirmed { get; init; }

    /// <summary>
    /// Announce windows already open when the next date is adopted. Set by
    /// <see cref="AfterReset"/>: a player who resets has just switched saves on
    /// purpose, and should hear about them exactly as an automatic switch would.
    /// </summary>
    public bool AnnounceOpenWindows { get; init; }

    /// <summary>A date seen but not yet confirmed. See <c>TrackerConfig.ConfirmSamples</c>.</summary>
    public DateOnly? Pending { get; init; }

    public int PendingCount { get; init; }

    /// <summary>
    /// Occurrences already alerted. Cleared selectively when the clock runs
    /// backwards, and rebuilt on a re-sync (see <c>DateTracker.Resync</c>).
    /// </summary>
    public ImmutableHashSet<TriggerKey> Fired { get; init; } = [];

    /// <summary>
    /// Confirmed readings too far from <see cref="LastSeen"/> to believe, but
    /// consistent with each other. Null when there are none. In-memory only.
    /// </summary>
    public ResyncCandidate? Candidate { get; init; }

    /// <summary>
    /// The timeline the last re-sync switched away from. In-memory only.
    /// </summary>
    /// <remarks>
    /// Switching away is not always final: a misread that held long enough to
    /// win gives way again when the real date comes back, and a player can load
    /// one save, then go back to the other. Remembering where that timeline was
    /// left, and what it had already alerted, lets the return pick up from there
    /// instead of starting blind, so what it crossed in the meantime is reported
    /// once and nothing already alerted is repeated. Only the most recent one is
    /// kept.
    /// </remarks>
    public Timeline? Abandoned { get; init; }

    public static readonly TrackerState Initial = new();

    /// <summary>The state <c>Reset date</c> leaves: nothing known, open windows announced on adoption.</summary>
    public static readonly TrackerState AfterReset = new() { AnnounceOpenWindows = true };
}

/// <summary>A committed date and the alerts given on the way to it.</summary>
/// <param name="Restored">
/// The date came from state.json and was never seen on screen, so the game may
/// have been played on past it while the app was closed.
/// </param>
public sealed record Timeline(DateOnly LastSeen, ImmutableHashSet<TriggerKey> Fired, bool Restored = false);

/// <summary>
/// A second timeline, built from readings the jump ceiling refused.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling cannot tell a misread from the truth; it can only tell that two
/// dates are far apart. If the committed date is the misread (a year read as 2013
/// and committed, or a save loaded two seasons ahead), every correct reading
/// afterwards is refused as well, forever, and the app goes quiet. So refused
/// readings are kept here instead of dropped, and once they have agreed with each
/// other for long enough without the committed date showing up again, they win.
/// </para>
/// <para>
/// <see cref="Floor"/> is what makes the switch-over lose nothing. Whatever the
/// clock crossed while this timeline was being confirmed lies between the lowest
/// date it visited and <see cref="Latest"/>: a reload inside the wait moves the
/// floor down, exactly as a reload on the committed timeline re-arms.
/// </para>
/// </remarks>
public sealed record ResyncCandidate(DateOnly First, DateOnly Floor, DateOnly Latest, int Confirmations);

public sealed record TrackerConfig
{
    /// <summary>
    /// How many consecutive identical reads are required before a new date is
    /// committed.
    /// </summary>
    /// <remarks>
    /// This is the guard against a single misread silently marking a trigger as
    /// crossed — the worst outcome available, because the alert then never fires
    /// and nothing anywhere indicates that anything went wrong. Two is enough in
    /// practice: the game holds a date on screen for seconds at a time, so the
    /// cost is one extra sample, while an isolated glitch is discarded.
    /// </remarks>
    public int ConfirmSamples { get; init; } = 2;

    /// <summary>
    /// Jumps larger than this, in either direction, are treated as a misread, not
    /// as a very long holiday or a reload. A full season of holidaying is
    /// legitimate; a jump of ten thousand days is a mangled year field.
    /// </summary>
    /// <remarks>
    /// Backwards counts too. A misread year is as likely to land in the past as
    /// in the future, and a reload that goes back more than a season is rare
    /// enough to wait out <see cref="ResyncConfirmations"/> for.
    /// </remarks>
    public int MaxJumpDays { get; init; } = 400;

    /// <summary>
    /// A jump that lands within this many days of the same calendar date in
    /// another year is treated as a misread year, however short it is.
    /// </summary>
    /// <remarks>
    /// A misread year keeps the day and the month, and the likeliest one is a
    /// single wrong last digit: 2025 read as 2026 is only 365 days, well inside
    /// <see cref="MaxJumpDays"/>, and would otherwise fire a whole year of alerts
    /// on every flicker. Normal play never produces that shape; the rare honest
    /// case, a holiday of about a year, just waits out a re-sync.
    /// </remarks>
    public int YearMisreadToleranceDays { get; init; } = 7;

    /// <summary>
    /// How far the clock can move on from a timeline and still be taken as that
    /// timeline carrying on: when a re-sync comes back to one it left (see
    /// <see cref="TrackerState.Abandoned"/>), and when a reading close to the
    /// committed date is counted towards a candidate instead.
    /// </summary>
    /// <remarks>
    /// Room for the clock to keep moving while a misread holds the tracked date,
    /// or during the wait for a switch, but no more: a different save a few months
    /// on is not a continuation, and treating it as one would report every intake
    /// in between as missed.
    /// </remarks>
    public int ReturnWindowDays { get; init; } = 60;

    /// <summary>
    /// How many confirmed readings the jump ceiling refused, consistent with each
    /// other, it takes to give up on the committed date and re-sync.
    /// </summary>
    /// <remarks>
    /// Each confirmation is <see cref="ConfirmSamples"/> identical reads, so the
    /// default is about twenty seconds at one sample a second. Long enough that a
    /// misread has to be all the app sees for that whole time; short enough that
    /// loading a save in another year does not look like the app has died. The
    /// count starts again on a single read of the committed date, on a confirmed
    /// reading close to it, and on a refused reading too far from the candidate or
    /// in the misread-year shape against it.
    /// </remarks>
    public int ResyncConfirmations { get; init; } = 10;

    /// <summary>Mean confidence floor, 0..1.</summary>
    public double MinMeanConfidence { get; init; } = 0.55;

    /// <summary>
    /// Per-symbol confidence floor, 0..1. Catches the single bad glyph that a
    /// mean comfortably hides.
    /// </summary>
    public double MinSymbolConfidence { get; init; } = 0.70;

    public static readonly TrackerConfig Default = new();
}
