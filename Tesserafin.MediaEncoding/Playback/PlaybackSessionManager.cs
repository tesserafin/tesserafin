using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.Diagnostics;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Extensions;
using Tesserafin.Model.Session;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Issue #71: why a <see cref="PlaybackSession"/> was removed. Threaded down to
/// <c>PlaybackSessionManager.RemoveNoLock</c> - the single removal funnel - from each of its
/// callers, so the one structured line that funnel emits says which actor reaped the session.
/// The whole #71 investigation turns on telling <see cref="TranscodingJobEnded"/> (the ffmpeg job
/// ending, which is NOT playback ending) apart from <see cref="HttpDelete"/> (the client's own
/// explicit teardown) and <see cref="TtlSweep"/> (the 6 h backstop).
/// </summary>
internal enum PlaybackSessionRemovalReason
{
    /// <summary>An explicit <c>DELETE /Playback/Sessions/{id}</c> from the client.</summary>
    HttpDelete,

    /// <summary><see cref="ITranscodeManager.TranscodingJobEnded"/> - the ffmpeg job ended.</summary>
    TranscodingJobEnded,

    /// <summary><see cref="ISessionManager.PlaybackStopped"/> - the legacy playback-stop report.</summary>
    PlaybackStopped,

    /// <summary>The <see cref="PlaybackSessionManager.SweepExpired"/> TTL backstop.</summary>
    TtlSweep,

    /// <summary><see cref="IDeviceLoggedOutSource.DeviceLoggedOut"/> - the owning device's token was revoked (#413).</summary>
    DeviceLoggedOut,
}

/// <inheritdoc cref="IPlaybackSessionManager"/>
public sealed class PlaybackSessionManager : IPlaybackSessionManager, IDisposable
{
    // Direct-play sessions have no transcoding job to signal their end, and a client can call
    // PlaybackInfo without ever starting playback. PlaybackStopped (see constructor) covers the
    // former in the common case; this TTL is the backstop for both when that signal never
    // arrives (crash, client bug, browser closed mid-probe). Generous on purpose: legitimate
    // playback can run for hours without any Create/Patch/Track call to refresh UpdatedAt.
    private static readonly TimeSpan ExpiryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    private readonly PlaybackSessionLiveStreams? _liveStreams;
    private readonly IPlaybackSessionPlanner _planner;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ISessionManager _sessionManager;
    private readonly IDeviceLoggedOutSource? _deviceLogouts;
    private readonly IPlaybackSessionDiagnostics? _diagnosticsStore;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;
    private readonly PlaybackOperationalMetrics _operationalMetrics;
    private readonly IRequestCorrelationAccessor _requestCorrelation;
    private readonly ILogger<PlaybackSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly Dictionary<PlaybackSessionId, PlaybackSession> _sessions = new();
    private readonly Dictionary<string, PlaybackSessionId> _byPlaySessionId = new(StringComparer.OrdinalIgnoreCase);

    // PR115d: play session ids for which ITranscodeManager.TranscodingJobStarted has actually fired -
    // consulted (and removed) by RecordTranscodeStartFailureIfNeverStarted to distinguish "ffmpeg
    // started, then the job ended" (already recorded as a success at Started time, nothing further to
    // do) from "ffmpeg never started" (a failure, recorded here). Guarded by _lock, same as
    // _byPlaySessionId - both are read/written from the same event handlers.
    private readonly HashSet<string> _startedPlaySessionIds = new(StringComparer.OrdinalIgnoreCase);

    // Issue #71: play session ids for which a transcode-start outcome has ALREADY been recorded into
    // _operationalMetrics. Before #71 this set was unnecessary: OnTranscodingJobEnded always evicted
    // the session (and with it _byPlaySessionId[playSessionId]) immediately after recording, so
    // neither a re-fired Ended nor a post-seek second job could ever correlate and record twice.
    // Client-owned sessions now survive that signal, so the "at most one start outcome per session"
    // invariant PlaybackStopThresholdGuard reads has to be stated explicitly instead of falling out
    // of the eviction. Guarded by _lock; cleared in RemoveNoLock alongside _startedPlaySessionIds.
    private readonly HashSet<string> _startOutcomeRecordedPlaySessionIds = new(StringComparer.OrdinalIgnoreCase);

    // Issue #71: sessions a CLIENT established through the v2 HTTP API (Create, i.e.
    // POST /Playback/Sessions) and therefore holds the PlaybackSessionId of. They are ended by that
    // client's own DELETE, or by the ExpiryTtl backstop - never by a PlaySessionId-keyed signal out
    // of the legacy transcode pipeline, which is the coupling issue #71 identifies as the root
    // cause (shared with #70). Sessions merely Track()ed by that pipeline - the HLS segment path,
    // DynamicHlsController -> TrackTranscodeOutput - are NOT in here: nobody holds their id and
    // nobody will ever DELETE them, so their job ending remains the right moment to drop them.
    // Guarded by _lock, same as _sessions and _byPlaySessionId; cleared in RemoveNoLock.
    private readonly HashSet<PlaybackSessionId> _clientOwnedSessions = new();

