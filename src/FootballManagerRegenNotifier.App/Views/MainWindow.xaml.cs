using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FootballManagerRegenNotifier.App.Services;
using FootballManagerRegenNotifier.App.ViewModels;
using FootballManagerRegenNotifier.Capture;
using FootballManagerRegenNotifier.Core.Settings;
using Serilog;

namespace FootballManagerRegenNotifier.App.Views;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _vm;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new SettingsStore());
        DataContext = _vm;

        _vm.SnipHandler = ShowSnipOverlay;
        _vm.Log.EntryAdded += ScrollLogToEnd;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Initialise(App.CatalogPath, App.TessDataPath);
            SetUpTray();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed.");
            MessageBox.Show(
                $"FM Regen Notifier could not start cleanly:\n\n{ex.Message}",
                "FM Regen Notifier", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    //  Snipping
    // =====================================================================

    /// <summary>
    /// Grabs the whole virtual desktop, then lets the user draw on that still
    /// image.
    /// </summary>
    /// <remarks>
    /// Hiding this window first keeps it out of the screenshot; without that the
    /// user would be selecting a region of our own UI. The screenshot is taken
    /// before the overlay appears so that a fullscreen game underneath cannot
    /// repaint or drop out of fullscreen while the selection is in progress.
    /// </remarks>
    private CaptureRect? ShowSnipOverlay()
    {
        var bounds = ScreenCapture.VirtualScreen;

        var wasVisible = IsVisible;
        if (wasVisible) Hide();

        try
        {
            // Give the compositor a moment to actually remove this window before
            // the desktop is captured.
            System.Threading.Thread.Sleep(180);

            using var capture = new ScreenCapture();
            var grab = capture.Capture(bounds.X, bounds.Y, bounds.Width, bounds.Height);

            if (!grab.Success || grab.Frame is null)
            {
                MessageBox.Show(
                    $"Could not capture the screen: {grab.Error}",
                    "FM Regen Notifier", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var overlay = new SnipOverlayWindow(grab.Frame, bounds);
            return overlay.ShowDialog() == true ? overlay.Result : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Snip overlay failed.");
            return null;
        }
        finally
        {
            if (wasVisible)
            {
                Show();
                Activate();
            }
        }
    }

    // =====================================================================
    //  Tray
    // =====================================================================

    private void SetUpTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "FM Regen Notifier",
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _reallyClosing = true;
            Close();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _vm.Alerts.AttachTray(_tray);

        // The tooltip carries the countdown so the next intake is discoverable
        // without waiting for, or even enabling, any notification.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.NextIntakeText)
                              or nameof(MainViewModel.StatusText))
            {
                UpdateTrayTooltip();
            }
        };
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;

        // NotifyIcon.Text throws above 63 characters.
        string text = $"FM Regen Notifier — {_vm.StatusText}\nNext: {_vm.NextIntakeText}";
        _tray.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    /// <summary>Selects a tab by index. Used by the --tab screenshot switch.</summary>
    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _vm.MinimiseToTray) Hide();
    }

    // =====================================================================
    //  Log
    // =====================================================================

    private void ScrollLogToEnd()
    {
        if (!_vm.Log.AutoScroll) return;
        if (LogList.Items.Count == 0) return;

        // Respect the user having scrolled up to read something.
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_vm.Log.ToPlainText());
        }
        catch (Exception ex)
        {
            // The clipboard is routinely locked by other processes; a copy button
            // must never be able to take the app down.
            Log.Warning(ex, "Could not copy the activity log to the clipboard.");
        }
    }

    // =====================================================================
    //  Shutdown
    // =====================================================================

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_vm.MinimiseToTray && !_reallyClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_vm.IsMonitoring)
        {
            // Let the sampler finish its current read before the process goes away.
            e.Cancel = true;
            await _vm.StopCommand.ExecuteAsync(null);
            _reallyClosing = true;
            Close();
            return;
        }

        _vm.Persist();
        Dispose();

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Releases the tray icon and the view model.
    /// </summary>
    /// <remarks>
    /// A WPF Window has its own lifecycle and nothing calls Dispose on one, so
    /// this is invoked from <c>OnClosing</c>. It exists as a real implementation
    /// rather than an analyzer suppression because the tray icon genuinely does
    /// need releasing: an undisposed NotifyIcon lingers in the notification area
    /// until the user hovers over it.
    /// </remarks>
    public void Dispose()
    {
        _vm.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        GC.SuppressFinalize(this);
    }
}
