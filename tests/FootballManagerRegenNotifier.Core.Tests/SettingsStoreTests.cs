using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;
using FootballManagerRegenNotifier.Core.Tracking;
using Xunit;

namespace FootballManagerRegenNotifier.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "fmrn-settings-" + Guid.NewGuid().ToString("N"));

    private SettingsStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Defaults_MatchTheValidatedCalibration()
    {
        var s = AppSettings.Default;

        Assert.Equal(2035, s.Capture.TopLeftX);
        Assert.Equal(5, s.Capture.TopLeftY);
        Assert.Equal(2180, s.Capture.BottomRightX);
        Assert.Equal(40, s.Capture.BottomRightY);
        Assert.Equal(145, s.Capture.Width);
        Assert.Equal(35, s.Capture.Height);

        Assert.Equal(4, s.Ocr.UpscaleFactor);
        Assert.Equal(128, s.Ocr.Threshold);
        Assert.Equal(ThresholdMode.BlackAndWhite, s.Ocr.Mode);
        Assert.True(s.Ocr.UseCharacterWhitelist);
        Assert.Equal("0123456789/", s.Ocr.CharacterWhitelist);
    }

    [Fact]
    public void MissingFiles_YieldDefaultsWithoutWarning()
    {
        var result = Store.LoadSettings();

        Assert.False(result.FileExisted);
        Assert.False(result.HasWarning);
        Assert.True(SettingsComparer.AreEquivalent(AppSettings.Default, result.Value));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var store = Store;
        var settings = AppSettings.Default with
        {
            Capture = new CaptureRect { TopLeftX = 10, TopLeftY = 20, BottomRightX = 110, BottomRightY = 60 },
            Ocr = OcrSettings.Default with { UpscaleFactor = 6, Threshold = 90, Invert = InvertMode.Always },
            SelectedCountryCodes = ["ENG", "BRA"],
            LeadDays = 7,
            PreferredDateOrder = DateOrder.MonthFirst,
        };

        store.SaveSettings(settings);
        var loaded = store.LoadSettings();

        Assert.True(loaded.FileExisted);
        Assert.False(loaded.HasWarning);
        Assert.True(SettingsComparer.AreEquivalent(settings, loaded.Value));
        Assert.Equal(settings.SelectedCountryCodes, loaded.Value.SelectedCountryCodes);
    }

    [Fact]
    public void CorruptSettings_FallBackToDefaultsAndReportWhy()
    {
        var store = Store;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.SettingsPath, "{ not json at all");

        var loaded = store.LoadSettings();

        Assert.True(SettingsComparer.AreEquivalent(AppSettings.Default, loaded.Value));
        Assert.True(loaded.HasWarning);
    }

    [Fact]
    public void EmptySettingsFile_FallsBackToDefaults()
    {
        var store = Store;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.SettingsPath, "null");

        var loaded = store.LoadSettings();

        Assert.True(SettingsComparer.AreEquivalent(AppSettings.Default, loaded.Value));
        Assert.True(loaded.HasWarning);
    }

    [Fact]
    public void SavingState_LeavesSettingsUntouched()
    {
        // The whole point of splitting the two files: the per-tick write must not
        // be able to damage a laboriously tuned capture rectangle.
        var store = Store;
        var settings = AppSettings.Default with
        {
            Capture = new CaptureRect { TopLeftX = 999, TopLeftY = 888, BottomRightX = 1100, BottomRightY = 950 },
        };
        store.SaveSettings(settings);

        for (int i = 0; i < 50; i++)
        {
            store.SaveState(new RuntimeState { LastSeenInGameDate = new DateOnly(2026, 3, i % 28 + 1) });
        }

        Assert.Equal(999, store.LoadSettings().Value.Capture.TopLeftX);
    }

    [Fact]
    public void RepeatedSaves_DoNotLeaveTempFilesBehind()
    {
        var store = Store;
        for (int i = 0; i < 5; i++) store.SaveSettings(AppSettings.Default);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void TrackerState_RoundTripsThroughPersistence()
    {
        var original = new TrackerState
        {
            LastSeen = new DateOnly(2026, 3, 14),
            Fired =
            [
                new TriggerKey("ENG", 2026, TriggerKind.WindowOpen),
                new TriggerKey("BRA", 2026, TriggerKind.Lead),
            ],
        };

        var persisted = SettingsStore.FromTrackerState(original, DateOrder.DayFirst);
        var store = Store;
        store.SaveState(persisted);
        var restored = SettingsStore.ToTrackerState(store.LoadState().Value);

        Assert.Equal(original.LastSeen, restored.LastSeen);
        Assert.Equal(original.Fired, restored.Fired);
    }

    [Fact]
    public void MalformedFiredKeys_AreDroppedRatherThanCrashingStartup()
    {
        var state = new RuntimeState
        {
            LastSeenInGameDate = new DateOnly(2026, 3, 14),
            FiredTriggerKeys = ["ENG:2026:WindowOpen", "garbage", "ENG:notayear:WindowOpen", "A:1:Nope"],
        };

        var restored = SettingsStore.ToTrackerState(state);

        Assert.Single(restored.Fired);
        Assert.Contains(new TriggerKey("ENG", 2026, TriggerKind.WindowOpen), restored.Fired);
    }

    [Fact]
    public void OcrSettings_ClampOutOfRangeValues()
    {
        var clamped = (OcrSettings.Default with
        {
            UpscaleFactor = 99,
            Threshold = -5,
            QuietZonePixels = 1000,
        }).Clamped();

        Assert.Equal(6, clamped.UpscaleFactor);
        Assert.Equal(0, clamped.Threshold);
        Assert.Equal(64, clamped.QuietZonePixels);
    }

    [Fact]
    public void CaptureRect_NormalisesCornersDrawnInAnyDirection()
    {
        // The snip overlay lets the user drag from any corner.
        var rect = new CaptureRect { TopLeftX = 200, TopLeftY = 100, BottomRightX = 50, BottomRightY = 20 };

        Assert.Equal(50, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(150, rect.Width);
        Assert.Equal(80, rect.Height);
        Assert.True(rect.IsValid);
    }

    [Fact]
    public void ZeroSizedRect_IsInvalid()
    {
        Assert.False(new CaptureRect { TopLeftX = 5, TopLeftY = 5, BottomRightX = 5, BottomRightY = 5 }.IsValid);
    }
}
