namespace FootballManagerRegenNotifier.Core.Model;

public enum SampleStatus
{
    /// <summary>A date was read and parsed. The only status that may mutate tracker state.</summary>
    Ok = 0,

    /// <summary>Captured fine, but the region held no date-shaped text (menu, modal, loading screen).</summary>
    NoDatePresent = 1,

    /// <summary>Text came back but would not parse, or confidence was too low to trust.</summary>
    Unreadable = 2,

    /// <summary>Football Manager is not running / not in the foreground. Nothing was captured.</summary>
    GameNotRunning = 3,

    /// <summary>The screen grab itself failed, or returned a uniform (black) frame.</summary>
    CaptureFailed = 4,
}

/// <summary>
/// One reading of the on-screen clock.
/// </summary>
/// <remarks>
/// Confidence is 0..1 on both fields, always. The underlying engine is not
/// consistent about this — Tesseract's <c>GetMeanConfidence()</c> returns 0..1
/// while <c>ResultIterator.GetConfidence()</c> returns a raw 0..100 — so the
/// normalisation happens once, at the adapter boundary, and everything above
/// this type can assume a single scale. Mixing the two scales produces a
/// confidence floor that rejects every sample forever, which presents to the
/// user as "the app simply never does anything".
/// </remarks>
public sealed record Observation
{
    public required SampleStatus Status { get; init; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SampleStatus.Ok"/>.</summary>
    public DateOnly? Date { get; init; }

    public string RawText { get; init; } = string.Empty;

    /// <summary>Mean confidence across the line, 0..1.</summary>
    public double MeanConfidence { get; init; }

    /// <summary>
    /// Lowest per-symbol confidence, 0..1. More useful than the mean for this
    /// job: one badly-formed glyph in "01/07/2026" is exactly the 7-vs-1 failure
    /// we care about, and it barely moves the mean.
    /// </summary>
    public double MinSymbolConfidence { get; init; }

    public string? FailureDetail { get; init; }

    public static Observation Read(DateOnly date, string raw, double mean, double minSymbol) =>
        new() { Status = SampleStatus.Ok, Date = date, RawText = raw, MeanConfidence = mean, MinSymbolConfidence = minSymbol };

    public static Observation NoDate(string raw) =>
        new() { Status = SampleStatus.NoDatePresent, RawText = raw };

    public static Observation Unreadable(string raw, string? detail = null) =>
        new() { Status = SampleStatus.Unreadable, RawText = raw, FailureDetail = detail };

    public static Observation GameNotRunning() =>
        new() { Status = SampleStatus.GameNotRunning };

    public static Observation CaptureFailed(string detail) =>
        new() { Status = SampleStatus.CaptureFailed, FailureDetail = detail };
}
