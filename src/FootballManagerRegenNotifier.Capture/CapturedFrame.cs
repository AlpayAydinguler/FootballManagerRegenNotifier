namespace FootballManagerRegenNotifier.Capture;

/// <summary>
/// A screen grab, as plain managed pixels.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <see cref="byte"/> array rather than a <c>Bitmap</c> or an
/// <c>HBITMAP</c>. Nothing in the pipeline owns an unmanaged handle, so the
/// entire class of GDI-handle leaks that kills a process sampling once a second
/// for eight hours simply cannot occur, and the preprocessing stages become pure
/// functions that unit-test against committed fixtures with no desktop present.
/// </para>
/// <para>
/// Pixels are BGRA, bottom-up rows excluded: the capture normalises to top-down.
/// Alpha is <b>not</b> meaningful. GDI's <c>BitBlt</c> writes only the colour
/// channels and leaves alpha at zero, which is why the preview renders as
/// <c>Bgr32</c> and never as <c>Bgra32</c> - the latter would interpret the
/// whole frame as fully transparent and show an empty box.
/// </para>
/// </remarks>
public sealed record CapturedFrame
{
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Bytes per row. Always <c>Width * 4</c> for frames this app produces.</summary>
    public required int Stride { get; init; }

    public static CapturedFrame Allocate(int width, int height) => new()
    {
        Pixels = new byte[width * height * 4],
        Width = width,
        Height = height,
        Stride = width * 4,
    };

    /// <summary>
    /// True when every pixel is identical.
    /// </summary>
    /// <remarks>
    /// A uniform frame is how a failed capture over a game presents: GDI hands
    /// back solid black rather than raising an error. Treating it as a real
    /// reading would feed the OCR engine a blank image every second.
    /// </remarks>
    public bool IsUniform()
    {
        if (Pixels.Length < 8) return true;

        byte b = Pixels[0], g = Pixels[1], r = Pixels[2];
        for (int i = 4; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] != b || Pixels[i + 1] != g || Pixels[i + 2] != r) return false;
        }
        return true;
    }

    /// <summary>Mean luminance, 0-255, using the BT.601 weights.</summary>
    public double MeanLuminance()
    {
        if (Pixels.Length == 0) return 0;

        double total = 0;
        int count = 0;
        for (int i = 0; i + 2 < Pixels.Length; i += 4)
        {
            total += 0.114 * Pixels[i] + 0.587 * Pixels[i + 1] + 0.299 * Pixels[i + 2];
            count++;
        }
        return count == 0 ? 0 : total / count;
    }
}
