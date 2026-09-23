using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.Capture.Preprocess;

public sealed record PreprocessResult(CapturedFrame Frame, bool Inverted, bool InkTouchesEdge);

/// <summary>
/// Turns a raw screen grab into the image actually handed to the OCR engine.
/// </summary>
/// <remarks>
/// <para>
/// A pure function of (frame, settings). No screen, no handles, no statics, so
/// every stage is testable against committed fixtures, and the live preview and
/// the sampler are guaranteed to be looking at the same bytes.
/// </para>
/// <para>
/// Order matters, and it is: grayscale, decide polarity, upscale, threshold,
/// pad. Thresholding before scaling would quantise away the very edge detail the
/// scaling is meant to preserve, and padding before scaling would scale the
/// padding too.
/// </para>
/// </remarks>
public static class Preprocessor
{
    public static PreprocessResult Run(CapturedFrame source, OcrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        settings = settings.Clamped();

        var gray = ToGrayscale(source);
        bool invert = ShouldInvert(gray, settings.Invert);
        if (invert) Invert(gray);

        var scaled = Upscale(gray, settings.UpscaleFactor);

        if (settings.Mode == ThresholdMode.BlackAndWhite)
        {
            Threshold(scaled, settings.Threshold);
        }

        if (settings.StrokeThickenPasses > 0 && settings.Mode == ThresholdMode.BlackAndWhite)
        {
            for (int i = 0; i < settings.StrokeThickenPasses; i++) scaled = Dilate(scaled);
        }

        bool touchesEdge = InkTouchesEdge(scaled, settings.Threshold);

        var padded = settings.QuietZonePixels > 0
            ? Pad(scaled, settings.QuietZonePixels)
            : scaled;

        return new PreprocessResult(padded, invert, touchesEdge);
    }

    /// <summary>BT.601 luma, written into all three colour channels.</summary>
    public static CapturedFrame ToGrayscale(CapturedFrame source)
    {
        var pixels = new byte[source.Width * source.Height * 4];

        for (int y = 0; y < source.Height; y++)
        {
            int srcRow = y * source.Stride;
            int dstRow = y * source.Width * 4;
            for (int x = 0; x < source.Width; x++)
            {
                int s = srcRow + x * 4;
                int d = dstRow + x * 4;
                byte luma = (byte)(0.114 * source.Pixels[s]
                                 + 0.587 * source.Pixels[s + 1]
                                 + 0.299 * source.Pixels[s + 2]);
                pixels[d] = pixels[d + 1] = pixels[d + 2] = luma;
                pixels[d + 3] = 255;
            }
        }

        return new CapturedFrame
        {
            Pixels = pixels,
            Width = source.Width,
            Height = source.Height,
            Stride = source.Width * 4,
        };
    }

    /// <summary>
    /// Decides whether the image needs its polarity flipped.
    /// </summary>
    /// <remarks>
    /// Football Manager draws a light clock on a dark bar, and Tesseract's models
    /// are trained on dark text on light paper. Feeding it white-on-black costs
    /// real accuracy for no reason. Auto-detection is simply "is most of this
    /// image dark", which is reliable here because the crop is nearly all
    /// background with a thin run of glyphs across it.
    /// </remarks>
    public static bool ShouldInvert(CapturedFrame gray, InvertMode mode) => mode switch
    {
        InvertMode.Always => true,
        InvertMode.Never => false,
        _ => gray.MeanLuminance() < 128,
    };

    public static void Invert(CapturedFrame frame)
    {
        var p = frame.Pixels;
        for (int i = 0; i + 2 < p.Length; i += 4)
        {
            p[i] = (byte)(255 - p[i]);
            p[i + 1] = (byte)(255 - p[i + 1]);
            p[i + 2] = (byte)(255 - p[i + 2]);
        }
    }

