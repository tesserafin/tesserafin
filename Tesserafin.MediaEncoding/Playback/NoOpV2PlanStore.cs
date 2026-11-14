using System;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// The default <see cref="IV2PlanStore"/> used when none is supplied - by
/// <see cref="PlaybackSessionManager"/>'s and <c>ShadowPlaybackSessionPlanner</c>'s
/// pre-PR115a constructor shapes, kept for source/binary compatibility. Retains nothing and
/// captures nothing: <see cref="TryGet"/> always reports no record, so every session stays
/// legacy-authoritative, matching pre-PR115a behavior exactly.
/// </summary>
public sealed class NoOpV2PlanStore : IV2PlanStore
{
    private NoOpV2PlanStore()
    {
    }

    /// <summary>
    /// Gets the shared singleton instance. Stateless, so a single instance is safe to reuse
    /// everywhere a default is needed.
    /// </summary>
    public static NoOpV2PlanStore Instance { get; } = new();

    /// <inheritdoc/>
    public IDisposable BeginCapture() => NullScope.Instance;

    /// <inheritdoc/>
    public void Publish(V2PlanRecord record)
    {
        // Intentionally discarded: no capture scope is ever meaningfully open.
    }

    /// <inheritdoc/>
    public V2PlanRecord? TakeCaptured() => null;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, V2PlanRecord record)
    {
        // Intentionally discarded.
    }

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out V2PlanRecord? record)
    {
        record = null;
        return false;
    }

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id)
    {
        // Nothing is ever retained.
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
