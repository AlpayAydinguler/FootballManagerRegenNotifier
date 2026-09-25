using FootballManagerRegenNotifier.Core.Settings;
using Xunit;

namespace FootballManagerRegenNotifier.Capture.Tests;

public class GameGateTests
{
    [Theory]
    [InlineData("fm", "fm")]
    [InlineData("fm.exe", "fm")]
    [InlineData("FM.EXE", "FM")]
    [InlineData("  fm.exe  ", "fm")]
    [InlineData("\"fm.exe\"", "fm")]
    [InlineData(@"D:\Games\Football Manager 26\fm.exe", "fm")]
    [InlineData("D:/Games/Football Manager 26/fm.exe", "fm")]
    public void ProcessName_IsNormalisedToWhatGetProcessesByNameExpects(string raw, string expected)
    {
        // Process.GetProcessesByName matches without the extension, so "fm.exe"
        // silently matches nothing and the gate reports the game as not running
        // while it is plainly on screen. Typing the executable name into a field
        // labelled "game process name" is the obvious thing to do.
        Assert.Equal(expected, GameGate.NormaliseProcessName(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProcessName_NormalisesToEmpty(string? raw)
    {
        Assert.Equal(string.Empty, GameGate.NormaliseProcessName(raw));
    }

    [Fact]
    public void GateDisabled_AlwaysAllows()
    {
        var gate = new GameGate(AppSettings.Default with { RequireGameRunning = false });

        Assert.True(gate.Check().Allowed);
    }

    [Fact]
    public void BlankProcessName_AllowsRatherThanBlockingForever()
    {
        // Clearing the field should not silently wedge the sampler shut.
        var gate = new GameGate(AppSettings.Default with { GameProcessName = "   " });

        Assert.True(gate.Check().Allowed);
    }

    [Fact]
    public void MissingProcess_ReportsWhatItLookedFor()
    {
        // "Football Manager is not running" is useless on its own when the game is
        // visibly open; the process name it searched for is the diagnostic.
        var gate = new GameGate(AppSettings.Default with
        {
            GameProcessName = "definitely-not-a-real-process.exe",
            RequireGameForeground = false,
        });

        var result = gate.Check();

        Assert.Equal(GateVerdict.GameNotRunning, result.Verdict);
        Assert.NotNull(result.Detail);
        Assert.Contains("definitely-not-a-real-process", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(".exe.exe", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_TakesNewSettingsWithoutRebuildingTheGate()
    {
        var gate = new GameGate(AppSettings.Default with { GameProcessName = "nope" });
        Assert.False(gate.Check().Allowed);

        gate.Update(AppSettings.Default with { RequireGameRunning = false });

        Assert.True(gate.Check().Allowed);
    }
}
