using FootballManagerRegenNotifier.Capture.Ocr;
using FootballManagerRegenNotifier.Capture.Preprocess;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.Capture;

/// <summary>
/// Everything one reading produces, including the intermediate image, so the
/// tuning preview and the sampler can never diverge.
/// </summary>
public sealed record ReadOutcome
{
    public required Observation Observation { get; init; }

    /// <summary>The exact image handed to the OCR engine. Null if capture failed.</summary>
    public CapturedFrame? PreprocessedFrame { get; init; }

    public string RawText { get; init; } = string.Empty;

    public string NormalisedText { get; init; } = string.Empty;

    public bool Inverted { get; init; }

    /// <summary>Ink is touching the image border, so the rectangle is probably too tight.</summary>
    public bool InkTouchesEdge { get; init; }

    public DateOrder? LearnedOrder { get; init; }

    public GateVerdict Gate { get; init; } = GateVerdict.Allowed;

    /// <summary>The gate's own explanation, e.g. which process name it looked for.</summary>
    public string? GateDetail { get; init; }
}

/// <summary>
/// Turns the screen into an <see cref="Observation"/>.
/// </summary>
/// <remarks>
/// The preview and the monitoring loop both go through here, which is what
/// guarantees that what the user tunes against is byte-for-byte what the tracker
/// later acts on. <paramref name="respectGate"/> is the one difference: the
/// sampler honours the privacy gate, while an explicit preview refresh is a
/// foreground user action and is allowed to run with the game closed. Without
/// that exemption the app would be impossible to calibrate before launching FM.
/// </remarks>
public sealed class DateReader(IScreenCapture capture, IOcrEngine engine, GameGate gate) : IDisposable
{
    private readonly IScreenCapture _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly IOcrEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly GameGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public IOcrEngine Engine => _engine;

    public ReadOutcome Read(AppSettings settings, DateOrder? learnedOrder, bool respectGate)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (respectGate)
        {
            var verdict = _gate.Check();
            if (!verdict.Allowed)
            {
                return new ReadOutcome
                {
                    Observation = Observation.GameNotRunning(),
                    Gate = verdict.Verdict,
                    GateDetail = verdict.Detail,
                };
            }
        }

        var grab = _capture.Capture(settings.Capture);
        if (!grab.Success)
        {
            return new ReadOutcome
            {
                Observation = Observation.CaptureFailed(grab.Error ?? "Unknown capture failure."),
            };
        }

        var frame = grab.Frame!;
        if (frame.IsUniform())
        {
            // Solid colour is how a blocked grab presents; GDI reports success and
            // hands back black rather than failing outright.
            return new ReadOutcome
            {
                Observation = Observation.CaptureFailed(
                    "Captured region is a single flat colour. The game may be in exclusive fullscreen."),
                PreprocessedFrame = Preprocessor.Run(frame, settings.Ocr).Frame,
            };
        }

        var processed = Preprocessor.Run(frame, settings.Ocr);
        var reading = _engine.Read(processed.Frame, settings.Ocr);

        if (reading.Failed)
        {
            return new ReadOutcome
            {
                Observation = Observation.Unreadable(string.Empty, reading.Error),
                PreprocessedFrame = processed.Frame,
                Inverted = processed.Inverted,
                InkTouchesEdge = processed.InkTouchesEdge,
            };
        }

        var parsed = FmDateParser.Parse(reading.Text, new DateParseOptions
        {
            Preferred = settings.PreferredDateOrder,
            AutoLearnOrder = settings.AutoLearnDateOrder,
            LearnedOrder = learnedOrder,
        });

        Observation observation = parsed.Date is { } date
            ? Observation.Read(date, reading.Text, reading.MeanConfidence, reading.MinSymbolConfidence)
            : parsed.Normalised.Length == 0
                ? Observation.NoDate(reading.Text)
                : Observation.Unreadable(reading.Text, parsed.Failure);

        return new ReadOutcome
        {
            Observation = observation,
            PreprocessedFrame = processed.Frame,
            RawText = reading.Text,
            NormalisedText = parsed.Normalised,
            Inverted = processed.Inverted,
            InkTouchesEdge = processed.InkTouchesEdge,
            LearnedOrder = parsed.LearnedOrder,
        };
    }

    public void Dispose()
    {
        _capture.Dispose();
        _engine.Dispose();
    }
}
