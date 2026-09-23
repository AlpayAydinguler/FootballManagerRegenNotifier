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

    public static readonly TrackerState Initial = new();
}

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
    /// Forward jumps larger than this are treated as a misread, not as a very
    /// long holiday. A full season of holidaying is legitimate; a jump of ten
    /// thousand days is a mangled year field.
    /// </summary>
    public int MaxJumpDays { get; init; } = 400;

    /// <summary>Mean confidence floor, 0..1.</summary>
    public double MinMeanConfidence { get; init; } = 0.55;

    /// <summary>
    /// Per-symbol confidence floor, 0..1. Catches the single bad glyph that a
    /// mean comfortably hides.
    /// </summary>
    public double MinSymbolConfidence { get; init; } = 0.70;

    public static readonly TrackerConfig Default = new();
}
