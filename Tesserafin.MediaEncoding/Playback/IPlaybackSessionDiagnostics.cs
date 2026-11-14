using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Observes session planning and lifecycle without exposing comparison results to execution.
/// </summary>
public interface IPlaybackSessionDiagnostics
{
    /// <summary>
    /// Opens an isolated synchronous planning capture, inheriting enclosing request metadata.
    /// Disposing a capture without attaching it discards it and restores the enclosing scope.
    /// </summary>
    /// <returns>The capture owned by this planning attempt.</returns>
    IPlaybackPlanningDiagnostics BeginPlanning();

    /// <summary>
    /// Removes all observations retained for a session.
    /// </summary>
    /// <param name="id">The removed session.</param>
    void Remove(PlaybackSessionId id);

    /// <summary>
    /// Records an observed lifecycle event, independently of whether planning produced diagnostics.
    /// </summary>
    /// <param name="id">The observed session.</param>
    /// <param name="lifecycleEvent">The event and its observed timestamp.</param>
    void RecordEvent(PlaybackSessionId id, PlaybackLifecycleEvent lifecycleEvent);
}
