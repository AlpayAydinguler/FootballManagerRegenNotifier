using FootballManagerRegenNotifier.Capture;
using FootballManagerRegenNotifier.Capture.Preprocess;
using FootballManagerRegenNotifier.Core.Settings;
using Xunit;

namespace FootballManagerRegenNotifier.Capture.Tests;

public class PreprocessorTests
{
    /// <summary>Builds a frame from a character map: '#' is ink, '.' is background.</summary>
    private static CapturedFrame FromMap(string[] rows, byte ink = 20, byte background = 200)
    {
        int h = rows.Length, w = rows[0].Length;
        var frame = CapturedFrame.Allocate(w, h);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = rows[y][x] == '#' ? ink : background;
                int i = y * frame.Stride + x * 4;
                frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = v;
                frame.Pixels[i + 3] = 255;
            }
        }
        return frame;
    }

    private static byte Luma(CapturedFrame f, int x, int y) => f.Pixels[y * f.Stride + x * 4 + 1];

    // --------------------------------------------------------------- grayscale

    [Fact]
    public void Grayscale_UsesBt601Weights()
    {
        var frame = CapturedFrame.Allocate(1, 1);
        // Pure red: B=0, G=0, R=255.
        frame.Pixels[0] = 0; frame.Pixels[1] = 0; frame.Pixels[2] = 255; frame.Pixels[3] = 255;

        var gray = Preprocessor.ToGrayscale(frame);

        Assert.Equal((byte)(0.299 * 255), Luma(gray, 0, 0));
    }

    [Fact]
    public void Grayscale_SetsOpaqueAlpha()
    {
        // BitBlt leaves alpha at zero. If the preview were told these bytes were
        // Bgra32 it would render a fully transparent, apparently empty box.
        var gray = Preprocessor.ToGrayscale(CapturedFrame.Allocate(4, 4));

        for (int i = 3; i < gray.Pixels.Length; i += 4) Assert.Equal(255, gray.Pixels[i]);
    }

    // ----------------------------------------------------------------- upscale

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Upscale_MultipliesBothDimensions(int factor)
    {
        var scaled = Preprocessor.Upscale(FromMap(["#.", ".#"]), factor);

        Assert.Equal(2 * factor, scaled.Width);
        Assert.Equal(2 * factor, scaled.Height);
    }

    [Fact]
    public void Upscale_ReplicatesExactlyWithNoHalfPixelShift()
    {
        // The reason this is integer replication rather than GDI+ nearest
        // neighbour: the latter offsets the sample grid by half a pixel unless
        // PixelOffsetMode.Half is also set, and a smeared crossbar is how a 7
        // turns into a 1.
        var scaled = Preprocessor.Upscale(FromMap(["#."]), 4);

        for (int x = 0; x < 4; x++) Assert.Equal(20, Luma(scaled, x, 0));
        for (int x = 4; x < 8; x++) Assert.Equal(200, Luma(scaled, x, 0));
    }

    [Fact]
    public void Upscale_IntroducesNoNewValues()
    {
        var scaled = Preprocessor.Upscale(FromMap(["#.", ".#"]), 5);

        for (int y = 0; y < scaled.Height; y++)
        {
            for (int x = 0; x < scaled.Width; x++)
            {
                Assert.Contains(Luma(scaled, x, y), (byte[])[20, 200]);
            }
        }
    }

    // --------------------------------------------------------------- inversion

    [Fact]
    public void AutoInvert_TriggersOnLightTextOverADarkBar()
    {
        // The Football Manager case: a bright clock on a dark title bar.
        var dark = Preprocessor.ToGrayscale(FromMap(["..#..", "..#.."], ink: 240, background: 15));

        Assert.True(Preprocessor.ShouldInvert(dark, InvertMode.Auto));
    }

    [Fact]
    public void AutoInvert_LeavesDarkTextOnLightBackgroundAlone()
    {
        var light = Preprocessor.ToGrayscale(FromMap(["..#..", "..#.."], ink: 15, background: 240));

        Assert.False(Preprocessor.ShouldInvert(light, InvertMode.Auto));
    }

    [Theory]
    [InlineData(InvertMode.Always, true)]
    [InlineData(InvertMode.Never, false)]
    public void ManualInvertModes_OverrideDetection(InvertMode mode, bool expected)
    {
        var light = Preprocessor.ToGrayscale(FromMap(["..#.."], ink: 15, background: 240));

        Assert.Equal(expected, Preprocessor.ShouldInvert(light, mode));
    }

    [Fact]
    public void Invert_IsItsOwnInverse()
    {
        var frame = Preprocessor.ToGrayscale(FromMap(["#.#", ".#."]));
        var original = (byte[])frame.Pixels.Clone();

        Preprocessor.Invert(frame);
        Preprocessor.Invert(frame);

        Assert.Equal(original, frame.Pixels);
    }

    // --------------------------------------------------------------- threshold

    [Fact]
    public void Threshold_ProducesOnlyPureBlackAndWhite()
    {
        var frame = Preprocessor.ToGrayscale(FromMap(["#.#"], ink: 100, background: 150));

        Preprocessor.Threshold(frame, 128);

        Assert.Equal(0, Luma(frame, 0, 0));
        Assert.Equal(255, Luma(frame, 1, 0));
        Assert.Equal(0, Luma(frame, 2, 0));
    }

    [Fact]
    public void GrayscaleMode_SkipsThresholding()
    {
        var result = Preprocessor.Run(
            FromMap(["#.#"], ink: 100, background: 150),
            OcrSettings.Default with { Mode = ThresholdMode.Grayscale, UpscaleFactor = 1, QuietZonePixels = 0, Invert = InvertMode.Never });

        Assert.Equal(100, Luma(result.Frame, 0, 0));
    }

    // -------------------------------------------------------------- quiet zone

    [Fact]
    public void Pad_GrowsTheImageOnAllFourSides()
    {
        var padded = Preprocessor.Pad(Preprocessor.ToGrayscale(FromMap(["##", "##"])), 16);

        Assert.Equal(2 + 32, padded.Width);
        Assert.Equal(2 + 32, padded.Height);
    }

    [Fact]
    public void Pad_FillsTheMarginWithBackgroundAndKeepsTheContent()
    {
        var source = Preprocessor.ToGrayscale(FromMap(["##", "##"], ink: 0, background: 0));
        var padded = Preprocessor.Pad(source, 3);

        Assert.Equal(255, Luma(padded, 0, 0));
        Assert.Equal(255, Luma(padded, padded.Width - 1, padded.Height - 1));
        Assert.Equal(0, Luma(padded, 3, 3));
    }

    [Fact]
    public void QuietZone_SeparatesInkFromTheBorder()
    {
        // Tesseract degrades on text flush against the frame edge.
        var settings = OcrSettings.Default with
        {
            UpscaleFactor = 1,
            QuietZonePixels = 8,
            Invert = InvertMode.Never,
            Mode = ThresholdMode.BlackAndWhite,
        };

        var result = Preprocessor.Run(FromMap(["##", "##"], ink: 0, background: 0), settings);

        Assert.False(Preprocessor.InkTouchesEdge(result.Frame, 128));
    }

    // ------------------------------------------------- the clipped-seven guard

    [Fact]
    public void ClippedGlyphTouchingTheTopEdge_IsFlagged()
    {
        // This is the failure the warning exists for. Shave the crossbar off a 7
        // and what is left is a 1, which parses perfectly and is silently wrong,
        // so nothing downstream of the capture can possibly catch it.
        var clipped = FromMap(
        [
            "..####",   // crossbar flush against the top edge
            ".....#",
            "....#.",
            "...#..",
        ], ink: 0, background: 255);

        var result = Preprocessor.Run(clipped, OcrSettings.Default with
        {
            UpscaleFactor = 1,
            QuietZonePixels = 0,
            Invert = InvertMode.Never,
        });

        Assert.True(result.InkTouchesEdge);
    }

    [Fact]
    public void GlyphWithHeadroom_IsNotFlagged()
    {
        var wellCropped = FromMap(
        [
            "......",
            ".####.",
            "....#.",
            "...#..",
            "......",
        ], ink: 0, background: 255);

        var result = Preprocessor.Run(wellCropped, OcrSettings.Default with
        {
            UpscaleFactor = 1,
            QuietZonePixels = 0,
            Invert = InvertMode.Never,
        });

        Assert.False(result.InkTouchesEdge);
    }

    // ----------------------------------------------------------- full pipeline

    [Fact]
    public void DefaultSettings_ProduceTheExpectedGeometry()
    {
        // 145x35 at 4x plus a 16px quiet zone on each side.
        var result = Preprocessor.Run(CapturedFrame.Allocate(145, 35), OcrSettings.Default);

        Assert.Equal(145 * 4 + 32, result.Frame.Width);
        Assert.Equal(35 * 4 + 32, result.Frame.Height);
    }

    [Fact]
    public void Pipeline_DoesNotMutateItsInput()
    {
        var source = FromMap(["#.#", ".#."], ink: 30, background: 220);
        var before = (byte[])source.Pixels.Clone();

        Preprocessor.Run(source, OcrSettings.Default);

        Assert.Equal(before, source.Pixels);
    }

    [Fact]
    public void OutOfRangeSettings_AreClampedRatherThanThrowing()
    {
        var result = Preprocessor.Run(CapturedFrame.Allocate(10, 10), OcrSettings.Default with
        {
            UpscaleFactor = 500,
            Threshold = 9999,
            QuietZonePixels = -4,
        });

        Assert.Equal(60, result.Frame.Width);
    }

    [Fact]
    public void DarkBarInput_IsInvertedSoTesseractSeesDarkOnLight()
    {
        var result = Preprocessor.Run(
            FromMap(["..##..", "..##.."], ink: 245, background: 10),
            OcrSettings.Default with { UpscaleFactor = 1, QuietZonePixels = 0 });

        Assert.True(result.Inverted);
        // Background became light, glyph became dark.
        Assert.Equal(255, Luma(result.Frame, 0, 0));
        Assert.Equal(0, Luma(result.Frame, 2, 0));
    }
}

