using System;
using System.Collections.Generic;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Retains at most one <see cref="ShadowDiagnosticRecord"/> per live <see cref="PlaybackSessionId"/>,
/// for the admin diagnostics endpoint (docs/pr92-design-playback-api-and-diagnostics.md §4.3, PR113).
/// </summary>
/// <remarks>
/// No session id exists yet when <c>ShadowPlaybackSessionPlanner</c> (removed, tesserafin#129 step 7) runs the shadow
/// comparison (<c>Plan()</c> is called before <c>PlaybackSessionManager.StoreOrReplace</c> mints
/// one), so correlation happens in two steps: the shadow run publishes its record into an ambient
/// slot via <see cref="Publish"/>, scoped by <see cref="BeginCapture()"/> around the synchronous
/// <c>Plan()</c> call; the caller then reads it back with <see cref="TakeCaptured"/> and, once the
/// real id is known, calls <see cref="Attach"/>. This works because the shadow run is entirely
/// synchronous within that scope - an <see cref="System.Threading.AsyncLocal{T}"/> write in a
/// nested synchronous call is visible to the caller after the call returns, and never leaks across
/// concurrent unrelated calls.
/// </remarks>
public interface IShadowDiagnosticsStore : IPlaybackSessionDiagnostics
{
    /// <summary>
    /// Gets the <see cref="ShadowCaptureInputs"/> the currently open ambient capture scope was
    /// opened with, or <see langword="null"/> when no scope is open or the scope carries none.
    /// </summary>
    ShadowCaptureInputs? CapturedInputs { get; }

    /// <summary>
    /// Opens a fresh ambient capture scope for one synchronous planning call, so no stale capture
    /// from an earlier call can leak into this one. PR113a: nesting is well-defined - opening a
    /// scope while another is already open on the same async flow suspends (rather than discards)
    /// the outer scope's state, which is restored exactly as it was when the inner scope's
    /// disposable is disposed.
    /// </summary>
    /// <returns>A disposable that closes the scope, restoring whatever scope (if any) enclosed it.</returns>
    IDisposable BeginCapture() => BeginCapture(null);

    /// <summary>
    /// Issue #75: opens a capture scope carrying the request-scoped facts the shadow run cannot
    /// recover on its own (see <see cref="ShadowCaptureInputs"/>), readable during the scope through
    /// <see cref="CapturedInputs"/>. Identical to <see cref="BeginCapture()"/> in every other
    /// respect - same nesting semantics, same restore-on-dispose. Deliberately an overload of the
    /// EXISTING ambient scope rather than a second channel: issue #75 forbids a new store.
    /// </summary>
    /// <param name="inputs">The request-scoped facts, or <see langword="null"/> for a caller that has none.</param>
    /// <returns>A disposable that closes the scope, restoring whatever scope (if any) enclosed it.</returns>
    IDisposable BeginCapture(ShadowCaptureInputs? inputs);

    /// <summary>
    /// Publishes a record into the currently open ambient capture scope. Called by
    /// <c>ShadowPlaybackSessionPlanner</c> (removed, tesserafin#129 step 7) only when a shadow run actually executed (behind
    /// its enabled/sampling gate) - never called otherwise, so <see cref="TakeCaptured"/> naturally
    /// returns <see langword="null"/> when shadow mode is off. PR113a: if no <see cref="BeginCapture()"/>
    /// scope is currently open on this async flow, the record is silently dropped rather than
    /// thrown or stored somewhere unscoped - a lost shadow diagnostic must never fail live playback.
    /// </summary>
    /// <param name="record">The record to publish.</param>
    void Publish(ShadowDiagnosticRecord record);

    /// <summary>
    /// Reads (without clearing) whatever was published into the currently open ambient capture
    /// scope during the call this scope wraps.
    /// </summary>
    /// <returns>The captured record, or <see langword="null"/> if nothing was published.</returns>
    ShadowDiagnosticRecord? TakeCaptured();

    /// <summary>
    /// Retains <paramref name="record"/> keyed by <paramref name="id"/>, replacing any previously
    /// retained record for that id.
    /// </summary>
    /// <param name="id">The live session this record diagnoses.</param>
    /// <param name="record">The record to retain.</param>
    void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record);

    /// <summary>
    /// Looks up the record retained for a session, if any.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <param name="record">The retained record, or <see langword="null"/> if none.</param>
    /// <returns><see langword="true"/> if a record was found.</returns>
    bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record);

    /// <summary>
    /// Gets every lifecycle event recorded for a session, in the order they were observed.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <returns>The recorded events, or an empty list if none were recorded.</returns>
    IReadOnlyList<PlaybackLifecycleEvent> GetEvents(PlaybackSessionId id);
}
