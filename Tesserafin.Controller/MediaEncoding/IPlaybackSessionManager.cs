using System.Collections.Generic;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Tracks the lifecycle of planned playback sessions (create/patch/delete), on top of
/// <see cref="IPlaybackSessionPlanner"/> decisions. This is the session bookkeeping half
/// of the point-1 protocol, kept separate from the pure planning decision so each can be
/// verified independently. Sessions tied to a play session id are ended automatically when
/// the corresponding transcoding job ends (via <see cref="ITranscodeManager.TranscodingJobEnded"/>)
/// or playback stops (direct play has no transcoding job to signal this). A time-to-live sweep
/// is the backstop for sessions that never receive either signal (e.g. a PlaybackInfo probe with
/// no playback that follows).
/// </summary>
public interface IPlaybackSessionManager
{
    /// <summary>
    /// Plans and stores a new session.
    /// </summary>
    /// <param name="request">The request to plan.</param>
    /// <param name="playSessionId">
    /// The client-facing play session id, when known. At most one session is kept per play
    /// session id: creating with the same id again replaces that session's plan and request,
    /// and the session is removed automatically when the transcoding job with that id ends.
    /// </param>
    /// <param name="playbackAttemptId">
    /// Issue #43. The opaque, client-supplied identifier of the playback ATTEMPT this call belongs
    /// to, or <c>null</c> when the client sent none. Recorded on the resulting session for
    /// diagnostics only — never used to look a session up, never an authorization input. Stable
    /// across the several requests (and retries) of one attempt, unlike the per-request
    /// <c>RequestId</c> of issue #42 and unlike <paramref name="playSessionId"/>, which can outlive
    /// several attempts.
    /// </param>
    /// <returns>The created (or updated) session, or <c>null</c> if no viable plan exists.</returns>
    PlaybackSession? Create(PlaybackSessionRequest request, string? playSessionId = null, string? playbackAttemptId = null);

    /// <summary>
    /// Re-plans an existing session with a new request, replacing its stored plan.
    /// </summary>
    /// <param name="id">The session to patch.</param>
    /// <param name="request">The new request to plan.</param>
    /// <param name="playbackAttemptId">
    /// Issue #43. The attempt id the client resent with this re-plan. A <c>PUT</c> mid-attempt
    /// carries the SAME value the original <c>POST</c> did, which is exactly what makes the two
    /// requests joinable; a new attempt carries a new value. <c>null</c> leaves whatever the
    /// session already recorded untouched, so a client that omits the field on a re-plan does not
    /// silently erase the correlation established at creation.
    /// </param>
    /// <returns>The updated session, or <c>null</c> if the session does not exist or no viable plan exists.</returns>
    PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request, string? playbackAttemptId = null);

    /// <summary>
    /// Records a session whose plan was already decided elsewhere — e.g. a controller that
    /// received an already-decided play method and codecs from the client — without invoking
    /// <see cref="IPlaybackSessionPlanner"/>. Use <see cref="Create"/> instead when the plan
    /// should be derived from <see cref="Tesserafin.Model.Dlna.MediaOptions"/>.
    /// </summary>
    /// <param name="kind">Whether this is an audio or video session.</param>
    /// <param name="plan">The already-decided plan to record.</param>
    /// <param name="playSessionId">
    /// The client-supplied play session id, when known. At most one session is kept per play
    /// session id: tracking the same id again replaces that session's plan, and the session is
    /// removed automatically when the transcoding job with that id ends.
    /// </param>
    /// <param name="playbackAttemptId">
    /// Issue #43. The opaque, client-supplied identifier of the playback ATTEMPT this call belongs
    /// to, or <c>null</c> when the client sent none. Recorded on the resulting session for
    /// diagnostics only — never used to look a session up, never an authorization input. Stable
    /// across the several requests (and retries) of one attempt, unlike the per-request
    /// <c>RequestId</c> of issue #42 and unlike <paramref name="playSessionId"/>, which can outlive
    /// several attempts.
    /// </param>
    /// <returns>The created (or updated) session.</returns>
    PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan, string? playSessionId = null, string? playbackAttemptId = null);

    /// <summary>
    /// Removes a session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <returns><c>true</c> if a session was removed; <c>false</c> if it did not exist.</returns>
    bool Delete(PlaybackSessionId id);

    /// <summary>
    /// Removes the session tied to the given play session id, if any.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <returns><c>true</c> if a session was removed; <c>false</c> if none was tied to that id.</returns>
    bool DeleteByPlaySessionId(string playSessionId);

    /// <summary>
    /// Gets a session by id, for diagnostics.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <returns>The session, or <c>null</c> if it does not exist.</returns>
    PlaybackSession? Get(PlaybackSessionId id);

    /// <summary>
    /// Gets a snapshot of all currently tracked sessions, for diagnostics.
    /// </summary>
    /// <returns>The current sessions.</returns>
    IReadOnlyList<PlaybackSession> GetAll();
}
