using System.Windows.Media;
using System.Windows.Media.Imaging;
using FootballManagerRegenNotifier.Capture;

namespace FootballManagerRegenNotifier.App.Services;

/// <summary>
/// Converts a captured frame into something WPF can draw.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="BitmapSource.Create(int,int,double,double,PixelFormat,BitmapPalette,Array,int)"/>
/// over the managed pixel array. The familiar alternative —
/// <c>Bitmap.GetHbitmap()</c> with <c>CreateBitmapSourceFromHBitmap</c> — leaks
/// a GDI handle on every call unless the HBITMAP is passed to
/// <c>DeleteObject</c>, and at one preview per second that exhausts the
/// process's GDI object quota within an evening. There is no handle here to
/// forget.
/// </para>
/// <para>
/// The format is <c>Bgr32</c>, never <c>Bgra32</c>. GDI's <c>BitBlt</c> writes
/// only the colour channels and leaves the alpha byte at zero, so declaring an
/// alpha channel would tell WPF the entire image is fully transparent and the
/// preview would render as an empty box.
/// </para>
/// </remarks>
public static class FrameImageSource
{
    public static BitmapSource Create(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96, 96,
            PixelFormats.Bgr32,
            palette: null,
            frame.Pixels,
            frame.Stride);

        // Freezing makes it safe to hand to the UI thread from the sampler and
        // removes the per-render locking WPF would otherwise do.
        source.Freeze();
        return source;
    }
}
