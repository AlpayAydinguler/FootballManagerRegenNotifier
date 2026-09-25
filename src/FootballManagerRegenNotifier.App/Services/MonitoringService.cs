using FootballManagerRegenNotifier.Capture;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;
using FootballManagerRegenNotifier.Core.Tracking;
using FootballManagerRegenNotifier.Core.Triggers;

namespace FootballManagerRegenNotifier.App.Services;

public sealed record SampleReport(
    ReadOutcome Outcome,
    IReadOnlyList<TrackerEvent> Events,
    TrackerState State,
    DateOrder? LearnedOrder);

/// <summary>
/// Drives the sampling loop.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="PeriodicTimer"/> with an <c>await</c>ed body rather than a
/// <c>DispatcherTimer</c>: the tick body does the capture, the OCR and the state
/// transition, which takes tens of milliseconds, and running that on the UI
/// thread would make the whole window stutter once a second.
/// </para>
/// <para>
/// Overlap is impossible by construction. <c>PeriodicTimer.WaitForNextTickAsync</c>
/// does not queue missed ticks, so a slow read simply delays the next one instead
/// of stacking a second read on top of it. The same semaphore is additionally
/// shared with the preview path, because a preview refresh and a scheduled sample
/// are two callers of one OCR engine and one capture surface.
/// </para>
/// </remarks>
public sealed class MonitoringService(
    DateReader reader,
    Func<AppSettings> settings,
    Func<TriggerCalendar> calendar,
    Func<IReadOnlySet<string>> selectedCodes) : IDisposable
{
    private readonly DateReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly Func<AppSettings> _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Func<TriggerCalendar> _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
    private readonly Func<IReadOnlySet<string>> _selected = selectedCodes ?? throw new ArgumentNullException(nameof(selectedCodes));

    /// <summary>
    /// Guards the single capture surface and OCR engine against the preview and
    /// the sampler running at once.
    /// </summary>
    private readonly SemaphoreSlim _readGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public TrackerState State { get; private set; } = TrackerState.Initial;

    public DateOrder? LearnedOrder { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised after every sample, on a background thread.</summary>
    public event Action<SampleReport>? Sampled;

    public void Restore(TrackerState state, DateOrder? learnedOrder)
    {
        State = state;
        LearnedOrder = learnedOrder;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null || loop is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        int interval = Math.Max(200, _settings().PollIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));

        // Sample immediately rather than waiting a full interval, so pressing
        // Start gives feedback at once.
        await SampleAsync(respectGate: true, token).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            await SampleAsync(respectGate: true, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes one reading and advances the tracker.
    /// </summary>
    /// <param name="respectGate">
    /// True for the sampling loop, which must honour the privacy gate. False for
    /// a user-initiated preview refresh: that is an explicit foreground action,
    /// and refusing it would make the app impossible to calibrate before the game
    /// is even launched. The caller is responsible for keeping preview text out
    /// of the rolling log.
    /// </param>
    public async Task<SampleReport?> SampleAsync(bool respectGate, CancellationToken token = default)
    {
        if (_disposed) return null;

        if (!await _readGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            // A read is already in flight. Skipping is correct: the next tick
            // will pick up a fresher frame than this one would have.
            return null;
        }

        try
        {
            var config = _settings();
            var outcome = _reader.Read(config, LearnedOrder, respectGate);

            if (outcome.LearnedOrder is { } learned) LearnedOrder = learned;

            IReadOnlyList<TrackerEvent> events = [];
            if (respectGate)
            {
                var selected = _selected();
                var tracker = new DateTracker(_calendar(), new TrackerConfig
                {
                    MinMeanConfidence = config.MinMeanConfidence,
                    MinSymbolConfidence = config.MinSymbolConfidence,
                });
                var step = tracker.Step(State, outcome.Observation, rule => selected.Contains(rule.Code));
                State = step.State;
                events = step.Events;
            }

            var report = new SampleReport(outcome, events, State, LearnedOrder);
            Sampled?.Invoke(report);
            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A single bad read must never take down the loop.
            var report = new SampleReport(
                new ReadOutcome { Observation = Observation.CaptureFailed(ex.Message) },
                [],
                State,
                LearnedOrder);
            Sampled?.Invoke(report);
            return report;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _readGate.Dispose();
        _reader.Dispose();
    }
}