    private readonly Timer _sweepTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionManager"/> class.
    /// </summary>
    /// <param name="planner">Instance of the <see cref="IPlaybackSessionPlanner"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="diagnosticsStore">
    /// Optional observation sink. Captures planning diagnostics and lifecycle events without
    /// exposing comparison records to the execution path. When absent, no observations are retained.
    /// </param>
    /// <param name="v2PlanStore">
    /// PR115a: retains the AUTHORITATIVE v2 plan (<see cref="V2PlanRecord"/>) <paramref name="planner"/>
    /// publishes when a <see cref="Create"/>/<see cref="Patch"/> call is served by v2, keyed by the
    /// resulting session id - a separate channel from <paramref name="diagnosticsStore"/>, which only
    /// ever holds observability data. Defaults to a no-op instance when not supplied, so every
    /// pre-PR115a call site - including existing test constructors - keeps compiling and behaving
    /// exactly as before: no authoritative record is ever retained.
    /// </param>
    /// <param name="liveWiringDiagnosticsStore">
    /// PR115c: retains the observable outcome (<see cref="PlaybackLiveWiringOutcome"/>) of the live
    /// streaming path's serve-v2-or-fallback decision - a third, separate channel from both
    /// <paramref name="diagnosticsStore"/> and <paramref name="v2PlanStore"/>. This manager never
    /// writes to it (the decision is made downstream, in <c>MediaInfoHelper</c>, after a session is
    /// already in hand) - it only evicts it, on the exact same removal paths as the other two
    /// per-session stores, so a stale outcome can never outlive the session it describes. Defaults
    /// to a no-op instance when not supplied, so every pre-PR115c call site - including existing test
    /// constructors - keeps compiling and behaving exactly as before: nothing is ever retained.
    /// </param>
    /// <param name="operationalMetrics">
    /// PR115d: where <see cref="OnTranscodingJobStarted"/>/<see cref="OnTranscodingJobEnded"/> record
    /// the ffmpeg transcode-start outcome for v2-served sessions - see
    /// <see cref="PlaybackOperationalMetrics"/>'s remarks for exactly what is recorded and why.
    /// Defaults to a fresh, unshared instance when not supplied, so every pre-PR115d call site -
    /// including existing test constructors - keeps compiling and behaving exactly as before:
    /// outcomes are recorded but never observed by anything outside this instance.
    /// </param>
    /// <param name="requestCorrelation">
    /// Issue #42: supplies the correlation id of the HTTP request in flight, stamped onto each
    /// <see cref="PlaybackLifecycleEvent"/> this manager records. Additive to the
    /// <see cref="PlaybackSessionId"/> grouping key, never a replacement for it. Defaults to
    /// <see cref="NullRequestCorrelationAccessor"/> when not supplied, so every pre-#42 call site —
    /// including existing test constructors — keeps compiling and simply records no request id.
    /// </param>
    /// <param name="logger">
    /// Issue #71: the sink for this class's removal/replacement lifecycle lines - most importantly
    /// the single line <see cref="RemoveNoLock"/> emits for EVERY session removal, carrying the
    /// <see cref="PlaybackSessionRemovalReason"/> that says which actor reaped it. PR #69
    /// instrumented the four HTTP edges but nothing on the removal side, which is precisely where a
    /// session disappears. Defaults to <see cref="NullLogger{T}"/> when not supplied, the same
    /// optional-dependency discipline as every other parameter above, so existing call sites -
    /// including test constructors - keep compiling and simply log nothing.
    /// </param>
    /// <param name="timeProvider">
    /// Issue #71: the clock behind <c>CreatedAt</c>/<c>UpdatedAt</c> (<see cref="StoreOrReplace"/>,
    /// <see cref="Patch"/>) and the removal line's age. Injected purely so lifetime tests can drive
    /// creation and re-plan to distinct instants and then call the already time-injected
    /// <see cref="SweepExpired"/> at a chosen point, instead of sleeping. Defaults to
    /// <see cref="TimeProvider.System"/>; production behaviour is unchanged.
    /// </param>
    /// <param name="liveStreams">
    /// The Live TV tuner streams sessions opened; every removal path releases the removed session's
    /// one. Optional: without it no session holds a live stream.
    /// </param>
    public PlaybackSessionManager(
        IPlaybackSessionPlanner planner,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager,
        IPlaybackSessionDiagnostics? diagnosticsStore = null,
        IV2PlanStore? v2PlanStore = null,
        IPlaybackLiveWiringDiagnosticsStore? liveWiringDiagnosticsStore = null,
        PlaybackOperationalMetrics? operationalMetrics = null,
        IRequestCorrelationAccessor? requestCorrelation = null,
        ILogger<PlaybackSessionManager>? logger = null,
        TimeProvider? timeProvider = null,
        PlaybackSessionLiveStreams? liveStreams = null)
    {
        _liveStreams = liveStreams;
        _logger = logger ?? NullLogger<PlaybackSessionManager>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestCorrelation = requestCorrelation ?? NullRequestCorrelationAccessor.Instance;
        _planner = planner;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        _diagnosticsStore = diagnosticsStore;
        _v2PlanStore = v2PlanStore ?? NoOpV2PlanStore.Instance;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore ?? NoOpPlaybackLiveWiringDiagnosticsStore.Instance;
        _operationalMetrics = operationalMetrics ?? new PlaybackOperationalMetrics();
        transcodeManager.TranscodingJobEnded += OnTranscodingJobEnded;
        transcodeManager.TranscodingJobStarted += OnTranscodingJobStarted;
        sessionManager.PlaybackStart += OnPlaybackStart;
        sessionManager.PlaybackStopped += OnPlaybackStopped;
        // #413: the session manager is also the source of device revocations (a separate interface,
        // so ISessionManager's published surface does not change).
        _deviceLogouts = sessionManager as IDeviceLoggedOutSource;
        if (_deviceLogouts is not null)
        {
            _deviceLogouts.DeviceLoggedOut += OnDeviceLoggedOut;
        }

        _sweepTimer = new Timer(_ => SweepExpired(_timeProvider.GetUtcNow()), null, SweepInterval, SweepInterval);
    }

