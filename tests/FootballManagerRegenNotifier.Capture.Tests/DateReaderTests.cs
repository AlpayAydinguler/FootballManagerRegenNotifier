using FootballManagerRegenNotifier.Capture.Ocr;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Settings;
using Xunit;

namespace FootballManagerRegenNotifier.Capture.Tests;

public class DateReaderTests
{
    private sealed class NoScreen : IScreenCapture
    {
        public CaptureOutcome Capture(CaptureRect rect) => CaptureOutcome.Fail("No screen in tests.");

        public void Dispose() { }
    }

    private sealed class NoEngine : IOcrEngine
    {
        public string Name => "none";

        public bool IsAvailable => false;

        public string? UnavailableReason => "No engine in tests.";

        public OcrReading Read(CapturedFrame preprocessedFrame, OcrSettings settings) =>
            throw new InvalidOperationException("Never reached: the capture fails first.");

        public void Dispose() { }
    }

    [Fact]
    public void Gate_FollowsTheSettingsOfEachRead_NotTheOnesItWasBuiltWith()
    {
        // The gate is built once, from the settings at startup. Editing the
        // process name or a gate checkbox used to do nothing until a restart,
        // which made the troubleshooting advice to change the name a dead end.
        var startup = AppSettings.Default with
        {
            RequireGameRunning = true,
            GameProcessName = "fmregen-test-no-such-process",
        };
        using var reader = new DateReader(new NoScreen(), new NoEngine(), new GameGate(startup));

        var gated = reader.Read(startup, learnedOrder: null, respectGate: true);
        Assert.Equal(GateVerdict.GameNotRunning, gated.Gate);

        var edited = startup with { RequireGameRunning = false };
        var open = reader.Read(edited, learnedOrder: null, respectGate: true);

        Assert.Equal(GateVerdict.Allowed, open.Gate);
        Assert.Equal(SampleStatus.CaptureFailed, open.Observation.Status);
    }
}
