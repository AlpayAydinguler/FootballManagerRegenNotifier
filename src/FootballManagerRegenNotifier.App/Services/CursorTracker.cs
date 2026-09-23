using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FootballManagerRegenNotifier.App.Services;

/// <summary>
/// Reports the global cursor position in physical screen pixels.
/// </summary>
/// <remarks>
/// <para>
/// Coordinates match the ones the capture rectangle is expressed in, which is
/// what makes "hover the corner, read the numbers, type them in" a workable way
/// to calibrate by hand.
/// </para>
/// <para>
/// This only lines up because the application declares PerMonitorV2 DPI
/// awareness in its manifest. Without that, Windows virtualises coordinates for
/// a scaled display and the numbers shown here would disagree with the pixels
/// actually grabbed — a mismatch that is invisible at 100% scaling and baffling
/// at 150%.
/// </para>
/// <para>
/// Polled at 20 Hz. Faster is imperceptible for reading numbers off a label, and
/// a global mouse hook would be a much larger hammer for a much smaller job.
/// </para>
/// </remarks>
public sealed class CursorTracker : IDisposable
{
    private readonly DispatcherTimer _timer;

    public CursorTracker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += (_, _) =>
        {
            if (GetCursorPos(out var p)) Moved?.Invoke(p.X, p.Y);
        };
    }

    /// <summary>Fires with the cursor's physical screen coordinates.</summary>
    public event Action<int, int>? Moved;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public static (int X, int Y) Current => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
