using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.App.Services;

/// <summary>
/// Delivers an alert through whichever channels are enabled, and suppresses
/// real-time repeats.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is layered rather than singular because any one channel can be
/// silently swallowed: a toast disappears under Focus Assist or a fullscreen
/// game, a flash is invisible if the taskbar is hidden, a sound is lost if the
/// user is on a call. Several quiet signals beat one that might not arrive.
/// </para>
/// <para>
/// The repeat cooldown lives here, not in the tracker. The tracker keys alerts
/// on the occurrence — country plus in-game year — which is what lets one long
/// Continue correctly announce forty nations at once. But reloading a save
/// re-arms those occurrences by design, so a save-scum loop would otherwise fire
/// the same alert every few seconds. Suppressing on wall-clock time here keeps
/// both properties: no swallowed fan-out, no machine-gunning.
/// </para>
/// </remarks>
public sealed class AlertService(Func<AppSettings> settings) : IDisposable
{
    private readonly Func<AppSettings> _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Dictionary<TriggerKey, DateTime> _lastDelivered = [];
    private readonly Lock _gate = new();

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _disposed;

    public void AttachTray(System.Windows.Forms.NotifyIcon trayIcon) => _trayIcon = trayIcon;

    /// <summary>
    /// Delivers an alert unless an identical one arrived within the cooldown.
    /// Returns false when suppressed, so the caller can log the suppression.
    /// </summary>
    public bool Raise(TrackerEventDescriptor alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (_disposed) return false;

        var config = _settings();

        if (alert.Key is { } key)
        {
            lock (_gate)
            {
                if (_lastDelivered.TryGetValue(key, out var last) &&
                    (DateTime.UtcNow - last).TotalSeconds < config.AlertRepeatCooldownSeconds)
                {
                    return false;
                }
                _lastDelivered[key] = DateTime.UtcNow;
            }
        }

        Deliver(alert, config);
        return true;
    }

    private void Deliver(TrackerEventDescriptor alert, AppSettings config)
    {
        // Each channel is independently guarded: a failure to flash must never
        // stop the sound, and neither may take down the sampler thread.
        if (config.AlertSound) Try(PlaySound);
        if (config.AlertTrayBalloon) Try(() => ShowBalloon(alert));
        if (config.AlertFlashWindow) Try(FlashMainWindow);
        if (config.AlertBringToFront) Try(BringToFront);
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* An alert channel must never be able to crash the app. */ }
    }

    private static void PlaySound() => SystemSounds.Exclamation.Play();

    private void ShowBalloon(TrackerEventDescriptor alert)
    {
        if (_trayIcon is null) return;

        _trayIcon.BalloonTipTitle = alert.Title;
        _trayIcon.BalloonTipText = alert.Message;
        _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(10_000);
    }

    private static void FlashMainWindow() => OnUiThread(window =>
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = helper.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 5,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    });

    private static void BringToFront() => OnUiThread(window =>
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    });

    private static void OnUiThread(Action<Window> action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null) return;

        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is { } window) action(window);
        });
    }

    /// <summary>
    /// True when Windows is suppressing notifications, so the UI can warn that a
    /// toast-only configuration will be silently swallowed.
    /// </summary>
    public static bool NotificationsAreSuppressed()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 &&
                   state is QUNS.BUSY
                         or QUNS.RUNNING_D3D_FULL_SCREEN
                         or QUNS.PRESENTATION_MODE
                         or QUNS.QUIET_TIME;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _trayIcon = null;
    }

    // --- interop ------------------------------------------------------------

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private enum QUNS
    {
        NOT_PRESENT = 1,
        BUSY = 2,
        RUNNING_D3D_FULL_SCREEN = 3,
        PRESENTATION_MODE = 4,
        ACCEPTS_NOTIFICATIONS = 5,
        QUIET_TIME = 6,
        APP = 7,
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUNS state);
}

/// <summary>What the alert service needs to know about an alert.</summary>
public sealed record TrackerEventDescriptor(string Title, string Message, TriggerKey? Key);
