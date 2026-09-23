using System.Text;
using FootballManagerRegenNotifier.Capture.Preprocess;
using FootballManagerRegenNotifier.Core.Settings;
using Tesseract;

namespace FootballManagerRegenNotifier.Capture.Ocr;

/// <summary>
/// Reads the clock with Tesseract 5.
/// </summary>
/// <remarks>
/// <para>
/// The engine is created lazily and rebuilt only when the settings that affect
/// it change, because constructing a <c>TesseractEngine</c> loads the language
/// model from disk and is far too expensive to do once a second.
/// </para>
/// <para>
/// Images are handed over as encoded BMP bytes through
/// <c>Pix.LoadFromMemory</c>. The usual <c>Bitmap</c> bridge is unavailable:
/// <c>PixConverter</c> ships only in the package's <c>net48</c> asset, and a
/// <c>net10.0-windows</c> project resolves the <c>netstandard2.0</c> one.
/// </para>
/// </remarks>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly string _tessDataPath;
    private readonly object _gate = new();

    private TesseractEngine? _engine;
    private string _engineSignature = string.Empty;
    private bool _disposed;

    public TesseractOcrEngine(string? tessDataPath = null)
    {
        _tessDataPath = tessDataPath ?? DefaultTessDataPath();
        UnavailableReason = Probe(_tessDataPath);
    }

    public string Name => "Tesseract";

    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason { get; }

    public string TessDataPath => _tessDataPath;

    public static string DefaultTessDataPath() =>
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    private static string? Probe(string path)
    {
        if (!Directory.Exists(path))
        {
            return $"Language data folder '{path}' does not exist.";
        }
        if (!File.Exists(Path.Combine(path, "eng.traineddata")))
        {
            return $"'eng.traineddata' is missing from '{path}'.";
        }
        return null;
    }

    public OcrReading Read(CapturedFrame preprocessedFrame, OcrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(preprocessedFrame);
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsAvailable) return OcrReading.Fail(UnavailableReason!);

        try
        {
            lock (_gate)
            {
                var engine = GetEngine(settings);
                byte[] bmp = BmpEncoder.Encode24Bpp(preprocessedFrame);

                using var pix = Pix.LoadFromMemory(bmp);
                using var page = engine.Process(pix, PageSegMode.SingleLine);

                string raw = page.GetText() ?? string.Empty;

                // GetMeanConfidence is already 0..1.
                double mean = page.GetMeanConfidence();
                double minSymbol = MinSymbolConfidence(page);

                string text = settings.UseCharacterWhitelist
                    ? FilterToWhitelist(raw, settings.CharacterWhitelist)
                    : raw.Trim();

                return new OcrReading(text, Math.Clamp(mean, 0, 1), minSymbol);
            }
        }
        catch (Exception ex)
        {
            return OcrReading.Fail($"Tesseract failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lowest per-symbol confidence in the page, on 0..1.
    /// </summary>
    /// <remarks>
    /// <c>ResultIterator.GetConfidence</c> returns a raw 0..100, hence the
    /// division. This is the signal that actually catches a malformed 7: one bad
    /// glyph in an eight-character date barely moves the line mean, so gating on
    /// the mean alone would let it straight through.
    /// </remarks>
    private static double MinSymbolConfidence(Page page)
    {
        double min = 1.0;
        bool any = false;

        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            string? symbol = iter.GetText(PageIteratorLevel.Symbol);
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            any = true;
            min = Math.Min(min, Math.Clamp(iter.GetConfidence(PageIteratorLevel.Symbol) / 100.0, 0, 1));
        }
        while (iter.Next(PageIteratorLevel.Symbol));

        return any ? min : 0;
    }

    /// <summary>
    /// Drops every character outside the whitelist.
    /// </summary>
    /// <remarks>
    /// Applied in addition to <c>tessedit_char_whitelist</c>, not instead of it.
    /// That parameter belongs to the legacy classifier: it was dropped in 4.0,
    /// partly restored in 4.1, and remains advisory under the LSTM engine, where
    /// setting it can even degrade whitespace handling. Post-filtering is the
    /// only way to make the toggle mean the same thing in both engine modes.
    /// </remarks>
    internal static string FilterToWhitelist(string raw, string whitelist)
    {
        if (string.IsNullOrEmpty(whitelist)) return raw.Trim();

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (whitelist.Contains(c, StringComparison.Ordinal)) sb.Append(c);
        }
        return sb.ToString();
    }

    private TesseractEngine GetEngine(OcrSettings settings)
    {
        string signature = $"{settings.UseLegacyEngine}|{settings.UseCharacterWhitelist}|{settings.CharacterWhitelist}";
        if (_engine is not null && _engineSignature == signature) return _engine;

        _engine?.Dispose();
        _engine = null;

        // Legacy mode is the only one that honours the whitelist strictly, but it
        // needs legacy-capable language data. Fall back rather than fail: a user
        // with an LSTM-only traineddata file should still get a working app.
        var mode = settings.UseLegacyEngine ? EngineMode.TesseractOnly : EngineMode.LstmOnly;
        try
        {
            _engine = new TesseractEngine(_tessDataPath, "eng", mode);
        }
        catch (Exception) when (mode == EngineMode.TesseractOnly)
        {
            _engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.LstmOnly);
        }

        if (settings.UseCharacterWhitelist && !string.IsNullOrEmpty(settings.CharacterWhitelist))
        {
            _engine.SetVariable("tessedit_char_whitelist", settings.CharacterWhitelist);
        }

        // The clock is one short run of glyphs, never prose. Telling the engine
        // so stops it looking for page structure that is not there.
        _engine.SetVariable("classify_bln_numeric_mode", "1");

        _engineSignature = signature;
        return _engine;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
            _disposed = true;
        }
    }
}
