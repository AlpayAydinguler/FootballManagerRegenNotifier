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

    /// <summary>A date seen but not yet confirmed. See <c>TrackerConfig.ConfirmSamples</c>.</summary>
    public DateOnly? Pending { get; init; }

    public int PendingCount { get; init; }

    /// <summary>Occurrences already alerted. Cleared selectively when the clock runs backwards.</summary>
    public ImmutableHashSet<TriggerKey> Fired { get; init; } = [];

    /// <summary>
    /// Confirmed readings too far from <see cref="LastSeen"/> to believe, but
    /// consistent with each other. Null when there are none. In-memory only.
    /// </summary>
    public ResyncCandidate? Candidate { get; init; }

    public static readonly TrackerState Initial = new();
}

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
/// <see cref="Since"/> makes the switch-over lose nothing. Whatever the clock
/// crossed while this timeline was being confirmed is exactly the triggers in
/// <c>(Since, Latest]</c>: it starts the day before the first reading, and drops
/// to any earlier date the timeline is later reloaded to.
/// </para>
/// </remarks>
public sealed record ResyncCandidate(DateOnly Since, DateOnly Latest, int Confirmations);

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
    /// How many confirmed readings beyond <see cref="MaxJumpDays"/>, consistent
    /// with each other, it takes to give up on the committed date and re-sync.
    /// </summary>
    /// <remarks>
    /// Each confirmation is <see cref="ConfirmSamples"/> identical reads, so the
    /// default is about twenty seconds at one sample a second. Long enough that a
    /// misread has to be all the app sees for that whole time; short enough that
    /// loading a save in another year does not look like the app has died. A
    /// single read of the committed date in between starts the count again.
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
