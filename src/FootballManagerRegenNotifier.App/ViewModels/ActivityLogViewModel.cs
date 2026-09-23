using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog.Events;

namespace FootballManagerRegenNotifier.App.ViewModels;

public enum LogSeverity
{
    Detail,
    Info,
    Success,
    Warning,
    Error,
    Alert,
}

public sealed record LogEntry(DateTime Timestamp, LogSeverity Severity, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public Brush Colour => Severity switch
    {
        LogSeverity.Detail => Brushes.Gray,
        LogSeverity.Success => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x7B)),
        LogSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)),
        LogSeverity.Error => new SolidColorBrush(Color.FromRgb(0xE5, 0x6B, 0x6B)),
        LogSeverity.Alert => new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xE8)),
    };
}

/// <summary>
/// The rolling event stream shown at the bottom of the window.
/// </summary>
/// <remarks>
/// Capped, because this is a long-running background app: an uncapped collection
/// sampling once a second grows without limit, and the UI virtualiser slows down
/// long before memory becomes the problem. Trimming from the front keeps the
/// recent history, which is the part anybody ever reads.
/// </remarks>
public sealed partial class ActivityLogViewModel : ObservableObject
{
    private const int MaxEntries = 500;
    private const int TrimTo = 400;

    [ObservableProperty]
    private bool _showDetail;

    [ObservableProperty]
    private bool _autoScroll = true;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Raised when a new entry is appended, so the view can scroll.</summary>
    public event Action? EntryAdded;

    public void Add(LogSeverity severity, string message)
    {
        // Mirror to the rolling file before the verbosity filter, so a user can
        // send a log that explains a problem they only noticed afterwards.
        // Everything reaching here is either a startup message or a tracker event
        // from a gated sample, so no ungated screen text is written to disk.
        Serilog.Log.Write(severity switch
        {
            LogSeverity.Error => LogEventLevel.Error,
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Detail => LogEventLevel.Debug,
            _ => LogEventLevel.Information,
        }, "{Message}", message);

        if (severity == LogSeverity.Detail && !ShowDetail) return;

        Entries.Add(new LogEntry(DateTime.Now, severity, message));

        if (Entries.Count > MaxEntries)
        {
            while (Entries.Count > TrimTo) Entries.RemoveAt(0);
        }

        EntryAdded?.Invoke();
    }

    public void Clear() => Entries.Clear();

    public string ToPlainText() => string.Join(Environment.NewLine,
        Entries.Select(e => $"{e.Time}  [{e.Severity}]  {e.Message}"));
}
