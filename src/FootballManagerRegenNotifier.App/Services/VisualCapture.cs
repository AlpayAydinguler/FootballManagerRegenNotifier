using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FootballManagerRegenNotifier.App.Services;

/// <summary>
/// Renders a live WPF element straight to a PNG.
/// </summary>
/// <remarks>
/// Used by the <c>--screenshot</c> switch to produce the images in the README.
/// It renders the visual tree directly rather than grabbing the screen, so the
/// result is exactly what WPF drew: no desktop composition, no overlapping
/// windows, no dependence on the machine having a display attached, and it works
/// unattended in CI.
/// </remarks>
public static class VisualCapture
{
    public static void SavePng(FrameworkElement element, string path, double scale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        int width = (int)Math.Ceiling(element.ActualWidth * scale);
        int height = (int)Math.Ceiling(element.ActualHeight * scale);
        if (width <= 0 || height <= 0) return;

        var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        element.Measure(new Size(element.ActualWidth, element.ActualHeight));
        element.Arrange(new Rect(new Size(element.ActualWidth, element.ActualHeight)));
        target.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
