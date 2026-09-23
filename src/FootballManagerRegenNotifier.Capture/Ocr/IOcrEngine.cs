using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.Capture.Ocr;

/// <summary>
/// One OCR reading, with confidences already normalised to 0..1.
/// </summary>
/// <remarks>
/// Normalisation happens here, at the adapter boundary, and not anywhere above.
/// Tesseract reports the line mean on 0..1 but per-symbol confidence on 0..100;
/// letting both scales travel upward produces a confidence floor that silently
/// rejects every sample, which looks from the outside like an app that has
/// simply stopped doing anything.
/// </remarks>
public sealed record OcrReading(
    string Text,
    double MeanConfidence,
    double MinSymbolConfidence,
    string? Error = null)
{
    public bool Failed => Error is not null;

    public static OcrReading Fail(string error) => new(string.Empty, 0, 0, error);
}

public interface IOcrEngine : IDisposable
{
    string Name { get; }

    /// <summary>True when the engine has everything it needs to run.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the engine is unavailable, phrased for the Activity Log.</summary>
    string? UnavailableReason { get; }

    OcrReading Read(CapturedFrame preprocessedFrame, OcrSettings settings);
}