    /// <inheritdoc/>
    public PlaybackSession? Create(PlaybackSessionRequest request, string? playSessionId = null, string? playbackAttemptId = null)
    {
        PlaybackPlan? plan;
        V2PlanRecord? v2Captured;
        // Diagnostics own capture and correlation; execution never reads comparison records.
        using var diagnostics = _diagnosticsStore?.BeginPlanning();
        using (_v2PlanStore.BeginCapture())
        {
            plan = Plan(request);
            v2Captured = _v2PlanStore.TakeCaptured();
        }

        if (plan is null)
        {
            return null;
        }

        var session = StoreOrReplace(request.Kind, playSessionId, request, plan, playbackAttemptId);

        // Issue #71: this is the ONLY entry point a client reaches with an id it keeps. Marking it
        // here (rather than inferring "Request is not null" downstream) states the ownership
        // explicitly and is what exempts the session from the legacy PlaySessionId-keyed reaping in
        // OnTranscodingJobEnded/OnPlaybackStopped. A Create landing on a session the legacy pipeline
        // had already Track()ed promotes it, which is correct - from this point a client holds its
        // id and owes it a DELETE.
        lock (_lock)
        {
            _clientOwnedSessions.Add(session.Id);
        }

        // PR113a: attach-or-remove unconditionally, not just attach-if-captured. StoreOrReplace
        // reuses the existing session id when playSessionId matches an in-flight session, so a
        // successful replan with no new capture (shadow disabled/not sampled this call) must evict
        // whatever record was retained for a *previous* capture on that same id - otherwise the
        // admin diagnostics endpoint would show the new plan next to a stale context/capabilities/
        // reasoning from an earlier call. Remove is a documented no-op when nothing is retained, so
        // this is safe (if slightly redundant) for the brand-new-session case too.
        diagnostics?.AttachTo(session.Id);

        // PR115a: same attach-or-remove discipline for the authoritative v2 record. A replan that is
        // no longer authoritative (mode changed, cohort resized) must evict the stale authoritative
        // record, or the session would keep being served a v2 plan its current configuration no
        // longer grants.
        if (v2Captured is not null)
        {
            _v2PlanStore.Attach(session.Id, v2Captured);
        }
        else
        {
            _v2PlanStore.Remove(session.Id);
        }

        return session;
    }