public class CapturedFrameTests
{
    [Fact]
    public void UniformFrame_IsDetected()
    {
        // A flat frame is how a blocked grab presents: GDI reports success and
        // returns solid black rather than raising an error.
        Assert.True(CapturedFrame.Allocate(16, 16).IsUniform());
    }

    [Fact]
    public void FrameWithAnySingleDifferingPixel_IsNotUniform()
    {
        var frame = CapturedFrame.Allocate(16, 16);
        frame.Pixels[4 * 40 + 1] = 200;

        Assert.False(frame.IsUniform());
    }

    [Fact]
    public void Allocate_ProducesAConsistentStride()
    {
        var frame = CapturedFrame.Allocate(145, 35);

        Assert.Equal(145 * 4, frame.Stride);
        Assert.Equal(145 * 35 * 4, frame.Pixels.Length);
    }
}

public class BmpEncoderTests
{
    [Fact]
    public void Encode_WritesAValidBitmapHeader()
    {
        byte[] bmp = BmpEncoder.Encode24Bpp(CapturedFrame.Allocate(4, 3));

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
        Assert.Equal(4, BitConverter.ToInt32(bmp, 18));
        // Positive height with bottom-up rows is canonical BMP. The negative-height
        // top-down convention belongs to the GetDIBits call, not to the file format.
        Assert.Equal(3, BitConverter.ToInt32(bmp, 22));
        Assert.Equal(24, BitConverter.ToInt16(bmp, 28));
    }

