using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Default <see cref="IShadowDiagnosticsStore"/> implementation: an in-process, thread-safe
/// dictionary of retained records plus the <see cref="AsyncLocal{T}"/>-backed ambient capture scope
/// used to correlate a shadow run with the session id minted after it (see the interface remarks).
/// </summary>
public sealed class InMemoryShadowDiagnosticsStore : IShadowDiagnosticsStore
{
    // The ambient slot holds a mutable AmbientState instance while a BeginCapture scope is open,
    // and null otherwise. The null-vs-instance distinction (rather than storing the record
    // directly) is what lets Publish tell "no scope is open" apart from "a scope is open but
    // nothing has been published yet" - both of which would otherwise look like a null record.
    private readonly AsyncLocal<AmbientState?> _ambient = new();
    private readonly ConcurrentDictionary<PlaybackSessionId, ShadowDiagnosticRecord> _records = new();

    // PR113b: independent of _records - a session accrues lifecycle events (ffmpeg launched,
    // playback started/stopped) whether or not a shadow diagnostic was ever retained for it, so
    // this is keyed and evicted on the same PlaybackSessionId lifecycle but never gated on shadow
    // mode. ConcurrentQueue preserves observation order without needing an external lock.
    private readonly ConcurrentDictionary<PlaybackSessionId, ConcurrentQueue<PlaybackLifecycleEvent>> _events = new();

    /// <inheritdoc/>
    public ShadowCaptureInputs? CapturedInputs => _ambient.Value?.Inputs;

    /// <inheritdoc/>
    public IDisposable BeginCapture() => BeginCapture(null);

    /// <inheritdoc/>
    public IDisposable BeginCapture(ShadowCaptureInputs? inputs) => OpenCapture(inputs);

    /// <inheritdoc/>
    public IPlaybackPlanningDiagnostics BeginPlanning() => OpenCapture(CapturedInputs);

    private CaptureScope OpenCapture(ShadowCaptureInputs? inputs)
    {
        var previous = _ambient.Value;
        var captured = new AmbientState { Inputs = inputs };
        _ambient.Value = captured;
        return new CaptureScope(this, _ambient, previous, captured);
    }

    /// <inheritdoc/>
    public void Publish(ShadowDiagnosticRecord record)
    {
        var state = _ambient.Value;
        if (state is null)
        {
            // PR113a: no BeginCapture scope is open on this async flow. This should not happen
            // from ShadowPlaybackSessionPlanner (it only calls Publish from inside the scope
            // PlaybackSessionManager opens around Plan()), but guard it anyway rather than
            // silently corrupting some unrelated caller's ambient slot. This type has no
            // ILogger (see class remarks) and shadow diagnostics are best-effort, so the record
            // is dropped instead of thrown - a lost diagnostic must never fail live playback.
            return;
        }

        state.Record = record;
    }

    /// <inheritdoc/>
    public ShadowDiagnosticRecord? TakeCaptured() => _ambient.Value?.Record;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record) => _records[id] = record;

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record) => _records.TryGetValue(id, out record);

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id)
    {
        _records.TryRemove(id, out _);
        _events.TryRemove(id, out _);
    }

    /// <inheritdoc/>
    public void RecordEvent(PlaybackSessionId id, PlaybackLifecycleEvent lifecycleEvent) =>
        _events.GetOrAdd(id, static _ => new ConcurrentQueue<PlaybackLifecycleEvent>()).Enqueue(lifecycleEvent);

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackLifecycleEvent> GetEvents(PlaybackSessionId id) =>
        _events.TryGetValue(id, out var queue) ? queue.ToArray() : Array.Empty<PlaybackLifecycleEvent>();

    private sealed class AmbientState
    {
        public ShadowDiagnosticRecord? Record { get; set; }

        // Issue #75: set once when the scope opens and never mutated afterwards - unlike Record,
        // which the shadow run publishes into. Nothing derived from it is retained beyond the
        // counts/flags the published record's ContractMapping carries.
        public ShadowCaptureInputs? Inputs { get; init; }
    }

    private sealed class CaptureScope : IPlaybackPlanningDiagnostics
    {
        private readonly InMemoryShadowDiagnosticsStore _owner;
        private readonly AmbientState _captured;
        private readonly AsyncLocal<AmbientState?> _ambient;
        private readonly AmbientState? _previous;
        private bool _disposed;

        public CaptureScope(InMemoryShadowDiagnosticsStore owner, AsyncLocal<AmbientState?> ambient, AmbientState? previous, AmbientState captured)
        {
            _owner = owner;
            _captured = captured;
            _ambient = ambient;
            _previous = previous;
        }

        public void AttachTo(PlaybackSessionId id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_captured.Record is { } record)
            {
                _owner.Attach(id, record);
            }
            else
            {
                _owner.Remove(id);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ambient.Value = _previous;
        }
    }
}
