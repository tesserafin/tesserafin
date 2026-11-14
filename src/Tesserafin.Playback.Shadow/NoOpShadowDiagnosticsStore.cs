using System;
using System.Collections.Generic;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Discards optional shadow observations when no retention store is configured.
/// </summary>
public sealed class NoOpShadowDiagnosticsStore : IShadowDiagnosticsStore
{
    private NoOpShadowDiagnosticsStore()
    {
    }

    /// <summary>
    /// Gets the shared singleton instance. Stateless, so a single instance is safe to reuse
    /// everywhere a default is needed.
    /// </summary>
    public static NoOpShadowDiagnosticsStore Instance { get; } = new();

    /// <inheritdoc/>
    public ShadowCaptureInputs? CapturedInputs => null;

    /// <inheritdoc/>
    public IDisposable BeginCapture() => NullScope.Instance;

    /// <inheritdoc/>
    public IDisposable BeginCapture(ShadowCaptureInputs? inputs) => NullScope.Instance;

    /// <inheritdoc/>
    public IPlaybackPlanningDiagnostics BeginPlanning() => NullScope.Instance;

    /// <inheritdoc/>
    public void Publish(ShadowDiagnosticRecord record)
    {
        // Intentionally discarded: no capture scope is ever meaningfully open.
    }

    /// <inheritdoc/>
    public ShadowDiagnosticRecord? TakeCaptured() => null;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record)
    {
        // Intentionally discarded.
    }

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record)
    {
        record = null;
        return false;
    }

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id)
    {
        // Nothing is ever retained.
    }

    /// <inheritdoc/>
    public void RecordEvent(PlaybackSessionId id, PlaybackLifecycleEvent lifecycleEvent)
    {
        // Intentionally discarded: no capture is ever meaningfully retained.
    }

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackLifecycleEvent> GetEvents(PlaybackSessionId id) => Array.Empty<PlaybackLifecycleEvent>();

    private sealed class NullScope : IPlaybackPlanningDiagnostics
    {
        public static NullScope Instance { get; } = new();

        public void AttachTo(PlaybackSessionId id)
        {
        }

        public void Dispose()
        {
        }
    }
}
