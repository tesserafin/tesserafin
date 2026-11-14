using System;
using System.Collections.Concurrent;
using System.Threading;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Default <see cref="IV2PlanStore"/> implementation: an in-process, thread-safe dictionary of
/// retained records plus the <see cref="AsyncLocal{T}"/>-backed ambient capture scope used to
/// correlate a planning call's v2 outcome with the session id minted after it. Same mechanics as
/// <c>InMemoryShadowDiagnosticsStore</c> (see that type's comments for the null-vs-instance
/// ambient rationale), kept separate because this store is execution authority, not observability.
/// </summary>
public sealed class InMemoryV2PlanStore : IV2PlanStore
{
    private readonly AsyncLocal<AmbientState?> _ambient = new();
    private readonly ConcurrentDictionary<PlaybackSessionId, V2PlanRecord> _records = new();

    /// <inheritdoc/>
    public IDisposable BeginCapture()
    {
        var previous = _ambient.Value;
        _ambient.Value = new AmbientState();
        return new CaptureScope(_ambient, previous);
    }

    /// <inheritdoc/>
    public void Publish(V2PlanRecord record)
    {
        var state = _ambient.Value;
        if (state is null)
        {
            // No BeginCapture scope is open on this async flow. Dropped rather than thrown for the
            // same reason as the shadow store: a lost record must never fail live playback - the
            // session simply stays legacy-authoritative, which is always a safe outcome.
            return;
        }

        state.Record = record;
    }

    /// <inheritdoc/>
    public V2PlanRecord? TakeCaptured() => _ambient.Value?.Record;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, V2PlanRecord record) => _records[id] = record;

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out V2PlanRecord? record) => _records.TryGetValue(id, out record);

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id) => _records.TryRemove(id, out _);

    private sealed class AmbientState
    {
        public V2PlanRecord? Record { get; set; }
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly AsyncLocal<AmbientState?> _ambient;
        private readonly AmbientState? _previous;
        private bool _disposed;

        public CaptureScope(AsyncLocal<AmbientState?> ambient, AmbientState? previous)
        {
            _ambient = ambient;
            _previous = previous;
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
