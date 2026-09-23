using System.Diagnostics;
using FootballManagerRegenNotifier.Capture.Native;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.Capture;

public enum GateVerdict
{
    Allowed,
    GameNotRunning,
    GameNotInForeground,
}

public sealed record GateResult(GateVerdict Verdict, string? Detail)
{
    public bool Allowed => Verdict == GateVerdict.Allowed;
}

/// <summary>
/// Decides whether the sampler is permitted to read the screen.
/// </summary>
/// <remarks>
/// <para>
/// This is a privacy control, not a performance optimisation, and the difference
/// matters for how it is implemented. With the gate off or too lax, a monitoring
/// session left running reads whatever happens to occupy the calibrated
/// coordinates — mail, a bank page, a password manager — hands it to an OCR
/// engine and writes the result to a rolling log file on disk. Nothing about
/// that is hypothetical; it is the default behaviour of a screen scraper that
/// does not check what it is scraping.
/// </para>
/// <para>
/// So the check runs on <b>every</b> tick rather than on a cached timer, and it
/// defaults to also requiring the foreground window. Two P/Invokes per second is
/// not a cost worth trading a data-minimisation guarantee for.
/// </para>
/// <para>
/// Note that this gates the <b>sampler and the file log</b> only. The tuning
/// preview is an explicit, foreground, user-initiated action, and blocking it
/// would make the app impossible to calibrate before the game is launched.
/// </para>
/// </remarks>
public sealed class GameGate(AppSettings settings)
{
    private AppSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public void Update(AppSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public GateResult Check()
    {
        if (!_settings.RequireGameRunning) return new GateResult(GateVerdict.Allowed, null);

        string name = _settings.GameProcessName;
        if (string.IsNullOrWhiteSpace(name)) return new GateResult(GateVerdict.Allowed, null);

        Process[] matches;
        try
        {
            matches = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return new GateResult(GateVerdict.GameNotRunning, "Could not enumerate processes.");
        }

        try
        {
            var live = matches.Where(p => !HasExited(p)).ToArray();
            if (live.Length == 0)
            {
                return new GateResult(GateVerdict.GameNotRunning,
                    $"No '{name}.exe' process is running.");
            }

            // An explicit path distinguishes the real game from an unrelated
            // process that happens to share a very common executable name.
            if (!string.IsNullOrWhiteSpace(_settings.GameExecutablePath))
            {
                live = [.. live.Where(p => PathMatches(p, _settings.GameExecutablePath))];
                if (live.Length == 0)
                {
                    return new GateResult(GateVerdict.GameNotRunning,
                        $"No '{name}.exe' running from the configured path.");
                }
            }

            if (!_settings.RequireGameForeground) return new GateResult(GateVerdict.Allowed, null);

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return new GateResult(GateVerdict.GameNotInForeground, "No foreground window.");
            }

            NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            bool isGame = live.Any(p => SafePid(p) == foregroundPid);

            return isGame
                ? new GateResult(GateVerdict.Allowed, null)
                : new GateResult(GateVerdict.GameNotInForeground,
                    "Football Manager is running but is not the active window.");
        }
        finally
        {
            foreach (var p in matches) p.Dispose();
        }
    }

    /// <summary>Best-effort full path of the running game, for pre-filling the setting.</summary>
    public static string? TryResolveExecutablePath(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                or System.ComponentModel.Win32Exception
                                or NotSupportedException)
        {
            // Reading MainModule across a 32/64-bit boundary or without rights
            // throws. The path is a convenience; never let it be fatal.
        }
        return null;
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static uint SafePid(Process p)
    {
        try { return (uint)p.Id; }
        catch (InvalidOperationException) { return 0; }
    }

    private static bool PathMatches(Process p, string expected)
    {
        try
        {
            string? actual = p.MainModule?.FileName;
            return actual is not null &&
                   string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                or System.ComponentModel.Win32Exception
                                or NotSupportedException
                                or ArgumentException)
        {
            // If the path cannot be read, fall back to the name match rather than
            // failing closed and leaving the user with a monitor that never runs.
            return true;
        }
    }
}