    /// <summary>
    /// Integer pixel replication.
    /// </summary>
    /// <remarks>
    /// Not <c>InterpolationMode.NearestNeighbor</c>, which offsets the sample
    /// grid by half a pixel unless <c>PixelOffsetMode.Half</c> is also set. That
    /// half-pixel shift smears glyph edges, and a smeared crossbar on a 7 is
    /// precisely how a 7 becomes a 1. Replication has no such subtlety: every
    /// output pixel is exactly one input pixel.
    /// </remarks>
    public static CapturedFrame Upscale(CapturedFrame source, int factor)
    {
        if (factor <= 1) return source;

        int width = source.Width * factor;
        int height = source.Height * factor;
        var pixels = new byte[width * height * 4];
        int stride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int srcRow = (y / factor) * source.Stride;
            int dstRow = y * stride;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + (x / factor) * 4;
                int d = dstRow + x * 4;
                pixels[d] = source.Pixels[s];
                pixels[d + 1] = source.Pixels[s + 1];
                pixels[d + 2] = source.Pixels[s + 2];
                pixels[d + 3] = 255;
            }
        }

        return new CapturedFrame { Pixels = pixels, Width = width, Height = height, Stride = stride };
    }

    public static void Threshold(CapturedFrame frame, int threshold)
    {
        var p = frame.Pixels;
        for (int i = 0; i + 2 < p.Length; i += 4)
        {
            byte v = p[i + 1] >= threshold ? (byte)255 : (byte)0;
            p[i] = p[i + 1] = p[i + 2] = v;
        }
    }

    /// <summary>
    /// Surrounds the image with background-coloured margin.
    /// </summary>
    /// <remarks>
    /// Tesseract expects a quiet zone around text and degrades without one. More
    /// importantly this is insurance against a slightly tight capture rectangle:
    /// if the crop shaves the crossbar off the top of a 7, what remains genuinely
    /// is a 1, and no engine setting recovers it. Padding cannot restore clipped
    /// ink, but it does stop glyphs from touching the frame edge once the
    /// rectangle is right.
    /// </remarks>
    public static CapturedFrame Pad(CapturedFrame source, int padding)
    {
        int width = source.Width + padding * 2;
        int height = source.Height + padding * 2;
        var pixels = new byte[width * height * 4];
        int stride = width * 4;

        // Post-inversion the background is light, so pad with white.
        Array.Fill(pixels, (byte)255);

        for (int y = 0; y < source.Height; y++)
        {
            int srcRow = y * source.Stride;
            int dstRow = (y + padding) * stride + padding * 4;
            Array.Copy(source.Pixels, srcRow, pixels, dstRow, source.Width * 4);
        }

        return new CapturedFrame { Pixels = pixels, Width = width, Height = height, Stride = stride };
    }

    /// <summary>
    /// True when dark ink reaches the outer border of the image.
    /// </summary>
    /// <remarks>
    /// Drives the calibration warning. Text running off the edge of the capture
    /// rectangle is the single most common cause of a wrong-but-plausible date,
    /// and it is invisible in the OCR output because a clipped 7 parses perfectly
    /// well as a 1. Telling the user their rectangle is too tight is worth more
    /// than any amount of engine tuning.
    /// </remarks>
    public static bool InkTouchesEdge(CapturedFrame frame, int threshold)
    {
        int w = frame.Width, h = frame.Height;
        if (w < 2 || h < 2) return false;

        byte cutoff = (byte)Math.Clamp(threshold, 0, 255);

        for (int x = 0; x < w; x++)
        {
            if (IsInk(frame, x, 0, cutoff) || IsInk(frame, x, h - 1, cutoff)) return true;
        }
        for (int y = 0; y < h; y++)
        {
            if (IsInk(frame, 0, y, cutoff) || IsInk(frame, w - 1, y, cutoff)) return true;
        }
        return false;
    }

    /// <summary>
    /// One pass of binary dilation on the dark strokes (a 4-connected minimum
    /// filter).
    /// </summary>
    /// <remarks>
    /// Thickens glyphs by one pixel in every direction. When text binarises down
    /// to a hairline skeleton, the short crossbar that separates a 7 from a 1, and
    /// from a slash, breaks up before anything else does, and the resulting misread
    /// is a date that parses perfectly and is silently wrong. Thickening restores
    /// the bar. Measured on synthetic dates, hairline renders go from roughly one
    /// correct reading in six to six in six.
    /// </remarks>
    public static CapturedFrame Dilate(CapturedFrame source)
    {
        int w = source.Width, h = source.Height;
        var pixels = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte min = At(source, x, y);
                if (x > 0) min = Math.Min(min, At(source, x - 1, y));
                if (x < w - 1) min = Math.Min(min, At(source, x + 1, y));
                if (y > 0) min = Math.Min(min, At(source, x, y - 1));
                if (y < h - 1) min = Math.Min(min, At(source, x, y + 1));

                int d = y * w * 4 + x * 4;
                pixels[d] = pixels[d + 1] = pixels[d + 2] = min;
                pixels[d + 3] = 255;
            }
        }

        return new CapturedFrame { Pixels = pixels, Width = w, Height = h, Stride = w * 4 };
    }

    private static byte At(CapturedFrame f, int x, int y) => f.Pixels[y * f.Stride + x * 4 + 1];

    private static bool IsInk(CapturedFrame frame, int x, int y, byte cutoff) =>
        frame.Pixels[y * frame.Stride + x * 4 + 1] < cutoff;
}
