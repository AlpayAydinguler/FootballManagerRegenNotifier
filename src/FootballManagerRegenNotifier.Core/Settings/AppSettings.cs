using FootballManagerRegenNotifier.Core.Parsing;

namespace FootballManagerRegenNotifier.Core.Settings;

/// <summary>
/// The capture rectangle, stored as the two corner points the UI exposes.
/// </summary>
/// <remarks>
/// These are <b>physical</b> pixels on the virtual desktop, which is why the app
/// declares PerMonitorV2 DPI awareness. Without that declaration Windows hands a
/// scaled process virtualised coordinates and the grab silently lands somewhere
/// other than where the user drew it.
/// </remarks>
public sealed record CaptureRect
{
    public int TopLeftX { get; init; } = 2035;
    public int TopLeftY { get; init; } = 5;
    public int BottomRightX { get; init; } = 2180;
    public int BottomRightY { get; init; } = 40;

    public int Width => Math.Abs(BottomRightX - TopLeftX);
    public int Height => Math.Abs(BottomRightY - TopLeftY);
    public int Left => Math.Min(TopLeftX, BottomRightX);
    public int Top => Math.Min(TopLeftY, BottomRightY);

    public bool IsValid => Width > 0 && Height > 0;

    public static readonly CaptureRect Default = new();
}

public enum ThresholdMode
{
    /// <summary>8-bit grayscale, no threshold applied.</summary>
    Grayscale = 0,

    /// <summary>Hard black and white at the threshold. The validated default.</summary>
    BlackAndWhite = 1,
}

public enum InvertMode
{
    /// <summary>Decide from the image itself, by dark-pixel fraction.</summary>
    Auto = 0,
    Never = 1,
    Always = 2,
}

public enum OcrEngineKind
{
    Tesseract = 0,

    /// <summary>Deterministic glyph template matching. Needs calibration first.</summary>
    TemplateMatch = 1,
}

/// <summary>
/// The image preprocessing and OCR parameters exposed in the tuning panel.
/// </summary>
/// <remarks>
/// Defaults are the settings validated by hand against a real FM26 screen:
/// 4x nearest-neighbour upscale, threshold 128, hard black and white, digits and
/// slash only. Inversion and the quiet zone are additions, and they are the two
/// that actually decide whether a 7 survives as a 7 — see <c>Preprocessor</c>.
/// </remarks>
public sealed record OcrSettings
{
    /// <summary>Integer upscale factor, 1-6.</summary>
    public int UpscaleFactor { get; init; } = 4;

    /// <summary>Binarization threshold, 0-255.</summary>
    public int Threshold { get; init; } = 128;

    public ThresholdMode Mode { get; init; } = ThresholdMode.BlackAndWhite;

    /// <summary>
    /// Football Manager draws light text on a dark bar, and Tesseract's models
    /// are trained on dark text on light paper.
    /// </summary>
    public InvertMode Invert { get; init; } = InvertMode.Auto;

    /// <summary>
    /// Blank margin added around the image after scaling, in output pixels.
    /// </summary>
    /// <remarks>
    /// Tesseract wants a quiet zone, and a tight crop is the single most
    /// effective way to turn a 7 into a 1: shave the horizontal bar off the top
    /// of a 7 and what is left really is a 1, which no amount of engine tuning
    /// recovers from.
    /// </remarks>
    public int QuietZonePixels { get; init; } = 16;

    /// <summary>
    /// Dilation passes applied to the binarised image, thickening dark strokes.
    /// </summary>
    /// <remarks>
    /// The principled fix for the 7-versus-1 problem. When a glyph binarises down
    /// to a one-pixel skeleton, the short crossbar that distinguishes a 7 from a 1
    /// (or from a slash) is the first thing to break up. Thickening the strokes
    /// restores it. Off by default, because the shipped threshold was validated
    /// against the real game and does not need it; raise this rather than fighting
    /// the threshold when the preview shows hairline glyphs.
    /// </remarks>
    public int StrokeThickenPasses { get; init; }

    public bool UseCharacterWhitelist { get; init; } = true;

    public string CharacterWhitelist { get; init; } = "0123456789/";

