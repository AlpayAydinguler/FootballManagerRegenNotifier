using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using FootballManagerRegenNotifier.App.Services;
using FootballManagerRegenNotifier.Capture;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.App.Views;

/// <summary>
/// Full-desktop region picker.
/// </summary>
/// <remarks>
/// <para>
/// Works over a <b>frozen screenshot</b> of the whole virtual desktop rather
/// than over a transparent window on the live desktop. That matters with a game
/// underneath: a topmost window appearing over a fullscreen application can make
/// it repaint, flicker or drop out of fullscreen mid-drag, and the user would be
/// selecting against a moving target. A still image cannot move.
/// </para>
/// <para>
/// Every coordinate returned is a <b>physical</b> pixel. WPF measures in
/// device-independent units, so the drag rectangle is scaled by the window's own
/// DPI before being handed back; without that step a calibration performed at
/// 150% scaling would be wrong by half again.
/// </para>
/// </remarks>
public partial class SnipOverlayWindow : Window
{
    private readonly VirtualScreenBounds _bounds;
    private Point _anchor;
    private bool _dragging;
    private bool _hasBeenActivated;

    public SnipOverlayWindow(CapturedFrame desktop, VirtualScreenBounds bounds)
    {
        InitializeComponent();

        _bounds = bounds;
        Backdrop.Source = FrameImageSource.Create(desktop);

        // Position in device-independent units; the conversion happens in OnSourceInitialized
        // once the window has a DPI to report.
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>The chosen rectangle in physical pixels, or null if cancelled.</summary>
    public CaptureRect? Result { get; private set; }

    private double ScaleX { get; set; } = 1.0;

    private double ScaleY { get; set; } = 1.0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
        {
            ScaleX = target.TransformToDevice.M11;
            ScaleY = target.TransformToDevice.M22;

            // Re-express the virtual-screen bounds in the units WPF lays out in.
            Left = _bounds.X / ScaleX;
            Top = _bounds.Y / ScaleY;
            Width = _bounds.Width / ScaleX;
            Height = _bounds.Height / ScaleY;
        }

        UpdateShade(new Rect(0, 0, 0, 0), visible: false);
        Activate();
        Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hasBeenActivated = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Only cancel on a genuine loss of focus. Showing a topmost window over a
        // fullscreen game can produce a spurious deactivation during the show
        // itself, and cancelling on that would make the tool look broken.
        if (_hasBeenActivated && _dragging is false && IsLoaded) Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Cancel();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _anchor = e.GetPosition(Root);
        _dragging = true;
        Selection.Visibility = Visibility.Visible;
        Readout.Visibility = Visibility.Visible;
        CaptureMouse();
        Update(_anchor);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Update(e.GetPosition(Root));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        var rect = Normalise(_anchor, e.GetPosition(Root));

        // Ignore a stray click: a one-pixel box is never what anyone meant.
        if (rect.Width < 4 || rect.Height < 4)
        {
            Cancel();
            return;
        }

        Result = ToPhysical(rect);
        DialogResult = true;
        Close();
    }

    private void Update(Point current)
    {
        var rect = Normalise(_anchor, current);

        Canvas.SetLeft(Selection, rect.X);
        Canvas.SetTop(Selection, rect.Y);
        Selection.Width = rect.Width;
        Selection.Height = rect.Height;

        UpdateShade(rect, visible: true);

        var physical = ToPhysical(rect);
        ReadoutText.Text =
            $"{physical.TopLeftX}, {physical.TopLeftY}  →  {physical.BottomRightX}, {physical.BottomRightY}" +
            $"    ({physical.Width} × {physical.Height} px)";

        // Keep the readout on screen when dragging near an edge.
        double readoutX = Math.Min(rect.X, ActualWidth - 260);
        double readoutY = rect.Y > 42 ? rect.Y - 38 : rect.Y + rect.Height + 10;
        Canvas.SetLeft(Readout, Math.Max(0, readoutX));
        Canvas.SetTop(Readout, Math.Max(0, readoutY));
    }

    /// <summary>Dims everything outside the selection using four rectangles.</summary>
    private void UpdateShade(Rect hole, bool visible)
    {
        if (!visible)
        {
            SetRect(ShadeTop, 0, 0, ActualWidth, ActualHeight);
            SetRect(ShadeBottom, 0, 0, 0, 0);
            SetRect(ShadeLeft, 0, 0, 0, 0);
            SetRect(ShadeRight, 0, 0, 0, 0);
            return;
        }

        SetRect(ShadeTop, 0, 0, ActualWidth, hole.Y);
        SetRect(ShadeBottom, 0, hole.Bottom, ActualWidth, Math.Max(0, ActualHeight - hole.Bottom));
        SetRect(ShadeLeft, 0, hole.Y, hole.X, hole.Height);
        SetRect(ShadeRight, hole.Right, hole.Y, Math.Max(0, ActualWidth - hole.Right), hole.Height);
    }

    private static void SetRect(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    private static Rect Normalise(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    /// <summary>Converts a rectangle in WPF units to physical screen pixels.</summary>
    private CaptureRect ToPhysical(Rect rect) => new()
    {
        TopLeftX = _bounds.X + (int)Math.Round(rect.X * ScaleX),
        TopLeftY = _bounds.Y + (int)Math.Round(rect.Y * ScaleY),
        BottomRightX = _bounds.X + (int)Math.Round(rect.Right * ScaleX),
        BottomRightY = _bounds.Y + (int)Math.Round(rect.Bottom * ScaleY),
    };

    private void Cancel()
    {
        Result = null;
        if (IsLoaded)
        {
            DialogResult = false;
            Close();
        }
    }
}
