using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Retains at most one <see cref="PlaybackLiveWiringOutcome"/> per live <see cref="PlaybackSessionId"/>
/// (PR115c): whether the request was actually served from the v2 execution plan, or why it fell back
/// to legacy - for the admin diagnostics surface, mirroring <c>IShadowDiagnosticsStore</c>'s
/// lookup shape (docs/pr92-design-playback-api-and-diagnostics.md §4.3).
/// </summary>
/// <remarks>
/// Deliberately simpler than <c>IShadowDiagnosticsStore</c>/<see cref="IV2PlanStore"/>: both of
/// those need the two-step ambient-capture handshake because their producer
/// (<c>ShadowPlaybackSessionPlanner</c>) runs before the real session id is minted. The live-wiring
/// decision this store records is made by <c>MediaInfoHelper.SetDeviceSpecificData</c>, strictly
/// AFTER <c>PlaybackSessionManager.Create</c>/<c>Patch</c> has already returned the session - the id
/// is already in hand, so a plain keyed <see cref="Record"/> call is all that is needed.
/// </remarks>
public interface IPlaybackLiveWiringDiagnosticsStore
{
    /// <summary>
    /// Retains <paramref name="outcome"/> keyed by <paramref name="id"/>, replacing any previously
    /// retained outcome for that id - the same "last decision wins" discipline as
    /// <see cref="IV2PlanStore.Attach"/>.
    /// </summary>
    /// <param name="id">The live session this outcome was decided for.</param>
    /// <param name="outcome">The outcome to retain.</param>
    void Record(PlaybackSessionId id, PlaybackLiveWiringOutcome outcome);

    /// <summary>
    /// Looks up the outcome retained for a session, if any.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <param name="outcome">The retained outcome, or <see langword="null"/> if none.</param>
    /// <returns><see langword="true"/> if an outcome was found.</returns>
    bool TryGet(PlaybackSessionId id, out PlaybackLiveWiringOutcome? outcome);

    /// <summary>
    /// Evicts the outcome retained for a session, if any. A no-op if none is retained.
    /// </summary>
    /// <param name="id">The session whose outcome should be evicted.</param>
    void Remove(PlaybackSessionId id);
}
