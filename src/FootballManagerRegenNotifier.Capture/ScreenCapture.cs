using FootballManagerRegenNotifier.Capture.Native;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.Capture;

public sealed record CaptureOutcome(CapturedFrame? Frame, string? Error)
{
    public bool Success => Frame is not null;

    public static CaptureOutcome Ok(CapturedFrame frame) => new(frame, null);
    public static CaptureOutcome Fail(string error) => new(null, error);
}

/// <summary>
/// Grabs a rectangle of the virtual desktop via GDI.
/// </summary>
/// <remarks>
/// <para>
/// GDI <c>BitBlt</c> rather than Windows Graphics Capture. WGC is the modern API
/// and would be the better choice on Windows 11, but its yellow capture-border
/// opt-out (<c>IsBorderRequired = false</c>) does not exist on Windows 10, so a
/// WGC build would paint a permanent yellow rectangle over the game for as long
/// as monitoring runs. For a 145x35 region sampled once a second over a
/// Direct3D 11 game in windowed or borderless mode, GDI is both sufficient and
/// invisible.
/// </para>
/// <para>
/// The device contexts and the DIB section are created once and reused for the
/// life of the object. At one sample per second over an eight-hour session that
/// is the difference between four GDI objects and roughly a hundred thousand.
/// </para>
/// </remarks>
public sealed class ScreenCapture : IScreenCapture
{
    private readonly object _gate = new();

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private byte[]? _buffer;
    private int _width;
    private int _height;
    private bool _disposed;

    public static VirtualScreenBounds VirtualScreen => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    public CaptureOutcome Capture(CaptureRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);

        if (!rect.IsValid)
        {
            return CaptureOutcome.Fail("The capture rectangle has zero width or height.");
        }

        return Capture(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    public CaptureOutcome Capture(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width <= 0 || height <= 0)
        {
            return CaptureOutcome.Fail("The capture rectangle has zero width or height.");
        }

        lock (_gate)
        {
            try
            {
                EnsureSurface(width, height);

                // CAPTUREBLT includes layered windows, which matters when the game
                // or an overlay draws through one.
                if (!NativeMethods.BitBlt(_memoryDc, 0, 0, width, height,
                        _screenDc, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                {
                    return CaptureOutcome.Fail("BitBlt failed. The screen may be locked or protected.");
                }

                var info = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = 40,
                        biWidth = width,
                        // Negative height requests a top-down DIB, so row zero is
                        // the top row and no vertical flip is needed later.
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = NativeMethods.BI_RGB,
                    },
                };

                int copied = NativeMethods.GetDIBits(
                    _memoryDc, _bitmap, 0, (uint)height, _buffer!, ref info, NativeMethods.DIB_RGB_COLORS);

                if (copied == 0)
                {
                    return CaptureOutcome.Fail("GetDIBits returned no scan lines.");
                }

                // Copy out: the internal buffer is reused next tick, and handing
                // it out directly would let the preview and the sampler race.
                var pixels = new byte[width * height * 4];
                Array.Copy(_buffer!, pixels, pixels.Length);

                var frame = new CapturedFrame
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height,
                    Stride = width * 4,
                };

                // A flat frame is how a blocked grab presents: BitBlt reports
                // success and hands back solid black. The screen device context is
                // cached for the life of this object, and a display-mode change or
                // a session transition can leave that cached handle reading black
                // forever after. Dropping it means the next tick starts from a
                // fresh one, which turns a permanently dead capture into a blip.
                if (frame.IsUniform()) ReleaseScreenDc();

                return CaptureOutcome.Ok(frame);
            }
            catch (Exception ex)
            {
                return CaptureOutcome.Fail($"Capture failed: {ex.Message}");
            }
        }
    }

    private void EnsureSurface(int width, int height)
    {
        if (_screenDc == IntPtr.Zero)
        {
            _screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("Could not obtain a screen device context.");
        }

        if (_memoryDc != IntPtr.Zero && _width == width && _height == height) return;

        ReleaseSurface();

        _memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero) throw new InvalidOperationException("Could not create a memory device context.");

        _bitmap = NativeMethods.CreateCompatibleBitmap(_screenDc, width, height);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not create a compatible bitmap.");

        _oldBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
        _buffer = new byte[width * height * 4];
        _width = width;
        _height = height;
    }

    /// <summary>Drops the cached screen DC so the next capture re-acquires one.</summary>
    private void ReleaseScreenDc()
    {
        ReleaseSurface();
        if (_screenDc != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }
    }

    private void ReleaseSurface()
    {
        if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memoryDc, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
        if (_memoryDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }
        _buffer = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_gate)
        {
            ReleaseSurface();
            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}

public interface IScreenCapture : IDisposable
{
    CaptureOutcome Capture(CaptureRect rect);
}

public readonly record struct VirtualScreenBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
