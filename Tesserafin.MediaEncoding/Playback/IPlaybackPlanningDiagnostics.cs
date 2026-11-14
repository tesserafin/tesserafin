using System;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// A diagnostic capture for one synchronous planning attempt. It cannot provide an execution plan.
/// </summary>
public interface IPlaybackPlanningDiagnostics : IDisposable
{
    /// <summary>
    /// Correlates this capture with a successful session before disposal. An empty capture evicts
    /// previous diagnostics for that id, so a successful replan never retains stale observations.
    /// </summary>
    /// <param name="id">The session produced by this planning attempt.</param>
    void AttachTo(PlaybackSessionId id);
}
