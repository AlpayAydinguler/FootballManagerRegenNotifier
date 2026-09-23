using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Tracking;

namespace FootballManagerRegenNotifier.Core.Settings;

/// <summary>
/// Volatile tracking state. Rewritten whenever the game clock moves.
/// </summary>
public sealed record RuntimeState
{
    public int SchemaVersion { get; init; } = 1;

    public DateOnly? LastSeenInGameDate { get; init; }

    /// <summary>Occurrence keys already alerted, as <c>CODE:YEAR:Kind</c>.</summary>
    public string[] FiredTriggerKeys { get; init; } = [];

    /// <summary>Component order learned from an unambiguous on-screen date.</summary>
    public DateOrder? LearnedDateOrder { get; init; }

    public static readonly RuntimeState Empty = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RuntimeState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves the two JSON files under <c>%APPDATA%\FMRegenNotifier</c>.
/// </summary>
/// <remarks>
/// <para>
/// Settings and runtime state are separate files on purpose. Settings change
/// when the user touches a control; state changes every time the clock moves.
/// Sharing one file would put a laboriously tuned capture rectangle behind a
/// write that happens all session long.
/// </para>
/// <para>
/// Both write through a temporary file and then replace, so a crash or a power
/// cut during a write leaves the previous file intact rather than a truncated
/// one. Every load failure falls back to defaults and reports the reason: losing
/// settings is annoying, but refusing to start is worse.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    public const string AppFolderName = "FMRegenNotifier";

    private readonly string _settingsPath;
    private readonly string _statePath;

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory();
        _settingsPath = Path.Combine(Directory, "settings.json");
        _statePath = Path.Combine(Directory, "state.json");
    }

    public string Directory { get; }

    public string SettingsPath => _settingsPath;

    public string StatePath => _statePath;

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    public LoadResult<AppSettings> LoadSettings() =>
        Load(_settingsPath, SettingsJsonContext.Default.AppSettings, AppSettings.Default);

    public LoadResult<RuntimeState> LoadState() =>
        Load(_statePath, SettingsJsonContext.Default.RuntimeState, RuntimeState.Empty);

    public void SaveSettings(AppSettings settings) =>
        Save(_settingsPath, settings, SettingsJsonContext.Default.AppSettings);

    public void SaveState(RuntimeState state) =>
        Save(_statePath, state, SettingsJsonContext.Default.RuntimeState);

    private static LoadResult<T> Load<T>(string path, JsonTypeInfo<T> typeInfo, T fallback)
        where T : class
    {
        if (!File.Exists(path)) return new LoadResult<T>(fallback, false, null);

        try
        {
            string json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize(json, typeInfo);
            return value is null
                ? new LoadResult<T>(fallback, true, $"'{path}' was empty; defaults restored.")
                : new LoadResult<T>(value, true, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LoadResult<T>(fallback, true,
                $"Could not read '{path}' ({ex.Message}). Defaults restored.");
        }
    }

    private void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        System.IO.Directory.CreateDirectory(Directory);

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));

        if (File.Exists(path))
        {
            // Replace keeps the original intact if the swap itself fails.
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    // --- mapping between persisted state and the live tracker ---------------

    public static TrackerState ToTrackerState(RuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fired = state.FiredTriggerKeys
            .Select(ParseKey)
            .Where(k => k is not null)
            .Select(k => k!.Value);

        return new TrackerState
        {
            LastSeen = state.LastSeenInGameDate,
            Fired = [.. fired],
        };
    }

    public static RuntimeState FromTrackerState(TrackerState tracker, DateOrder? learnedOrder)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        return new RuntimeState
        {
            LastSeenInGameDate = tracker.LastSeen,
            FiredTriggerKeys = [.. tracker.Fired.Select(k => k.ToString())],
            LearnedDateOrder = learnedOrder,
        };
    }

    internal static Model.TriggerKey? ParseKey(string raw)
    {
        var parts = raw.Split(':');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[1], out int year)) return null;
        if (!Enum.TryParse<Model.TriggerKind>(parts[2], ignoreCase: true, out var kind)) return null;
        return new Model.TriggerKey(parts[0], year, kind);
    }
}

/// <summary>
/// Value comparison for settings.
/// </summary>
/// <remarks>
/// <see cref="AppSettings"/> is a record, but records compare array members by
/// reference, so <c>a == b</c> is false for two settings objects holding equal
/// country selections. Dirty-tracking with <c>==</c> would therefore report a
/// change on every comparison and rewrite the file constantly. Comparing the
/// serialised form is both correct and exactly what "has anything worth saving
/// changed" means for a file-backed setting.
/// </remarks>
public static class SettingsComparer
{
    public static bool AreEquivalent(AppSettings a, AppSettings b) =>
        string.Equals(Serialise(a), Serialise(b), StringComparison.Ordinal);

    public static string Serialise(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
}

public sealed record LoadResult<T>(T Value, bool FileExisted, string? Warning)
{
    public bool HasWarning => Warning is not null;
}