    [Fact]
    public void Encode_PadsRowsToAFourByteBoundary()
    {
        // 3 pixels x 3 bytes = 9, padded to 12.
        byte[] bmp = BmpEncoder.Encode24Bpp(CapturedFrame.Allocate(3, 2));

        Assert.Equal(54 + 12 * 2, bmp.Length);
    }

    [Fact]
    public void Encode_WritesRowsBottomUp()
    {
        var frame = CapturedFrame.Allocate(1, 2);
        // Top row white, bottom row black.
        frame.Pixels[0] = frame.Pixels[1] = frame.Pixels[2] = 255;
        frame.Pixels[4] = frame.Pixels[5] = frame.Pixels[6] = 0;

        byte[] bmp = BmpEncoder.Encode24Bpp(frame);

        // First stored row is the image's bottom row.
        Assert.Equal(0, bmp[54]);
        Assert.Equal(255, bmp[54 + 4]);
    }

    [Fact]
    public void Encode_DeclaresAResolutionSoTesseractDoesNotWarn()
    {
        byte[] bmp = BmpEncoder.Encode24Bpp(CapturedFrame.Allocate(2, 2));

        Assert.Equal(3780, BitConverter.ToInt32(bmp, 38));
        Assert.Equal(3780, BitConverter.ToInt32(bmp, 42));
    }
}
