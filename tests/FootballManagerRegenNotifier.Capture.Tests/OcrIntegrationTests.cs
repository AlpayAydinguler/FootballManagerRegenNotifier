using FootballManagerRegenNotifier.Capture.Ocr;
using FootballManagerRegenNotifier.Capture.Preprocess;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;
using Xunit;

namespace FootballManagerRegenNotifier.Capture.Tests;

/// <summary>
/// End-to-end OCR against the real Tesseract engine and the real language data.
/// </summary>
/// <remarks>
/// <para>
/// These synthesise a date the way the game draws it — light glyphs on a dark
/// bar, small, in a UI sans — and assert on what comes back. That is the only way
/// to get evidence about the 7-versus-1 problem, which no other kind of test can
/// see: a 7 misread as a 1 yields a date that is perfectly valid and silently
/// wrong, so nothing downstream can flag it.
/// </para>
/// <para>
/// The renderer is a proxy. It uses a stock Windows UI font rather than FM26's
/// Unity font, so these tests prove the <em>pipeline</em> is sound and characterise
/// how it degrades; they are not a claim about exact accuracy in the real game.
/// The shipped defaults come from calibration against the actual game, not from
/// here — see <see cref="OcrDiagnostics"/> for the sweep behind the tuning advice
/// in the README.
/// </para>
/// </remarks>
public class OcrIntegrationTests
{
    private static string TessDataPath => Path.Combine(AppContext.BaseDirectory, "tessdata");

    private static bool LanguageDataAvailable =>
        File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

    /// <summary>
    /// Settings that read a synthetic date reliably at every font size tested.
    /// </summary>
    /// <remarks>
    /// Threshold 160 rather than the shipped 128 because the stock font renders
    /// thinner than FM26's; see the class remarks. The shipped default is left
    /// alone deliberately — it was validated against the real game.
    /// </remarks>
    private static OcrSettings Robust => OcrSettings.Default with
    {
        Threshold = 160,
        StrokeThickenPasses = 1,
    };

    /// <summary>Text size typical of a 35px-tall title bar.</summary>
    private const float RealisticFontPx = 20f;

    private static CapturedFrame Render(string text, float fontSize = RealisticFontPx, int width = 145) =>
        OcrDiagnostics.Render(text, width, 35, fontSize, "Segoe UI");

    private static string ReadText(CapturedFrame frame, OcrSettings settings)
    {
        using var engine = new TesseractOcrEngine(TessDataPath);
        Assert.True(engine.IsAvailable, engine.UnavailableReason);
        return engine.Read(Preprocessor.Run(frame, settings).Frame, settings).Text;
    }

    // ------------------------------------------------------------ availability

    [Fact]
    public void MissingLanguageData_IsReportedNotThrown()
    {
        using var engine = new TesseractOcrEngine(Path.Combine(Path.GetTempPath(), "definitely-not-tessdata"));

        Assert.False(engine.IsAvailable);
        Assert.NotNull(engine.UnavailableReason);

        // A missing file is a diagnosable state, not a crash: the app must start,
        // explain itself and let the user point at the right folder.
        var reading = engine.Read(CapturedFrame.Allocate(64, 64), OcrSettings.Default);
        Assert.True(reading.Failed);
        Assert.NotNull(reading.Error);
    }

    // -------------------------------------------------------------- happy path

    [SkippableTheory]
    [InlineData("01/07/2026", 2026, 7, 1)]
    [InlineData("14/03/2026", 2026, 3, 14)]
    [InlineData("31/12/2027", 2027, 12, 31)]
    [InlineData("28/02/2028", 2028, 2, 28)]
    [InlineData("22/09/2026", 2026, 9, 22)]
    public void TypicalDates_ReadAndParseCorrectly(string text, int y, int m, int d)
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var parsed = FmDateParser.Parse(ReadText(Render(text), Robust),
            new DateParseOptions { AutoLearnOrder = false });