    /// <inheritdoc/>
    public PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request, string? playbackAttemptId = null)
    {
        PlaybackPlan? plan;
        V2PlanRecord? v2Captured;
        // Diagnostics own capture and correlation; execution never reads comparison records.
        using var diagnostics = _diagnosticsStore?.BeginPlanning();
        using (_v2PlanStore.BeginCapture())
        {
            plan = Plan(request);
            v2Captured = _v2PlanStore.TakeCaptured();
        }

        if (plan is null)
        {
            return null;
        }

        // Issue #70, the PUT atomicity guard. The early return above only fires when the LEGACY
        // planner refuses, and it essentially never does on a re-plan: StreamBuilder DEMOTES a
        // request its constraints forbid rather than rejecting it, so `plan` is viable. The v2
        // engine can still refuse (PlaybackExecutionPlanBuilder: NotViable, NoStreamsSelected,
        // MissingOutputContainer), and ShadowPlaybackSessionPlanner publishes that refusal anyway,
        // as a record with a null ExecutionPlan - "v2 was authoritative here and produced nothing
        // executable". A record is only ever published when the effective mode is AUTHORITATIVE
        // (see RunShadow's `if (prepared.Authoritative)`), so this condition IS "authoritative and
        // unexecutable"; a Shadow-mode refusal captures nothing and is unaffected.
        //
        // Attaching that record would destroy the good, executable one the previous Create/Patch
        // left behind, and the session would answer 200 while every subsequent GET Stream fell back
        // to legacy with PlanNotExecutable. Refusing here (the controller maps a null Patch to 422)
        // is the only outcome that keeps the operation ALL-OR-NOTHING: this returns before the
        // _sessions write and before either store's attach-or-remove, and IV2PlanStore.Publish only
        // ever writes into the ambient capture slot - never into the store itself - so nothing at
        // all has been mutated at this point.
        if (v2Captured is { ExecutionPlan: null })
        {
            // #203: id.Value.ToString("N") is PlaybackSessionId.ToString()'s own body projected at the
            // logging boundary - the same 32 hex characters, visible to the analysis.
            _logger.LogInformation(
                "Playback session {SessionId} re-plan refused (authoritative v2 decision {DecisionMethod} produced no executable plan, viable {IsViable}, engine {EngineVersion}) - previous session, plan and v2 record left intact.",
                id.Value.ToString("N"),
                v2Captured.Decision.Method,
                v2Captured.Decision.IsViable,
                v2Captured.Decision.EngineVersion);
            return null;
        }

        PlaybackSession updated;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var existing))
            {
                return null;
            }

            // Issue #43: a null attempt id on a re-plan means "the client did not resend it",
            // NOT "forget the attempt". Erasing a correlation established at creation because a
            // later request of the SAME attempt omitted the field would defeat the whole point.
            updated = existing with
            {
                Kind = request.Kind,
                Request = request,
                Plan = plan,
                UpdatedAt = _timeProvider.GetUtcNow(),
                PlaybackAttemptId = playbackAttemptId ?? existing.PlaybackAttemptId,
            };
            _sessions[id] = updated;
        }

        // PR113a: attach-or-remove unconditionally (see the matching comment in Create) - a
        // successful Patch with no new capture must evict whatever record was retained from an
        // earlier Create/Patch on this id, not leave it attached to the freshly replanned session.
        diagnostics?.AttachTo(updated.Id);

        // PR115a: same attach-or-remove discipline for the authoritative v2 record. A replan that is
        // no longer authoritative (mode changed, cohort resized) must evict the stale authoritative
        // record, or the session would keep being served a v2 plan its current configuration no
        // longer grants.
        if (v2Captured is not null)
        {
            _v2PlanStore.Attach(updated.Id, v2Captured);
        }
        else
        {
            _v2PlanStore.Remove(updated.Id);
        }

        return updated;
    }

    /// <inheritdoc/>
    public PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan, string? playSessionId = null, string? playbackAttemptId = null)
        => StoreOrReplace(kind, playSessionId, null, plan, playbackAttemptId);

    /// <inheritdoc/>
    public PlaybackSession TrackTranscodeOutput(string outputVideoCodec, string outputAudioCodec, TranscodeReason transcodeReasons, string? playSessionId = null)
    {
        var playMethod = EncodingHelper.IsCopyCodec(outputVideoCodec) && EncodingHelper.IsCopyCodec(outputAudioCodec)
            ? PlayMethod.DirectStream
            : PlayMethod.Transcode;
        return Track(PlaybackMediaKind.Video, new PlaybackPlan(playMethod, transcodeReasons), playSessionId);
    }

    /// <inheritdoc/>
    public bool Delete(PlaybackSessionId id)
    {
        lock (_lock)
        {
            return RemoveNoLock(id, PlaybackSessionRemovalReason.HttpDelete);
        }
    }

    /// <inheritdoc/>
    public bool DeleteByPlaySessionId(string playSessionId)
    {
        lock (_lock)
        {
            return _byPlaySessionId.TryGetValue(playSessionId, out var id)
                && RemoveNoLock(id, PlaybackSessionRemovalReason.HttpDelete);
        }
    }

    /// <inheritdoc/>
    public PlaybackSession? Get(PlaybackSessionId id)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(id, out var session) ? session : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackSession> GetAll()
    {
        lock (_lock)
        {
            return _sessions.Values.ToList();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transcodeManager.TranscodingJobEnded -= OnTranscodingJobEnded;
        _transcodeManager.TranscodingJobStarted -= OnTranscodingJobStarted;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        if (_deviceLogouts is not null)
        {
            _deviceLogouts.DeviceLoggedOut -= OnDeviceLoggedOut;
        }

        _sweepTimer.Dispose();
    }

    /// <summary>
    /// Removes sessions whose plan has not been refreshed within <see cref="ExpiryTtl"/>. Safety
    /// net for sessions that never receive an <see cref="ITranscodeManager.TranscodingJobEnded"/>
    /// or <see cref="ISessionManager.PlaybackStopped"/> signal. Internal (not private) so tests
    /// can drive it directly instead of depending on the timer's real-time interval.
    /// </summary>
    /// <param name="now">The current time, injected so tests can simulate expiry.</param>
    /// <returns>The number of sessions removed.</returns>
    internal int SweepExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = _sessions.Values.Where(s => now - s.UpdatedAt > ExpiryTtl).ToList();
            foreach (var session in expired)
            {
                // Issue #71: the TTL reap is logged with the two numbers that make it falsifiable -
                // the UpdatedAt it judged and the instant that stamp expired - so a TTL reap can
                // never be mistaken for an event-driven one in an incident timeline.
                _logger.LogInformation(
                    "Playback session {SessionId} expired (play session {PlaySessionId}, updated {UpdatedAt}, expired at {ExpiresAt}, sweep of {ExpiredCount}).",
                    session.Id,
                    session.PlaySessionId,
                    session.UpdatedAt,
                    session.UpdatedAt + ExpiryTtl,
                    expired.Count);
                RemoveNoLock(session.Id, PlaybackSessionRemovalReason.TtlSweep);
            }

            return expired.Count;
        }
    }

    /// <summary>
    /// Issue #71: the LEGACY-signal reap, used by <see cref="OnTranscodingJobEnded"/> and
    /// <see cref="OnPlaybackStopped"/> - and the fix for the defect. Both signals are keyed on
    /// <c>PlaySessionId</c>, which is the legacy transcode pipeline's job key, not the v2 session's
    /// identity; treating either as "this playback session is over" is what let an ffmpeg process
    /// exiting destroy a session the user was still watching. So a session a client established and
    /// still holds the id of (<see cref="_clientOwnedSessions"/>) is left alone here: it ends on
    /// that client's own <c>DELETE</c>, with <see cref="SweepExpired"/>'s TTL as the backstop for
    /// the client that never sends one. Sessions the legacy pipeline tracked by itself are reaped
    /// exactly as before.
    /// </summary>
    /// <param name="playSessionId">The legacy play session id the signal carried.</param>
    /// <param name="reason">Which signal is asking.</param>
    /// <returns><c>true</c> when a session was removed.</returns>
    private bool ReapByPlaySessionId(string playSessionId, PlaybackSessionRemovalReason reason)
    {
        lock (_lock)
        {
            if (!_byPlaySessionId.TryGetValue(playSessionId, out var id))
            {
                return false;
            }

            if (_clientOwnedSessions.Contains(id))
            {
                _logger.LogInformation(
                    "Playback session {SessionId} retained across {RemovalReason} (play session {PlaySessionId}) - client-owned sessions end on an explicit DELETE or the TTL backstop.",
                    id,
                    reason,
                    playSessionId);
                return false;
            }

            return RemoveNoLock(id, reason);
        }
    }

    /// <summary>
    /// #301: a play session id is the client's to choose, so a second user naming it plans a session
    /// of its own rather than taking over the first user's.
    /// </summary>
    private static bool BelongToDifferentUsers(PlaybackSessionRequest? existing, PlaybackSessionRequest? incoming)
        => existing is not null && incoming is not null && !existing.Options.UserId.Equals(incoming.Options.UserId);

    private PlaybackSession StoreOrReplace(PlaybackMediaKind kind, string? playSessionId, PlaybackSessionRequest? request, PlaybackPlan plan, string? playbackAttemptId = null)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(playSessionId)
                && _byPlaySessionId.TryGetValue(playSessionId, out var existingId)
                && _sessions.TryGetValue(existingId, out var existing)
                && !BelongToDifferentUsers(existing.Request, request))
            {
                // Issue #43: same "null does not erase" rule as Patch - and it now actually applies
                // to Request too. `Request = request` was unconditional here, which this comment
                // already claimed it was not: Track (the HLS segment path, DynamicHlsController ->
                // Track) ALWAYS passes request: null, so any segment fetch landing on the session a
                // previous Create established for the same play session id silently nulled the
                // stored request.
                //
                // That null is load-bearing downstream, not cosmetic. PlaybackSessionsController
                // reads ownership off session.Request?.Options.UserId
                // (EnsureCallerOwnsSessionOrIsAdmin) and forbids when it is null, and
                // GetPlaybackSessionStream 422s on the same null. So the ordinary client sequence -
                // POST Playback/Sessions, then fetch a segment - locked the session's OWN OWNER out
                // of it: PUT and DELETE answered 403 and GET Stream 422, while an administrator was
                // unaffected (hence the endpoint reading as "admin only"). Issue #43's client-side
                // teardown is impossible while that holds, DELETE being precisely the verb refused.
                //
                // Issue #70: Kind and Plan used to keep overwriting unconditionally, on the reasoning
                // that Track's whole purpose is to record the plan actually being executed. That
                // holds for a session the legacy pipeline established by itself - and ONLY there.
                // TrackTranscodeOutput builds `new PlaybackPlan(playMethod, transcodeReasons)`, whose
                // StreamInfo defaults to null (IPlaybackSessionPlanner), so for a session a CLIENT
                // planned through POST Playback/Sessions the same overwrite REPLACED a fully planned
                // v2 plan with a stub carrying no StreamInfo - and GET Stream reads exactly that
                // (PlaybackSessionsController: legacyStreamInfo = session.Plan.StreamInfo,
                // mediaSource = legacyStreamInfo?.MediaSource) and 422s when either is null. So the
                // ordinary client sequence - POST, then fetch a segment - left the session alive but
                // UNSERVABLE. Before issue #71 the segment path's job ending evicted the session
                // outright, which is why nobody ever observed the degraded state.
                //
                // The exemption is scoped exactly as issue #71 scoped its Request guard, and for the
                // same reason - it must not become a blanket "never overwrite":
                //   - `request is null` : only a caller with no request of its own (Track, the HLS
                //     segment path) is deferring here. A re-POST re-entering this branch on a live
                //     play session id carries a freshly planned plan and must still install it.
                //   - client-owned      : a session the legacy pipeline tracked by itself has no
                //     v2 plan to protect and nobody holding its id; recording the executing plan
                //     really is the right thing there, so that path is untouched.
                var preservePlannedDecision = request is null && _clientOwnedSessions.Contains(existingId);

                // UpdatedAt is deliberately still refreshed on this path even when the planned
                // decision is preserved: it is the only stamp SweepExpired judges, so freezing it
                // would hand issue #71's TTL backstop a session the user is actively streaming.
                var updated = existing with
                {
                    Kind = preservePlannedDecision ? existing.Kind : kind,
                    Request = request ?? existing.Request,
                    Plan = preservePlannedDecision ? existing.Plan : plan,
                    UpdatedAt = now,
                    PlaybackAttemptId = playbackAttemptId ?? existing.PlaybackAttemptId,
                };
                _sessions[existingId] = updated;

                // Issue #71: the in-place replacement is the one lifecycle transition that mutates a
                // live session without removing it - #70's plan-overwrite vector. Old and new state
                // side by side, plus whether the CALLER brought a request at all, so a v2 plan
                // silently rewritten by a legacy segment fetch (Track passes request: null) is
                // visible. IncomingRequestWasNull is literally `request is null` - it names the
                // caller's input, not an outcome; PlanPreserved next to it is the outcome, and the
                // two differ precisely on a legacy-tracked session (true / false).
                _logger.LogInformation(
                    "Playback session {SessionId} replaced in place (play session {PlaySessionId}, method {OldPlayMethod} -> {NewPlayMethod}, kind {OldKind} -> {NewKind}, incoming request null {IncomingRequestWasNull}, plan preserved {PlanPreserved}, stream info {HasStreamInfo}, attempt {PlaybackAttemptId}).",
                    existingId,
                    playSessionId.ToSingleLogLine(),
                    existing.Plan.PlayMethod,
                    updated.Plan.PlayMethod,
                    existing.Kind,
                    updated.Kind,
                    request is null,
                    // Issue #70: the bit that says whether this in-place replacement degraded a
                    // client's planned decision or faithfully recorded a legacy one, plus whether
                    // what the session ended up holding is servable at all - GET Stream 422s
                    // without a StreamInfo, so a false/false pair here IS the unservable state.
                    preservePlannedDecision,
                    updated.Plan.StreamInfo is not null,
                    updated.PlaybackAttemptId.ToSingleLogLine());

                return updated;
            }

            var session = new PlaybackSession(PlaybackSessionId.NewId(), kind, playSessionId, request, plan, now, now, playbackAttemptId);
            _sessions[session.Id] = session;
            if (!string.IsNullOrEmpty(playSessionId))
            {
                _byPlaySessionId[playSessionId] = session.Id;
            }

            return session;
        }
    }

    /// <summary>
    /// The single funnel every removal path goes through. Issue #71: it is therefore the only place
    /// that can emit one line per removal, and the <paramref name="reason"/> its callers thread down
    /// is the whole point - a session that disappears mid-playback is diagnosable only if the line
    /// says WHICH actor removed it.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <param name="reason">Which actor is removing it.</param>
    /// <returns><c>true</c> when a session was actually removed.</returns>
    private bool RemoveNoLock(PlaybackSessionId id, PlaybackSessionRemovalReason reason)
    {
        if (!_sessions.Remove(id, out var session))
        {
            return false;
        }

        _clientOwnedSessions.Remove(id);

        // Issue #71: THE line. PlaybackAttemptId is what joins this teardown to the created/replaced
        // lines PR #69 emits on the HTTP edges; RemovalReason is what separates "the client asked"
        // from "an ffmpeg job ended while the user was still watching".
        // #203: id.Value.ToString("N") is PlaybackSessionId.ToString()'s own body projected at the
        // logging boundary - the same 32 hex characters, visible to the analysis.
        _logger.LogInformation(
            "Playback session {SessionId} removed (play session {PlaySessionId}, attempt {PlaybackAttemptId}, reason {RemovalReason}, created {CreatedAt}, updated {UpdatedAt}, age {AgeSeconds}s).",
            id.Value.ToString("N"),
            session.PlaySessionId.ToSingleLogLine(),
            session.PlaybackAttemptId.ToSingleLogLine(),
            reason,
            session.CreatedAt,
            session.UpdatedAt,
            (_timeProvider.GetUtcNow() - session.CreatedAt).TotalSeconds);

        // #301: another user's session can hold this id by now; its lifecycle state stays its own.
        if (!string.IsNullOrEmpty(session.PlaySessionId)
            && _byPlaySessionId.TryGetValue(session.PlaySessionId, out var mapped)
            && mapped.Equals(id))
        {
            _byPlaySessionId.Remove(session.PlaySessionId);

            // PR115d: belt-and-suspenders against a leaked entry - the normal path already removes
            // this in RecordTranscodeStartFailureIfNeverStarted's Ended handling, but a session can
            // be evicted by a DIFFERENT path first (PlaybackStopped arriving before the transcoding
            // job's Ended event, SweepExpired's TTL backstop, an explicit Delete) while a Started was
            // already recorded and no Ended ever follows to clean it up otherwise.
            _startedPlaySessionIds.Remove(session.PlaySessionId);

            // Issue #71: same lifetime as the set above - the next session to reuse this play
            // session id gets a fresh start-outcome budget.
            _startOutcomeRecordedPlaySessionIds.Remove(session.PlaySessionId);
        }

        // PR113: evicts whatever shadow diagnostic was retained for this session, if any - covers
        // every removal path (Delete, DeleteByPlaySessionId, the transcoding-job-ended/playback-stopped
        // handlers, and SweepExpired), since they all funnel through here.
        _diagnosticsStore?.Remove(id);

        // PR115a: evicts whatever authoritative v2 record was retained for this session, if any -
        // same removal paths as the diagnostics store above.
        _v2PlanStore.Remove(id);

        // PR115c: evicts whatever live-wiring outcome was retained for this session, if any - same
        // removal paths as the two stores above.
        _liveWiringDiagnosticsStore.Remove(id);

        // A Live TV session holds one reference on its tuner stream; every removal path releases it
        // (the close itself runs outside this lock).
        _liveStreams?.Release(id);

        return true;
    }

    private void OnTranscodingJobEnded(object? sender, TranscodingJob job)
    {
        if (string.IsNullOrEmpty(job.PlaySessionId))
        {
            return;
        }

        // PR115d: recorded BEFORE the reap below, which - when it does reap - evicts (among the
        // other two per-session stores) _liveWiringDiagnosticsStore, the ServedByV2 read this needs.
        // Same ordering constraint RecordLifecycleEvent's callers already respect. Issue #71: the
        // reap no longer happens for a client-owned session, so this method no longer gets its
        // idempotency from the eviction - it claims a token in _startOutcomeRecordedPlaySessionIds
        // instead. See that field.
        RecordTranscodeStartFailureIfNeverStarted(job.PlaySessionId);

        // Issue #71: logged BEFORE the removal below, because the removal is exactly what this line
        // exists to attribute. Correlated says whether this ffmpeg job's play session id currently
        // addresses a tracked v2 session at all - when it does, the removal that follows is the
        // ffmpeg job's end being treated as playback's end, which is the defect under investigation.
        PlaybackSessionId? correlatedId;
        lock (_lock)
        {
            correlatedId = _byPlaySessionId.TryGetValue(job.PlaySessionId, out var found) ? found : null;
        }

        _logger.LogInformation(
            "Transcoding job ended for play session {PlaySessionId} (job {JobId}, correlated {Correlated}, session {SessionId}).",
            job.PlaySessionId,
            job.Id,
            correlatedId is not null,
            correlatedId);

        ReapByPlaySessionId(job.PlaySessionId, PlaybackSessionRemovalReason.TranscodingJobEnded);
    }

    /// <summary>
    /// PR113b: records a real "ffmpeg launched" timeline event for the session tied to this job's
    /// play session id, if any is currently tracked. A no-op (not an error) when no tracked session
    /// matches - the job may belong to a live stream or a request this manager never planned.
    /// </summary>
    private void OnTranscodingJobStarted(object? sender, TranscodingJob job)
    {
        // PR115d: records the transcode-start SUCCESS here, at Started time, not at Ended - see
        // RecordTranscodeStartSuccessIfV2Served's remarks for why doing it here (rather than
        // inferring "it must have started" from Ended) avoids a bias toward over-counting failures
        // when a session is evicted (PlaybackStopped, SweepExpired) before its job's Ended event
        // arrives.
        if (!string.IsNullOrEmpty(job.PlaySessionId))
        {
            RecordTranscodeStartSuccessIfV2Served(job.PlaySessionId);
        }

        RecordLifecycleEvent(job.PlaySessionId, "FfmpegStarted");
    }

    /// <summary>
    /// PR113b: records a real "playback started" timeline event for the session tied to this
    /// report's play session id, if any is currently tracked.
    /// </summary>
    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) =>
        RecordLifecycleEvent(e.PlaySessionId, "PlaybackStarted");

    /// <summary>
    /// #413: a revoked device's sessions end with its token, the way an explicit
    /// <c>DELETE /Playback/Sessions/{id}</c> ends one: the session and its retained plan are
    /// removed, then its owner's jobs under its play session are stopped and their output removed.
    /// Its capabilities were already revoked by device. Only sessions planned for that device and
    /// that user: a session of the same user on another device plays on.
    /// </summary>
    private void OnDeviceLoggedOut(object? sender, DeviceLoggedOutEventArgs e)
    {
        if (string.IsNullOrEmpty(e.DeviceId))
        {
            return;
        }

        List<PlaybackSession> ended;
        lock (_lock)
        {
            ended = _sessions.Values
                .Where(s => s.Request?.Options is { } options
                    && options.UserId.Equals(e.UserId)
                    && string.Equals(options.DeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var session in ended)
            {
                RemoveNoLock(session.Id, PlaybackSessionRemovalReason.DeviceLoggedOut);
            }
        }

        foreach (var session in ended)
        {
            _logger.LogInformation(
                "Playback session {SessionId} ended: its device's token was revoked.",
                session.Id.Value.ToString("N"));
            if (!string.IsNullOrEmpty(session.PlaySessionId))
            {
                var owner = e.UserId;
                _ = StopJobsOfRevokedSessionAsync(session.PlaySessionId, owner);
            }
        }
    }

    private async Task StopJobsOfRevokedSessionAsync(string playSessionId, Guid owner)
    {
        try
        {
            await _transcodeManager.StopTranscodingJobs(playSessionId, job => owner.Equals(job.UserId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the jobs of a revoked device's play session {PlaySessionId} failed.", playSessionId);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PlaySessionId))
        {
            // PR113b: recorded before eviction, for parity with FfmpegStarted/PlaybackStarted -
            // but RemoveNoLock (reached via the reap below, when it reaps) evicts every retained
            // PlaybackLifecycleEvent for this session, this one included, immediately afterward.
            // For a LEGACY-tracked session the admin diagnostics endpoint can therefore never
            // actually observe a "PlaybackStopped" entry: by the time it stops, RemoveNoLock has
            // already deleted the session itself, so PlaybackSessionManager.Get(id) - and with it
            // GetPlaybackSession - returns 404 regardless of what the event store retains.
            // Issue #71: a CLIENT-OWNED session is no longer removed here, so for those the entry
            // does survive and the endpoint can finally show it - a side benefit, not the point.
            RecordLifecycleEvent(e.PlaySessionId, "PlaybackStopped");

            // Issue #71: same before-the-removal placement and same purpose as the transcoding-job
            // line above - this is the OTHER PlaySessionId-keyed reap, and an incident timeline has
            // to be able to tell the two apart.
            PlaybackSessionId? correlatedId;
            lock (_lock)
            {
                correlatedId = _byPlaySessionId.TryGetValue(e.PlaySessionId, out var found) ? found : null;
            }

            _logger.LogInformation(
                "Playback stopped reported for play session {PlaySessionId} (correlated {Correlated}, session {SessionId}).",
                e.PlaySessionId,
                correlatedId is not null,
                correlatedId);

            ReapByPlaySessionId(e.PlaySessionId, PlaybackSessionRemovalReason.PlaybackStopped);
        }
    }

    /// <summary>
    /// Correlates <paramref name="playSessionId"/> to a currently tracked session id and, if found,
    /// records <paramref name="stage"/> as observed right now. Silently does nothing for an unknown
    /// or null/empty play session id - a lifecycle event that cannot be correlated is simply
    /// dropped, never thrown, matching every other diagnostics-retention failure mode in this class.
    /// </summary>
    private void RecordLifecycleEvent(string? playSessionId, string stage)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        PlaybackSessionId id;
        lock (_lock)
        {
            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }
        }

        // Issue #42: the request id is stamped per EVENT, alongside (never instead of) the
        // session-scoped grouping key `id`. Usually null here — these three signals arrive from
        // ffmpeg and session callbacks, not from a request — and null is the honest answer.
        _diagnosticsStore?.RecordEvent(
            id,
            new PlaybackLifecycleEvent(stage, DateTimeOffset.UtcNow, _requestCorrelation.CurrentRequestId));
    }

    /// <summary>
    /// PR115d: called from <see cref="OnTranscodingJobStarted"/> - correlates
    /// <paramref name="playSessionId"/> to a currently tracked session and, only when that session
    /// was actually served by v2 (per <see cref="_liveWiringDiagnosticsStore"/>), records a
    /// SUCCESSFUL ffmpeg transcode start into <see cref="_operationalMetrics"/> immediately.
    /// </summary>
    /// <remarks>
    /// Deliberately recorded here, at Started time, rather than inferred at Ended time from
    /// "<see cref="_startedPlaySessionIds"/> contains this id". A session can be evicted by a path
    /// unrelated to this transcoding job - <see cref="OnPlaybackStopped"/> (the ordinary "user
    /// stopped playback" signal) or <see cref="SweepExpired"/>'s TTL backstop - strictly BETWEEN this
    /// job's Started and Ended events. Recording the success at Ended time would then find no
    /// correlatable session (evicted) and silently drop it, while a job that never starts at all
    /// produces no playback and therefore no <see cref="OnPlaybackStopped"/> signal, so ITS failure
    /// (recorded by <see cref="RecordTranscodeStartFailureIfNeverStarted"/>) is essentially never
    /// dropped the same way. Recording successes only at Ended would therefore have biased the
    /// observed rate upward (successes silently dropped more often than failures) - exactly the
    /// wrong direction for a rate a stop-threshold guard reads to decide whether to force legacy for
    /// the whole cohort. Recording the success here, while the session is still guaranteed tracked
    /// (this is the very job whose launch <see cref="ITranscodeManager.TranscodingJobStarted"/> is
    /// reporting), removes that race entirely.
    /// </remarks>
    /// <param name="playSessionId">The play session id the started job belongs to.</param>
    private void RecordTranscodeStartSuccessIfV2Served(string playSessionId)
    {
        PlaybackSessionId id;
        lock (_lock)
        {
            // Marked unconditionally (not gated on correlation/ServedByV2) - RecordTranscodeStartFailureIfNeverStarted
            // needs to know "did Started ever fire for this play session id" regardless of whether
            // this manager can still correlate it to a v2-served session by then.
            _startedPlaySessionIds.Add(playSessionId);

            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }

            // Issue #71: at most one start outcome per session - see
            // _startOutcomeRecordedPlaySessionIds. Claimed here regardless of the ServedByV2 check
            // below, exactly as the pre-#71 eviction consumed the opportunity regardless of it.
            if (!_startOutcomeRecordedPlaySessionIds.Add(playSessionId))
            {
                return;
            }
        }

        if (_liveWiringDiagnosticsStore.TryGet(id, out var outcome) && outcome is not null && outcome.ServedByV2)
        {
            _operationalMetrics.RecordTranscodeStart(failed: false);
        }
    }

    /// <summary>
    /// PR115d: called from <see cref="OnTranscodingJobEnded"/>, BEFORE the eviction that follows it -
    /// records a FAILED ffmpeg transcode start into <see cref="_operationalMetrics"/> when this job
    /// ended without <see cref="RecordTranscodeStartSuccessIfV2Served"/> ever having observed a
    /// matching <see cref="ITranscodeManager.TranscodingJobStarted"/> for this play session id (i.e.
    /// the successful-start case already fully handled itself, at Started time - see that method's
    /// remarks). Silently does nothing for an unknown play session id or a legacy-served session, the
    /// same "correlate or silently no-op" discipline <see cref="RecordLifecycleEvent"/> already
    /// follows. Idempotent against <see cref="ITranscodeManager.TranscodingJobEnded"/>'s documented
    /// "may be raised more than once for the same job" contract - but, since issue #71, NOT because
    /// the caller evicts the session: it no longer does so for a client-owned one. Idempotency now
    /// comes from <see cref="_startOutcomeRecordedPlaySessionIds"/>, which also caps the outcomes a
    /// single session can contribute when a seek kills one job and starts another under the same
    /// play session id.
    /// </summary>
    /// <param name="playSessionId">The play session id the ended job belongs to.</param>
    private void RecordTranscodeStartFailureIfNeverStarted(string playSessionId)
    {
        PlaybackSessionId id;
        lock (_lock)
        {
            if (_startedPlaySessionIds.Remove(playSessionId))
            {
                // It DID start (Started fired first) - RecordTranscodeStartSuccessIfV2Served already
                // recorded this outcome; nothing left to do here but stop tracking the id.
                return;
            }

            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }

            // Issue #71: at most one start outcome per session - see the matching claim in
            // RecordTranscodeStartSuccessIfV2Served.
            if (!_startOutcomeRecordedPlaySessionIds.Add(playSessionId))
            {
                return;
            }
        }

        if (_liveWiringDiagnosticsStore.TryGet(id, out var outcome) && outcome is not null && outcome.ServedByV2)
        {
            _operationalMetrics.RecordTranscodeStart(failed: true);
        }
    }

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
