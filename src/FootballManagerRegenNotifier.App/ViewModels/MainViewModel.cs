using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FootballManagerRegenNotifier.App.Services;
using FootballManagerRegenNotifier.Capture;
using FootballManagerRegenNotifier.Capture.Ocr;
using FootballManagerRegenNotifier.Core.Catalog;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;
using FootballManagerRegenNotifier.Core.Tracking;
using FootballManagerRegenNotifier.Core.Triggers;

namespace FootballManagerRegenNotifier.App.ViewModels;

public enum CountrySort
{
    Name,
    YouthRating,
    RegenDate,
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AlertService _alerts;
    private readonly CursorTracker _cursor;
    private readonly Dispatcher _dispatcher;

    private MonitoringService? _monitor;
    private DateReader? _reader;
    private TriggerCalendar _calendar = new([]);
    private HashSet<string> _selectedCodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Coalesces slider drags into one re-read. See <see cref="RequestPreview"/>.</summary>
    private readonly DispatcherTimer _previewDebounce;

    private bool _suppressPersist = true;
    private bool _disposed;

    public MainViewModel(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _alerts = new AlertService(() => BuildSettings());
        _cursor = new CursorTracker();
        _cursor.Moved += (x, y) => CursorPosition = $"{x}, {y}";

        _previewDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _previewDebounce.Tick += async (_, _) =>
        {
            _previewDebounce.Stop();
            await RefreshPreviewAsync().ConfigureAwait(true);
        };

        CountriesView = new CollectionViewSource { Source = Countries };
        CountriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CountryRowViewModel.Continent)));
        ApplySort();
    }

    public ActivityLogViewModel Log { get; } = new();

    public ObservableCollection<CountryRowViewModel> Countries { get; } = [];

    public CollectionViewSource CountriesView { get; }

    public ICollectionView CountriesCollectionView => CountriesView.View;

    public AlertService Alerts => _alerts;

    // ------------------------------------------------------------ calibration

    [ObservableProperty] private int _topLeftX = CaptureRect.Default.TopLeftX;
    [ObservableProperty] private int _topLeftY = CaptureRect.Default.TopLeftY;
    [ObservableProperty] private int _bottomRightX = CaptureRect.Default.BottomRightX;
    [ObservableProperty] private int _bottomRightY = CaptureRect.Default.BottomRightY;

    [ObservableProperty] private string _cursorPosition = "–, –";

    public string RegionSize => $"{Math.Abs(BottomRightX - TopLeftX)} × {Math.Abs(BottomRightY - TopLeftY)} px";

    // -------------------------------------------------------------- ocr tuning

    [ObservableProperty] private int _upscaleFactor = OcrSettings.Default.UpscaleFactor;
    [ObservableProperty] private int _threshold = OcrSettings.Default.Threshold;
    [ObservableProperty] private bool _blackAndWhite = true;
    [ObservableProperty] private InvertMode _invert = OcrSettings.Default.Invert;
    [ObservableProperty] private int _quietZonePixels = OcrSettings.Default.QuietZonePixels;
    [ObservableProperty] private int _strokeThickenPasses;
    [ObservableProperty] private bool _useWhitelist = true;
    [ObservableProperty] private string _whitelist = OcrSettings.Default.CharacterWhitelist;
    [ObservableProperty] private bool _useLegacyEngine;

    public static Array InvertModes => Enum.GetValues<InvertMode>();

    public static Array DateOrders => Enum.GetValues<DateOrder>();

    [ObservableProperty] private DateOrder _preferredDateOrder = DateOrder.DayFirst;
    [ObservableProperty] private bool _autoLearnDateOrder = true;

    // ----------------------------------------------------------------- preview

    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string _rawText = "–";
    [ObservableProperty] private string _parsedDate = "–";
    [ObservableProperty] private string _confidenceText = "–";
    [ObservableProperty] private string _previewWarning = string.Empty;
    [ObservableProperty] private bool _hasPreviewWarning;

    // ----------------------------------------------------------------- runtime

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnipCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCountriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeselectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectTopTierCommand))]
    private bool _isMonitoring;

    public bool IsIdle => !IsMonitoring;

    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private string _lastSeenDate = "–";
    [ObservableProperty] private string _nextIntakeText = "–";
    [ObservableProperty] private string _engineStatus = "–";
    [ObservableProperty] private string _gameStatus = "–";

    // -------------------------------------------------------------- selection

    [ObservableProperty] private CountrySort _sortBy = CountrySort.Name;
    [ObservableProperty] private int _topTierThreshold = 130;
    [ObservableProperty] private bool _topTierIncludesLowerLeagues;
    [ObservableProperty] private int _leadDays;
    [ObservableProperty] private int _pollIntervalMs = 1000;
    [ObservableProperty] private bool _requireGameRunning = true;
    [ObservableProperty] private bool _requireGameForeground = true;
    [ObservableProperty] private string _gameProcessName = "fm";
    [ObservableProperty] private bool _alertSound = true;
    [ObservableProperty] private bool _alertFlashWindow = true;
    [ObservableProperty] private bool _alertBringToFront;
    [ObservableProperty] private bool _alertTrayBalloon = true;
    [ObservableProperty] private bool _minimiseToTray = true;

    public string SelectedSummary =>
        $"{Countries.Count(c => c.IsSelected)} of {Countries.Count} selected";

    // =====================================================================
    //  Lifecycle
    // =====================================================================

    public void Initialise(string catalogPath, string tessDataPath)
    {
        var settings = _store.LoadSettings();
        if (settings.HasWarning) Log.Add(LogSeverity.Warning, settings.Warning!);
        ApplySettings(settings.Value);

        LoadCountries(catalogPath, settings.Value);

        var engine = new TesseractOcrEngine(tessDataPath);
        EngineStatus = engine.IsAvailable
            ? $"Tesseract, language data in {Path.GetFileName(tessDataPath)}"
            : $"Unavailable — {engine.UnavailableReason}";

        if (!engine.IsAvailable)
        {
            Log.Add(LogSeverity.Error, $"OCR engine unavailable: {engine.UnavailableReason}");
        }

        var gate = new GameGate(BuildSettings());
        _reader = new DateReader(new ScreenCapture(), engine, gate);

        _monitor = new MonitoringService(
            _reader,
            BuildSettings,
            () => _calendar,
            () => _selectedCodes);
        _monitor.Sampled += OnSampled;

        var state = _store.LoadState();
        if (state.HasWarning) Log.Add(LogSeverity.Warning, state.Warning!);
        _monitor.Restore(SettingsStore.ToTrackerState(state.Value), state.Value.LearnedDateOrder);
        if (state.Value.LastSeenInGameDate is { } seen)
        {
            LastSeenDate = seen.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        _cursor.Start();
        _suppressPersist = false;

        Log.Add(LogSeverity.Info, "Ready. Calibrate the region, tick some countries, then press Start.");
        UpdateNextIntake();
        RequestPreview();
    }

    private void LoadCountries(string catalogPath, AppSettings settings)
    {
        var result = CountryCatalog.LoadOrSeed(catalogPath);
        foreach (string warning in result.Warnings) Log.Add(LogSeverity.Warning, warning);

        Log.Add(result.Source switch
        {
            CatalogSource.SeedBecauseMissing => LogSeverity.Info,
            CatalogSource.SeedBecauseUnreadable => LogSeverity.Warning,
            _ => LogSeverity.Info,
        }, result.Source switch
        {
            CatalogSource.SeedBecauseMissing => $"Created {catalogPath} with {result.Rules.Count} countries.",
            CatalogSource.SeedBecauseUnreadable => "Using the built-in dataset because the spreadsheet could not be read.",
            _ => $"Loaded {result.Rules.Count} countries from {Path.GetFileName(catalogPath)}.",
        });

        Countries.Clear();
        var chosen = new HashSet<string>(settings.SelectedCountryCodes, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in result.Rules)
        {
            var row = new CountryRowViewModel(rule)
            {
                // On a first run there is nothing saved yet, so fall back to the
                // dataset's own defaults rather than starting with nothing armed.
                IsSelected = settings.SelectionInitialised
                    ? chosen.Contains(rule.Code)
                    : rule.EnabledByDefault,
            };
            row.PropertyChanged += OnCountryRowChanged;
            Countries.Add(row);
        }

        RebuildSelection();
        ApplySort();
    }

    private void OnCountryRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CountryRowViewModel.IsSelected)) return;
        RebuildSelection();
        Persist();
    }

    private void RebuildSelection()
    {
        _selectedCodes = Countries.Where(c => c.IsSelected)
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _calendar = new TriggerCalendar(Countries.Select(c => c.Rule), LeadDays);
        OnPropertyChanged(nameof(SelectedSummary));
        UpdateNextIntake();
    }

    private void UpdateNextIntake()
    {
        var armed = Countries.Where(c => c.IsSelected).Select(c => c.Rule).ToList();
        if (armed.Count == 0)
        {
            NextIntakeText = "No countries selected";
            return;
        }

        var from = _monitor?.State.LastSeen ?? DateOnly.FromDateTime(DateTime.Today);
        var next = new TriggerCalendar(armed, LeadDays).Next(from);

        NextIntakeText = next is null
            ? "–"
            : $"{next.Rule.Country} in {next.FireOn.DayNumber - from.DayNumber} day(s) " +
              $"({next.FireOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})";
    }

    // =====================================================================
    //  Settings plumbing
    // =====================================================================

    public AppSettings BuildSettings() => new()
    {
        Capture = new CaptureRect
        {
            TopLeftX = TopLeftX,
            TopLeftY = TopLeftY,
            BottomRightX = BottomRightX,
            BottomRightY = BottomRightY,
        },
        Ocr = new OcrSettings
        {
            UpscaleFactor = UpscaleFactor,
            Threshold = Threshold,
            Mode = BlackAndWhite ? ThresholdMode.BlackAndWhite : ThresholdMode.Grayscale,
            Invert = Invert,
            QuietZonePixels = QuietZonePixels,
            StrokeThickenPasses = StrokeThickenPasses,
            UseCharacterWhitelist = UseWhitelist,
            CharacterWhitelist = Whitelist,
            UseLegacyEngine = UseLegacyEngine,
        },
        SelectedCountryCodes = [.. _selectedCodes],
        SelectionInitialised = true,
        TopTierThreshold = TopTierThreshold,
        TopTierIncludesLowerLeagues = TopTierIncludesLowerLeagues,
        LeadDays = LeadDays,
        PollIntervalMs = PollIntervalMs,
        PreferredDateOrder = PreferredDateOrder,
        AutoLearnDateOrder = AutoLearnDateOrder,
        RequireGameRunning = RequireGameRunning,
        RequireGameForeground = RequireGameForeground,
        GameProcessName = GameProcessName,
        AlertSound = AlertSound,
        AlertFlashWindow = AlertFlashWindow,
        AlertBringToFront = AlertBringToFront,
        AlertTrayBalloon = AlertTrayBalloon,
        MinimiseToTray = MinimiseToTray,
    };

    private void ApplySettings(AppSettings s)
    {
        _suppressPersist = true;

        TopLeftX = s.Capture.TopLeftX;
        TopLeftY = s.Capture.TopLeftY;
        BottomRightX = s.Capture.BottomRightX;
        BottomRightY = s.Capture.BottomRightY;

        UpscaleFactor = s.Ocr.UpscaleFactor;
        Threshold = s.Ocr.Threshold;
        BlackAndWhite = s.Ocr.Mode == ThresholdMode.BlackAndWhite;
        Invert = s.Ocr.Invert;
        QuietZonePixels = s.Ocr.QuietZonePixels;
        StrokeThickenPasses = s.Ocr.StrokeThickenPasses;
        UseWhitelist = s.Ocr.UseCharacterWhitelist;
        Whitelist = s.Ocr.CharacterWhitelist;
        UseLegacyEngine = s.Ocr.UseLegacyEngine;

        TopTierThreshold = s.TopTierThreshold;
        TopTierIncludesLowerLeagues = s.TopTierIncludesLowerLeagues;
        LeadDays = s.LeadDays;
        PollIntervalMs = s.PollIntervalMs;
        PreferredDateOrder = s.PreferredDateOrder;
        AutoLearnDateOrder = s.AutoLearnDateOrder;
        RequireGameRunning = s.RequireGameRunning;
        RequireGameForeground = s.RequireGameForeground;
        GameProcessName = s.GameProcessName;
        AlertSound = s.AlertSound;
        AlertFlashWindow = s.AlertFlashWindow;
        AlertBringToFront = s.AlertBringToFront;
        AlertTrayBalloon = s.AlertTrayBalloon;
        MinimiseToTray = s.MinimiseToTray;

        _suppressPersist = false;
    }

    public void Persist()
    {
        if (_suppressPersist || _disposed) return;
        try
        {
            _store.SaveSettings(BuildSettings());
        }
        catch (Exception ex)
        {
            Log.Add(LogSeverity.Warning, $"Could not save settings: {ex.Message}");
        }
    }

    private void PersistState()
    {
        if (_monitor is null || _disposed) return;
        try
        {
            _store.SaveState(SettingsStore.FromTrackerState(_monitor.State, _monitor.LearnedOrder));
        }
        catch (Exception ex)
        {
            Log.Add(LogSeverity.Warning, $"Could not save tracking state: {ex.Message}");
        }
    }

    // =====================================================================
    //  Property reactions
    // =====================================================================

    partial void OnTopLeftXChanged(int value) => OnRegionChanged();
    partial void OnTopLeftYChanged(int value) => OnRegionChanged();
    partial void OnBottomRightXChanged(int value) => OnRegionChanged();
    partial void OnBottomRightYChanged(int value) => OnRegionChanged();

    private void OnRegionChanged()
    {
        OnPropertyChanged(nameof(RegionSize));
        RequestPreview();
        Persist();
    }

    partial void OnUpscaleFactorChanged(int value) => RequestPreview();
    partial void OnThresholdChanged(int value) => RequestPreview();
    partial void OnBlackAndWhiteChanged(bool value) => RequestPreview();
    partial void OnInvertChanged(InvertMode value) => RequestPreview();
    partial void OnQuietZonePixelsChanged(int value) => RequestPreview();
    partial void OnStrokeThickenPassesChanged(int value) => RequestPreview();
    partial void OnUseWhitelistChanged(bool value) => RequestPreview();
    partial void OnWhitelistChanged(string value) => RequestPreview();
    partial void OnUseLegacyEngineChanged(bool value) => RequestPreview();
    partial void OnPreferredDateOrderChanged(DateOrder value) => RequestPreview();
    partial void OnAutoLearnDateOrderChanged(bool value) => RequestPreview();

    partial void OnSortByChanged(CountrySort value) => ApplySort();

    partial void OnLeadDaysChanged(int value)
    {
        RebuildSelection();
        Persist();
    }

    /// <summary>
    /// Schedules a preview refresh.
    /// </summary>
    /// <remarks>
    /// Debounced because dragging a slider raises a change per pixel of travel,
    /// and each refresh is a screen grab plus a full OCR pass. Without this a
    /// single drag would queue hundreds of reads and the UI would lock up.
    /// </remarks>
    private void RequestPreview()
    {
        Persist();
        if (_disposed) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    public async Task RefreshPreviewAsync()
    {
        if (_monitor is null || _disposed) return;

        // respectGate: false. A preview is an explicit foreground action by the
        // user, so it works with the game closed — otherwise the app could not be
        // calibrated before launching FM. Nothing read here reaches the log file.
        var report = await _monitor.SampleAsync(respectGate: false).ConfigureAwait(true);
        if (report is null) return;

        ApplyPreview(report.Outcome);
    }

    private void ApplyPreview(ReadOutcome outcome)
    {
        if (outcome.PreprocessedFrame is { } frame)
        {
            PreviewImage = FrameImageSource.Create(frame);
        }

        RawText = string.IsNullOrWhiteSpace(outcome.RawText) ? "–" : outcome.RawText;

        ParsedDate = outcome.Observation.Date is { } date
            ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : "–";

        ConfidenceText = outcome.Observation.Status == SampleStatus.Ok
            ? $"mean {outcome.Observation.MeanConfidence:P0}, weakest glyph {outcome.Observation.MinSymbolConfidence:P0}"
            : "–";

        string? warning = outcome.Observation.Status switch
        {
            SampleStatus.CaptureFailed => outcome.Observation.FailureDetail,
            SampleStatus.Unreadable when outcome.Observation.FailureDetail is { } d => d,
            _ => null,
        };

        if (outcome.InkTouchesEdge)
        {
            warning = "Text is touching the edge of the region. Widen it — a clipped 7 reads as a 1.";
        }

        PreviewWarning = warning ?? string.Empty;
        HasPreviewWarning = warning is not null;
    }

    // =====================================================================
    //  Sampling
    // =====================================================================

    private void OnSampled(SampleReport report)
    {
        // Raised on a background thread; everything below touches the UI.
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            ApplyPreview(report.Outcome);
            GameStatus = DescribeGate(report.Outcome);

            foreach (var e in report.Events) Record(e);

            if (report.State.LastSeen is { } seen)
            {
                LastSeenDate = seen.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }

            if (report.Events.Count > 0)
            {
                PersistState();
                UpdateNextIntake();
            }
        });
    }

    private static string DescribeGate(ReadOutcome outcome) => outcome.Gate switch
    {
        GateVerdict.GameNotRunning => "Football Manager is not running",
        GateVerdict.GameNotInForeground => "Football Manager is not the active window",
        _ => outcome.Observation.Status switch
        {
            SampleStatus.Ok => "Reading",
            SampleStatus.CaptureFailed => "Capture failed",
            SampleStatus.NoDatePresent => "No date on screen",
            SampleStatus.Unreadable => "Unreadable",
            _ => "–",
        },
    };

    private void Record(TrackerEvent e)
    {
        if (e.IsAlert && e.Trigger is { } trigger)
        {
            bool delivered = _alerts.Raise(new TrackerEventDescriptor(
                $"{trigger.Rule.Country} youth intake",
                e.Message,
                trigger.Key));

            Log.Add(LogSeverity.Alert, delivered
                ? e.Message
                : $"{e.Message}  (repeat suppressed)");
            return;
        }

        Log.Add(e.Kind switch
        {
            TrackerEventKind.JumpQuarantined => LogSeverity.Warning,
            TrackerEventKind.SampleRejected => LogSeverity.Detail,
            TrackerEventKind.AwaitingConfirmation => LogSeverity.Detail,
            TrackerEventKind.ClockRegressed => LogSeverity.Warning,
            TrackerEventKind.TriggersReArmed => LogSeverity.Success,
            TrackerEventKind.DateAdopted => LogSeverity.Success,
            _ => LogSeverity.Info,
        }, e.Message);
    }

    // =====================================================================
    //  Commands
    // =====================================================================

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Start()
    {
        if (_monitor is null) return;

        if (_selectedCodes.Count == 0)
        {
            Log.Add(LogSeverity.Warning, "No countries selected — nothing will alert. Tick at least one.");
        }

        if (AlertService.NotificationsAreSuppressed())
        {
            Log.Add(LogSeverity.Warning,
                "Windows is currently suppressing notifications (Focus Assist or a fullscreen app). " +
                "Sound and window flash will still work.");
        }

        IsMonitoring = true;
        StatusText = "Monitoring";
        Log.Add(LogSeverity.Success, $"Monitoring started — checking every {PollIntervalMs} ms.");
        _monitor.Start();
        Persist();
    }

    [RelayCommand(CanExecute = nameof(IsMonitoring))]
    private async Task StopAsync()
    {
        if (_monitor is null) return;

        await _monitor.StopAsync().ConfigureAwait(true);
        IsMonitoring = false;
        StatusText = "Idle";
        GameStatus = "–";
        Log.Add(LogSeverity.Info, "Monitoring stopped.");
        PersistState();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectAll() => SetAll(_ => true);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void DeselectAll() => SetAll(_ => false);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectTopTier() => SetAll(c =>
        c.YouthRating >= TopTierThreshold &&
        (TopTierIncludesLowerLeagues || !c.IsLowerLeague));

    private void SetAll(Func<CountryRowViewModel, bool> predicate)
    {
        foreach (var row in Countries) row.IsSelected = predicate(row);
        Log.Add(LogSeverity.Info, $"{Countries.Count(c => c.IsSelected)} countries selected.");
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ReloadCountries()
    {
        foreach (var row in Countries) row.PropertyChanged -= OnCountryRowChanged;
        LoadCountries(App.CatalogPath, BuildSettings());
        Persist();
    }

    [RelayCommand]
    private void ResetTuning()
    {
        var d = OcrSettings.Default;
        UpscaleFactor = d.UpscaleFactor;
        Threshold = d.Threshold;
        BlackAndWhite = d.Mode == ThresholdMode.BlackAndWhite;
        Invert = d.Invert;
        QuietZonePixels = d.QuietZonePixels;
        StrokeThickenPasses = d.StrokeThickenPasses;
        UseWhitelist = d.UseCharacterWhitelist;
        Whitelist = d.CharacterWhitelist;
        UseLegacyEngine = d.UseLegacyEngine;
        Log.Add(LogSeverity.Info, "OCR settings reset to the validated defaults.");
    }

    [RelayCommand]
    private void ResetRegion()
    {
        var d = CaptureRect.Default;
        TopLeftX = d.TopLeftX;
        TopLeftY = d.TopLeftY;
        BottomRightX = d.BottomRightX;
        BottomRightY = d.BottomRightY;
    }

    [RelayCommand]
    private void TestAlert()
    {
        _alerts.Raise(new TrackerEventDescriptor(
            "Test alert",
            "This is what a youth intake alert looks like.",
            Key: null));
        Log.Add(LogSeverity.Alert, "Test alert raised.");
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    /// <summary>Set by the view, which owns the window needed to show the overlay.</summary>
    public Func<CaptureRect?>? SnipHandler { get; set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Snip()
    {
        if (SnipHandler?.Invoke() is not { } rect) return;

        TopLeftX = rect.TopLeftX;
        TopLeftY = rect.TopLeftY;
        BottomRightX = rect.BottomRightX;
        BottomRightY = rect.BottomRightY;
        Log.Add(LogSeverity.Success,
            $"Region set to {rect.Left}, {rect.Top} — {rect.Width} × {rect.Height} px.");
    }

    private void ApplySort()
    {
        var view = CountriesView;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();

            // The continent key must sort first or grouping fragments: WPF forms
            // groups from consecutive runs, so an unsorted group key produces one
            // group per run rather than one per continent.
            view.SortDescriptions.Add(new SortDescription(
                nameof(CountryRowViewModel.Continent), ListSortDirection.Ascending));

            view.SortDescriptions.Add(SortBy switch
            {
                CountrySort.YouthRating => new SortDescription(
                    nameof(CountryRowViewModel.YouthRating), ListSortDirection.Descending),
                CountrySort.RegenDate => new SortDescription(
                    nameof(CountryRowViewModel.WindowSortKey), ListSortDirection.Ascending),
                _ => new SortDescription(
                    nameof(CountryRowViewModel.Country), ListSortDirection.Ascending),
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _previewDebounce.Stop();
        _cursor.Dispose();
        _monitor?.Dispose();
        _alerts.Dispose();
    }
}
