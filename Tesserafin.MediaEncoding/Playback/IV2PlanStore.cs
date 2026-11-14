using System;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Retains at most one authoritative <see cref="V2PlanRecord"/> per live
/// <see cref="PlaybackSessionId"/> (PR115a). This is the execution authority a canary session's v2
/// plan lives in - NOT <c>IShadowDiagnosticsStore</c>, which remains a pure observability
/// projection: the shadow store may be disabled, sampled out, or evicted without a live playback
/// ever noticing, so nothing on a live path may depend on it.
/// </summary>
/// <remarks>
/// Correlation uses the same two-step ambient-capture handshake as
/// <c>IShadowDiagnosticsStore</c> (see its remarks): no session id exists yet when
/// <c>ShadowPlaybackSessionPlanner</c> runs the engine, so the planner publishes into a
/// scope <see cref="PlaybackSessionManager"/> opens around its synchronous <c>Plan()</c> call, and
/// the manager attaches the captured record once the real id is known.
/// </remarks>
public interface IV2PlanStore
{
    /// <summary>
    /// Opens a fresh ambient capture scope for one synchronous planning call. Nesting restores the
    /// enclosing scope's state on dispose, same as <c>IShadowDiagnosticsStore</c>.
    /// </summary>
    /// <returns>A disposable that closes the scope, restoring whatever scope (if any) enclosed it.</returns>
    IDisposable BeginCapture();

    /// <summary>
    /// Publishes a record into the currently open ambient capture scope. Called by
    /// <c>ShadowPlaybackSessionPlanner</c> only when the effective mode made v2
    /// authoritative for this planning call - never for a pure observability (shadow) run. Silently
    /// dropped when no scope is open on this async flow.
    /// </summary>
    /// <param name="record">The record to publish.</param>
    void Publish(V2PlanRecord record);

    /// <summary>
    /// Reads (without clearing) whatever was published into the currently open ambient capture
    /// scope during the call this scope wraps.
    /// </summary>
    /// <returns>The captured record, or <see langword="null"/> if nothing was published.</returns>
    V2PlanRecord? TakeCaptured();

    /// <summary>
    /// Retains <paramref name="record"/> keyed by <paramref name="id"/>, replacing any previously
    /// retained record for that id.
    /// </summary>
    /// <param name="id">The live session this record is authoritative for.</param>
    /// <param name="record">The record to retain.</param>
    void Attach(PlaybackSessionId id, V2PlanRecord record);

    /// <summary>
    /// Looks up the record retained for a session, if any.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <param name="record">The retained record, or <see langword="null"/> if none.</param>
    /// <returns><see langword="true"/> if a record was found.</returns>
    bool TryGet(PlaybackSessionId id, out V2PlanRecord? record);

    /// <summary>
    /// Evicts the record retained for a session, if any. A no-op if none is retained.
    /// </summary>
    /// <param name="id">The session whose record should be evicted.</param>
    void Remove(PlaybackSessionId id);
}