    public OcrEngineKind Engine { get; init; } = OcrEngineKind.Tesseract;

    /// <summary>
    /// Use Tesseract's legacy classifier instead of the LSTM engine.
    /// </summary>
    /// <remarks>
    /// Only the legacy classifier honours <c>tessedit_char_whitelist</c>
    /// strictly; under LSTM it is advisory and can degrade whitespace handling.
    /// The whitelist is therefore also applied as a post-filter, so the toggle
    /// does something real whichever engine mode is selected.
    /// </remarks>
    public bool UseLegacyEngine { get; init; }

    public static readonly OcrSettings Default = new();

    public OcrSettings Clamped() => this with
    {
        UpscaleFactor = Math.Clamp(UpscaleFactor, 1, 6),
        Threshold = Math.Clamp(Threshold, 0, 255),
        QuietZonePixels = Math.Clamp(QuietZonePixels, 0, 64),
        StrokeThickenPasses = Math.Clamp(StrokeThickenPasses, 0, 4),
    };
}

/// <summary>
/// Everything the user configures. Written rarely - on change and on close.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="RuntimeState"/>, which is rewritten as the game
/// clock moves. A hot write loop must never be able to corrupt a laboriously
/// tuned capture rectangle.
/// </remarks>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;

    public CaptureRect Capture { get; init; } = CaptureRect.Default;

    public OcrSettings Ocr { get; init; } = OcrSettings.Default;

    /// <summary>Country codes the user has ticked.</summary>
    public string[] SelectedCountryCodes { get; init; } = [];

    /// <summary>True once the selection has been initialised from the dataset defaults.</summary>
    public bool SelectionInitialised { get; init; }

    /// <summary>Youth rating at or above which "Select Top Tier" ticks a country.</summary>
    public int TopTierThreshold { get; init; } = 130;

    /// <summary>Include "(Lower Leagues)" rows in the top-tier bulk action.</summary>
    public bool TopTierIncludesLowerLeagues { get; init; }

    /// <summary>Days before the window opens to raise an early heads-up. 0 disables it.</summary>
    public int LeadDays { get; init; }

    public int PollIntervalMs { get; init; } = 1000;

    public DateOrder PreferredDateOrder { get; init; } = DateOrder.DayFirst;

    public bool AutoLearnDateOrder { get; init; } = true;

    // --- Privacy gate -------------------------------------------------------

    /// <summary>
    /// Only sample while Football Manager is running.
    /// </summary>
    /// <remarks>
    /// This is a data-minimisation control, not an optimisation. With it off, a
    /// forgotten monitoring session reads whatever occupies those coordinates -
    /// mail, banking, messages - and writes the text to a rolling log file.
    /// </remarks>
    public bool RequireGameRunning { get; init; } = true;

    /// <summary>Additionally require the game window to be in the foreground.</summary>
    public bool RequireGameForeground { get; init; } = true;

    public string GameProcessName { get; init; } = "fm";

    /// <summary>Optional full path, to distinguish the real game from a same-named process.</summary>
    public string GameExecutablePath { get; init; } = string.Empty;

    // --- Alerting -----------------------------------------------------------

    public bool AlertSound { get; init; } = true;
    public bool AlertFlashWindow { get; init; } = true;
    public bool AlertBringToFront { get; init; }
    public bool AlertTrayBalloon { get; init; } = true;

    /// <summary>
    /// Real-time seconds for which a repeat of the same occurrence is silenced.
    /// </summary>
    /// <remarks>
    /// Reload, advance past the intake, reload, advance again: a save-scum loop
    /// legitimately re-arms and re-fires the same alert many times a minute. The
    /// occurrence key cannot suppress that without also suppressing the genuine
    /// case where one long jump crosses forty nations at once, so the repeat
    /// guard lives here, on wall-clock time, at the alert channel. Every fire is
    /// still written to the Activity Log.
    /// </remarks>
    public int AlertRepeatCooldownSeconds { get; init; } = 60;

    public bool MinimiseToTray { get; init; } = true;
    public bool StartMonitoringOnLaunch { get; init; }

    public static readonly AppSettings Default = new();
}