        Assert.Equal(new DateOnly(y, m, d), parsed.Date);
    }

    [SkippableTheory]
    [InlineData("07/07/2027")]
    [InlineData("17/07/2026")]
    [InlineData("27/07/2027")]
    [InlineData("07/11/2026")]
    [InlineData("11/11/2026")]
    public void SevensAreNotConfusedWithOnesOrSlashes(string text)
    {
        // The headline risk, and the reason the preprocessing exists at all.
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        Assert.Equal(text, ReadText(Render(text), Robust));
    }

    [SkippableFact]
    public void EveryDigitSurvivesARoundTrip()
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        Assert.Equal("01/23/4567", ReadText(Render("01/23/4567"), Robust));
        Assert.Equal("89/01/2345", ReadText(Render("89/01/2345"), Robust));
    }

    // ------------------------------------------------- characterised behaviour

    [SkippableFact]
    public void HairlineText_IsWhereAccuracyBreaksDown()
    {
        // Documents the failure mode the README's troubleshooting section describes.
        // At a small size with the shipped threshold the glyphs binarise to a
        // one-pixel skeleton, the crossbar of a 7 breaks up, and it is read as a 1
        // or a slash. Raising the threshold and thickening the strokes fixes it.
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var hairline = Render("17/07/2026", fontSize: 15f);

        string shipped = ReadText(hairline, OcrSettings.Default);
        string tuned = ReadText(hairline, Robust);

        Assert.Equal("17/07/2026", tuned);
        Assert.NotEqual(tuned, shipped);
    }

    [SkippableFact]
    public void StrokeThickening_RescuesAThinRender()
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var thin = Render("01/07/2026", fontSize: 15f);
        var baseline = OcrSettings.Default with { Threshold = 160 };

        Assert.NotEqual("01/07/2026", ReadText(thin, baseline));
        Assert.Equal("01/07/2026", ReadText(thin, baseline with { StrokeThickenPasses = 1 }));
    }

    [SkippableFact]
    public void WhitelistStripsNonDateCharacters()
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        // Some screens draw a weekday next to the date.
        string read = ReadText(Render("Sat 14/03/2026", width: 210), Robust);

        Assert.DoesNotContain("S", read, StringComparison.Ordinal);
        Assert.Contains("14/03/2026", read, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void DisablingTheWhitelistLetsLettersThrough()
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        string read = ReadText(Render("Sat 14/03/2026", width: 210),
            Robust with { UseCharacterWhitelist = false });

        Assert.Contains("14/03/2026", read, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- contract

    [SkippableFact]
    public void ConfidenceIsReportedOnAZeroToOneScale()
    {
        // If either value ever exceeded 1 the tracker's confidence floor would
        // reject every sample forever, and the app would simply look idle.
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        using var engine = new TesseractOcrEngine(TessDataPath);
        var processed = Preprocessor.Run(Render("14/03/2026"), Robust);

        var reading = engine.Read(processed.Frame, Robust);

        Assert.InRange(reading.MeanConfidence, 0.0, 1.0);
        Assert.InRange(reading.MinSymbolConfidence, 0.0, 1.0);
        Assert.True(reading.MeanConfidence > 0.5,
            $"Mean confidence was {reading.MeanConfidence:F3} on a clean synthetic date.");
    }

    [SkippableFact]
    public void ReadsAreDeterministic()
    {
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var frame = Render("14/03/2026");

        Assert.Equal(ReadText(frame, Robust), ReadText(frame, Robust));
    }

    [SkippableFact]
    public void LegacyEngineModeStartsAgainstTheBundledLanguageData()
    {
        // The full legacy+LSTM file is bundled precisely so the strict in-engine
        // whitelist is available. Prove that mode actually loads.
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var settings = Robust with { UseLegacyEngine = true };
        using var engine = new TesseractOcrEngine(TessDataPath);

        var reading = engine.Read(Preprocessor.Run(Render("14/03/2026"), settings).Frame, settings);

        Assert.False(reading.Failed, reading.Error);
        Assert.Contains("2026", reading.Text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ABlankRegionYieldsNoDigitsRatherThanNoise()
    {
        // The region often shows something that is not a date: a menu, a modal, a
        // loading screen. That must produce nothing, not a plausible number.
        Skip.IfNot(LanguageDataAvailable, "eng.traineddata is not present.");

        var blank = CapturedFrame.Allocate(145, 35);

        string read = ReadText(blank, Robust);

        Assert.False(read.Any(char.IsAsciiDigit), $"Expected no digits from a blank region, got '{read}'.");
    }
}
