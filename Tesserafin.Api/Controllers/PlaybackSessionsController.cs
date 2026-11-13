using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Api.Playback;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The client-facing playback session protocol (docs/pr92-design-playback-api-and-diagnostics.md
/// §2-3): create, fully replace, and end a session. Responses are the stable, versioned
/// <see cref="PlaybackSessionResponse"/> (PR91 decision vocabulary), never the internal
/// <see cref="PlaybackSession"/> record. The admin-only listing/diagnostic surface this controller
/// used to also expose has moved to <see cref="PlaybackDiagnosticsSessionsController"/> — it never
/// shared this controller's public or authorization scope, only its route. Since PR115a, responses
/// reflect the v2 decision (<c>DecisionVersion</c> = the real engine version) for sessions the
/// canary made v2-authoritative, and the legacy projection otherwise.
/// </summary>
[Route("Playback/Sessions")]
[Authorize]
[Tags("Playback")]
public class PlaybackSessionsController : BaseTesserafinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IItemAccessService _itemAccessService;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IPlaybackLiveStreamResolver _liveStreamResolver;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IShadowDiagnosticsStore _shadowDiagnosticsStore;
    private readonly ILogger<PlaybackSessionsController> _logger;
    private readonly ITranscodeManager? _transcodeManager;
    private readonly IPlaybackCredentialService? _credentialService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface: the item as the user may see it.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="v2PlanStore">Instance of the <see cref="IV2PlanStore"/> interface (PR115a) — looked up to see whether v2 is authoritative for a session's response.</param>
    /// <param name="liveStreamResolver">
    /// PR117 (docs/pr116d-url-contract-design.md §3.3): the shared live-wiring decision, re-run at
    /// call time on <see cref="GetPlaybackSessionStream"/> - same component, same kill switch/
    /// stop-threshold guard/SourceId verification/Dolby Vision exclusion, that
    /// <c>MediaInfoHelper.SetDeviceSpecificData</c> already consults for the legacy path.
    /// </param>
    /// <param name="liveWiringDiagnosticsStore">
    /// PR117: read back immediately after <paramref name="liveStreamResolver"/> resolves, so the
    /// descriptor's <c>FallbackReason</c> reflects exactly the decision that produced the URL - the
    /// same store the admin diagnostics detail route already surfaces (PR115c).
    /// </param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface - resolves the external subtitle delivery URL for the descriptor.</param>
    /// <param name="logger">
    /// Issue #43: carries the client-supplied <c>PlaybackAttemptId</c> into the structured log scope
    /// for the duration of the action, so every line emitted while serving a request of one attempt
    /// is joinable to the other requests of that same attempt. Trailing and optional so existing
    /// test constructions keep compiling.
    /// </param>
    /// <param name="shadowDiagnosticsStore">
    /// Issue #75: the SAME store PR113 already uses to correlate a shadow run with its session - not
    /// a new one. This controller is the only place that holds both halves issue #75's mapping
    /// comparison needs (the declared <c>ClientCapabilities</c> off the request body, and the
    /// request's <c>Content-Length</c>), so it opens the ambient capture scope carrying them; the
    /// scope PlaybackSessionManager opens around planning inherits them. Trailing and optional,
    /// defaulting to the no-op store, so every existing test construction keeps compiling and simply
    /// produces no contract-mapping diagnostic.
    /// </param>
    /// <param name="transcodeManager">#301: ends the session's own jobs when the session is deleted.</param>
    /// <param name="credentialService">#301: revokes the session's capabilities when the session is deleted.</param>
    public PlaybackSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IItemAccessService itemAccessService,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IV2PlanStore v2PlanStore,
        IPlaybackLiveStreamResolver liveStreamResolver,
        IPlaybackLiveWiringDiagnosticsStore liveWiringDiagnosticsStore,
        IMediaEncoder mediaEncoder,
        ILogger<PlaybackSessionsController>? logger = null,
        IShadowDiagnosticsStore? shadowDiagnosticsStore = null,
        ITranscodeManager? transcodeManager = null,
        IPlaybackCredentialService? credentialService = null)
    {
        _transcodeManager = transcodeManager;
        _credentialService = credentialService;
        _logger = logger ?? NullLogger<PlaybackSessionsController>.Instance;
        _shadowDiagnosticsStore = shadowDiagnosticsStore ?? NoOpShadowDiagnosticsStore.Instance;
        _playbackSessionManager = playbackSessionManager;
        _itemAccessService = itemAccessService;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _v2PlanStore = v2PlanStore;
        _liveStreamResolver = liveStreamResolver;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Creates (or, if <see cref="CreatePlaybackSessionRequest.PlaySessionId"/> matches an
    /// existing session, replaces) a playback session.
    /// </summary>
    /// <param name="request">The session to plan.</param>
    /// <response code="200">Session created.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="404">Item not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The created session's stable decision projection.</returns>
    [HttpPost]
    [ServiceFilter(typeof(PlaybackContractScanFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> CreatePlaybackSession([FromBody] CreatePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        var (kind, options, isLocalVideo) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        // Issue #75: the ambient scope carrying the two request-scoped facts the shadow run cannot
        // recover on its own. Content-Length is read from the HEADER only - request buffering stays
        // off, and an absent header stays null rather than becoming a lying 0. Opened around the
        // planning call and closed immediately after: nothing derived from these inputs is retained
        // beyond the counts and presence flags of the resulting ContractMappingDiagnostic. When
        // shadow mode is off (the default) the shadow run never executes and this scope costs one
        // AsyncLocal write and produces nothing.
        PlaybackSession? session;
        using (BeginLocalVideoScope(isLocalVideo, request))
        using (_shadowDiagnosticsStore.BeginCapture(new ShadowCaptureInputs(request.Capabilities, HttpContext.Request.ContentLength, ReadCapturedScan())))
        {
            session = _playbackSessionManager.Create(
                new PlaybackSessionRequest(kind, options),
                request.PlaySessionId,
                request.PlaybackAttemptId);
        }

        if (session is null)
        {
            return NoViablePlan();
        }

        // A POST naming a play session that already has one replaces that session in place. When
        // the new plan is the engine's, nothing of an earlier plan's job may be served for it.
        if (session.Plan.StreamInfo is null)
        {
            await StopJobsOfAnEnginePlan(session.PlaySessionId, options.UserId).ConfigureAwait(false);
        }

        // Issue #43: the lifecycle line a browser e2e joins against X-Request-Id (the ambient
        // RequestId scope of #42) and the attempt scope opened above. One line per transition;
        // the PUT and DELETE transitions below each have their own.
        _logger.LogInformation(
            "Playback session {SessionId} created (method {PlayMethod}, attempt {PlaybackAttemptId}).",
            session.Id,
            session.Plan.PlayMethod,
            session.PlaybackAttemptId);

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Fully re-plans an existing session with a complete new set of options. Decision v1 (PR92
    /// §3): this replaces the misnamed <c>PATCH</c> the point-1 protocol shipped with, which
    /// already required a complete body — <c>PUT</c> states that honestly.
    /// </summary>
    /// <param name="id">The session to replace.</param>
    /// <param name="request">The complete new options to plan.</param>
    /// <response code="200">Session updated.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Item or session not found.</response>
    /// <response code="422">The session exists but re-planning it with these options produces no viable plan.</response>
    /// <returns>The updated session's stable decision projection.</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> ReplacePlaybackSession([FromRoute] PlaybackSessionId id, [FromBody] ReplacePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        // PR118: PUT used to have no ownership check at all - a pre-existing gap (§4.2 of the PR117
        // design doc called it out explicitly for this endpoint) - any authenticated caller who knew
        // an id could re-plan someone else's session. Checked against the EXISTING session (not the
        // new request body's own UserId, which a caller fully controls) before doing any planning
        // work, same owner-or-admin semantics as GetPlaybackSessionStream.
        var existingSession = _playbackSessionManager.Get(id);
        if (existingSession is null)
        {
            return NotFound();
        }

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(existingSession);
        if (authorization is not null)
        {
            return authorization;
        }

        var (kind, options, isLocalVideo) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        // PR #38 (docs/design-playback-v2-lifecycle.md, decided in 2cf777d2): the session was just
        // proven to exist above, so a null here can only mean re-planning produced NO viable plan -
        // a 404 said "unknown id" for what is really an unsatisfiable request, and left the client's
        // track-change path unable to tell the two apart. 404 stays reserved for the unknown id.
        //
        // Issue #43: the SAME attempt id the POST carried, when the client is re-planning inside
        // one attempt. That identity across two different HTTP requests is exactly what a request
        // id could never provide, and is why this is a separate field.
        // Issue #75: same ambient scope as the POST - a PUT re-plans with a complete new body, so it
        // carries its own declared capabilities and its own Content-Length.
        PlaybackSession? session;
        using (BeginLocalVideoScope(isLocalVideo, request))
        using (_shadowDiagnosticsStore.BeginCapture(new ShadowCaptureInputs(request.Capabilities, HttpContext.Request.ContentLength, ReadCapturedScan())))
        {
            session = _playbackSessionManager.Patch(id, new PlaybackSessionRequest(kind, options), request.PlaybackAttemptId);
        }

        if (session is null)
        {
            return NoViablePlan();
        }

        // The plan just changed and the play session did not: see StopJobsOfAnEnginePlan. Whether
        // the previous plan or the new one is the engine's, the job writing the previous plan's
        // output must be gone before the new plan's playlist is read.
        if (existingSession.Plan.StreamInfo is null || session.Plan.StreamInfo is null)
        {
            await StopJobsOfAnEnginePlan(existingSession.PlaySessionId, existingSession.Request?.Options.UserId).ConfigureAwait(false);
        }

        // Issue #43: the re-plan transition, with the decision bits that actually change on a track/
        // constraint change. The attempt id is read off the PATCHED session, which preserves the one
        // recorded at creation when this PUT omitted the field ("not sent" is not "forget it").
        _logger.LogInformation(
            "Playback session {SessionId} replaced (method {OldPlayMethod} -> {NewPlayMethod}, attempt {PlaybackAttemptId}).",
            session.Id,
            existingSession.Plan.PlayMethod,
            session.Plan.PlayMethod,
            session.PlaybackAttemptId);

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Removes a playback session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <response code="204">Session removed.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeletePlaybackSession([FromRoute] PlaybackSessionId id)
    {
        // PR118: same pre-existing gap as PUT (§4.2) - any authenticated caller who knew an id could
        // end someone else's session. Same owner-or-admin semantics as GetPlaybackSessionStream.
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            // Issue #43: no session means no stored attempt id to scope with - inventing one would
            // be worse than none. The line still ties the 404 to the request via the ambient
            // RequestId scope (#42).
            // #203: same projection as the deleted line below - same bytes, visible to the analysis.
            _logger.LogInformation("Playback session {SessionId} delete requested, but the session was already gone.", id.Value.ToString("N"));
            return NotFound();
        }

        // Issue #43: DELETE carries no body, so the attempt id is recovered from the stored session -
        // the same value the POST/PUT of this attempt recorded - and scopes everything after the
        // existence check, exactly like the request-supplied scope on POST/PUT.
        using var attemptScope = BeginAttemptScope(session.PlaybackAttemptId);

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

        if (!_playbackSessionManager.Delete(id))
        {
            // Raced away between the Get above and the Delete (PlaybackStopped, TTL sweep, a
            // concurrent DELETE) - same observable outcome as the not-found path above.
            // #203: same projection as the deleted line below - same bytes, visible to the analysis.
            _logger.LogInformation("Playback session {SessionId} delete requested, but the session was already gone.", id.Value.ToString("N"));
            return NotFound();
        }

        // #301: deleting the session is the client's stop, so it has to end what the session
        // started. Only its owner's jobs: a play session id is the client's to send, and another
        // user's job, and its capabilities, under the same one survive. Old capability links die with them.
        if (!string.IsNullOrEmpty(session.PlaySessionId))
        {
            var owner = session.Request?.Options.UserId;
            if (owner is { } ownerId)
            {
                _credentialService?.RevokePlaySession(session.PlaySessionId, ownerId, null);
                if (_transcodeManager is not null)
                {
                    await _transcodeManager.StopTranscodingJobs(session.PlaySessionId, job => ownerId.Equals(job.UserId)).ConfigureAwait(false);
                }
            }
        }

        // #203: `id.Value.ToString("N")` is the body of PlaybackSessionId.ToString() written out at
        // the logging boundary, so the bytes are the same 32 hexadecimal characters this statement
        // has always written - PlaybackSessionIdProjectionTests holds that down against both
        // shipped formatters. What changes is only what is visible: the route-bound value reached
        // this sink as a whole struct, which is a two-node flow with nothing in between to model.
        // Projecting the Guid out makes the safe rendering an ordinary expression instead of an
        // override the analysis has to take on trust. The value business logic uses is untouched:
        // Get, Delete and the ownership check above all still take `id` itself.
        _logger.LogInformation(
            "Playback session {SessionId} deleted (attempt {PlaybackAttemptId}).",
            id.Value.ToString("N"),
            session.PlaybackAttemptId.ToSingleLogLine());
        return NoContent();
    }

    /// <summary>
    /// PR117 (docs/pr116d-url-contract-design.md §2): resolves the executable URL for a session
    /// already planned via <see cref="CreatePlaybackSession"/>/<see cref="ReplacePlaybackSession"/>.
    /// A read, not a decision (§2.2): re-projects <c>session.Plan</c>/<c>session.Request</c> that
    /// already exist, re-evaluating the kill switch/stop-threshold guard/plan resolution at THIS
    /// call (§3.1) - the same live-wiring decision the legacy <c>PlaybackInfo</c> path makes on
    /// every request, never cached from the original <c>POST</c>/<c>PUT</c>.
    /// </summary>
    /// <param name="id">The session to resolve a stream URL for.</param>
    /// <param name="startTimeTicks">
    /// The position, in ticks, playback should start from - a property of the moment a client asks
    /// to read, not of the session's own planning decision (§2.2, option (i)).
    /// </param>
    /// <response code="200">Descriptor resolved.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <response code="409">The session has no <c>PlaySessionId</c> - re-request via <see cref="ReplacePlaybackSession"/> supplying one.</response>
    /// <response code="422">The session has no plannable stream - nothing can be served for it.</response>
    /// <returns>The resolved stream descriptor.</returns>
    [HttpGet("{id}/Stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult<PlaybackSessionStreamDescriptor> GetPlaybackSessionStream([FromRoute] PlaybackSessionId id, [FromQuery] long startTimeTicks = 0)
    {
        if (startTimeTicks < 0)
        {
            return BadRequest("startTimeTicks must be non-negative.");
        }

        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        // Issue #43: like DELETE, this request carries no attempt id of its own - it is recovered
        // from the stored session, so every line emitted while resolving the URL (the live-wiring
        // decision below logs its serve/fallback outcome) is joinable to the attempt that planned
        // the session. A missing session (the 404 above) has none to offer, so no scope there.
        using var attemptScope = BeginAttemptScope(session.PlaybackAttemptId);

        // §4.2 (mandatory, new on this endpoint): a URL carries the caller's own access token
        // (StreamInfo.ToUrl's &ApiKey=) - this endpoint must never hand that token to anyone but the
        // session's own owner or an administrator. PR118: the same check now also guards
        // ReplacePlaybackSession/DeletePlaybackSession - see EnsureCallerOwnsSessionOrIsAdmin.
        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

        // §2.3 (mandatory): a session without a PlaySessionId cannot correlate a served URL to the
        // transcoding job lifecycle - emitting one anyway would break that correlation silently.
        if (string.IsNullOrEmpty(session.PlaySessionId))
        {
            return Conflict("Session has no PlaySessionId - PUT a replacement request supplying one before requesting a stream URL.");
        }

        // Consolidation step 1: a film the engine alone decided - a direct play, or a remux this
        // server executes as a stream copy - has no legacy StreamInfo at all. Its descriptor is
        // minted from the retained execution plan and names the session's own file or HLS route;
        // nothing below (kill switch, resolver, StreamInfo.ToUrl) applies.
        if (session.Plan.StreamInfo is null
            && _v2PlanStore.TryGet(session.Id, out var engineDecided)
            && engineDecided is { ExecutionPlan: { } enginePlan }
            && LocalVideoDescriptorMapper.CanMap(enginePlan)
            && session.Request?.Options is { } engineOptions)
        {
            if (string.IsNullOrEmpty(User.GetToken()))
            {
                return Forbid();
            }

            return Ok(LocalVideoDescriptorMapper.Map(session.Id, session.PlaySessionId, engineOptions.ItemId, enginePlan, engineDecided.Decision.EngineVersion));
        }

        var options = session.Request?.Options;
        var legacyStreamInfo = session.Plan.StreamInfo;
        var mediaSource = legacyStreamInfo?.MediaSource;
        // PR #38: this used to be a second 409 on the same operation, which OpenAPI cannot express -
        // one `responses` entry per status code per operation means two 409s discriminated only by
        // their body are invisible to every generated client. The two conditions are genuinely
        // different: the 409 above is REPAIRABLE by the client (PUT a request carrying a
        // PlaySessionId), this one is structurally unservable, so it is 422.
        if (options is null || legacyStreamInfo is null || mediaSource is null)
        {
            return UnprocessableEntity("Session has no plannable stream - this endpoint only serves sessions planned via Playback/Sessions.");
        }

        // PR118: a caller authenticated without a bearer token has no business triggering
        // resolution at all. Checked before any operational effect (resolution, diagnostics,
        // metrics). The token itself is not used: no descriptor carries a credential.
        if (string.IsNullOrEmpty(User.GetToken()))
        {
            return Forbid();
        }

        var resolved = _liveStreamResolver.Resolve(
            session.Id,
            legacyStreamInfo,
            mediaSource,
            options.Profile,
            options.ItemId,
            options.DeviceId,
            session.PlaySessionId,
            startTimeTicks,
            options.AlwaysBurnInSubtitleWhenTranscoding);

        // PR118: the legacy fallback path returns the SAME mutable StreamInfo instance retained in
        // session.Plan.StreamInfo (not a fresh projection, unlike the v2-adapter path) - stamping
        // PlaySessionId/StartPositionTicks directly onto it let two concurrent calls with different
        // startTimeTicks race on each other's writes. WithRequestContext copies per-request first:
        // mirrors MediaInfoHelper.SetDeviceSpecificData's own re-stamp, but never mutates the
        // instance the session itself still holds, on either path (v2 or legacy).
        var resolvedStreamInfo = resolved.WithRequestContext(
            session.PlaySessionId,
            startTimeTicks);

        // §3: FallbackReason/ServedBy must reflect exactly the decision that produced
        // resolvedStreamInfo, not a separately timed lookup - read back the outcome the resolver
        // itself just recorded (PR115c's own store), then only look up the real engine version when
        // that outcome says v2 actually served this request.
        _liveWiringDiagnosticsStore.TryGet(session.Id, out var outcome);
        var servedBy = PlaybackSessionResponse.LegacyDecisionVersion;
        if (outcome is { ServedByV2: true } && _v2PlanStore.TryGet(session.Id, out var v2Record) && v2Record is not null)
        {
            servedBy = v2Record.Decision.EngineVersion;
        }

        // #374: a session v2 decides keeps a legacy plan as its fallback even when that plan serves
        // nothing the client declared. A real fallback (kill switch, stop threshold, unexecutable
        // plan) refuses rather than hand over the file the client said it cannot open.
        if (outcome is not { ServedByV2: true }
            && session.Kind == PlaybackMediaKind.Video
            && PlaybackSessionPlanner.IsUnservableVideoPlan(resolvedStreamInfo))
        {
            return NoViablePlan();
        }

        // Issue #57: a Remux decision (domain PlaybackMethod.Remux, mapped by
        // PlaybackExecutionPlanAdapter to legacy PlayMethod.DirectStream) announced a container the
        // source file does not have - Container=mp4/MimeType=video/mp4 for a Matroska source - and
        // then served that source file BYTE-IDENTICALLY. Cause: StreamInfo.ToUrl appends
        // "&Static=true" for every IsDirectStream stream (DirectStream OR DirectPlay), and
        // VideosController.GetVideoStream answers Static=true with
        // FileStreamResponseHelpers.GetStaticFileResult(state.MediaPath, ...) - the source file
        // verbatim, under the announced container's MIME type. The decision and the descriptor are
        // both correct (the client declared it cannot decode the source container, which is exactly
        // what the ContainerNotSupported reason means); it is the EXECUTION that must actually
        // remux, so the URL must not ask for the static file.
        //
        // Normalizing PlayMethod to Transcode for URL serialization is not a re-decision: it is
        // exactly what the legacy PlaybackInfo path already does at its own URL-minting site
        // (MediaInfoHelper.SetDeviceSpecificData sets `streamInfo.PlayMethod = PlayMethod.Transcode`
        // before calling ToUrl for mediaSource.TranscodingUrl), which is why that path never
        // exhibited this defect. This v2 descriptor endpoint was the one URL-minting site that
        // skipped the normalization.
        //
        // DirectPlay is deliberately NOT normalized: there Static=true is correct - the announced
        // container IS the source's, so serving the file verbatim is the honest answer.
        //
        // Gated on ServedByV2. A legacy plan still reaching here as DirectStream is one the planner
        // kept because the client can open the source as it is; a legacy plan it could not open was
        // re-planned onto a declared output profile (#374, PlaybackSessionPlanner.PlanVideo).
        //
        // Mutating here is safe and deliberately scoped: resolvedStreamInfo is the per-request
        // MemberwiseClone WithRequestContext just produced (see the PR118 note above), never the
        // instance session.Plan.StreamInfo retains, and PlayMethod is a value type. Nothing outside
        // this method observes the change.
        if (outcome is { ServedByV2: true } && resolvedStreamInfo.PlayMethod == PlayMethod.DirectStream)
        {
            resolvedStreamInfo.PlayMethod = PlayMethod.Transcode;
        }

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(resolvedStreamInfo, servedBy, outcome?.FallbackReason, _mediaEncoder);
        return Ok(descriptor);
    }

    /// <summary>
    /// PR118: the owner-or-admin authorization shared by all three verbs that operate on an
    /// existing session (GET Stream, PUT, DELETE) - originally only enforced on GET Stream (PR117),
    /// leaving PUT/DELETE reachable by any authenticated caller who knew the session id (§4.2 of the
    /// PR117 design doc flagged this as a pre-existing, separately tracked gap). Checked against the
    /// session's OWN stored request options, never anything the caller's request body supplies.
    /// </summary>
    /// <param name="session">The session being read, replaced, or deleted.</param>
    /// <returns>
    /// <c>null</c> if the caller may proceed; otherwise the <see cref="ForbidResult"/> to return
    /// as-is.
    /// </returns>
    private ActionResult? EnsureCallerOwnsSessionOrIsAdmin(PlaybackSession session)
    {
        if (User.IsInRole(UserRoles.Administrator))
        {
            return null;
        }

        var options = session.Request?.Options;
        if (options is null || !options.UserId.Equals(User.GetUserId()))
        {
            return Forbid();
        }

        return null;
    }

    /// <summary>
    /// A session the engine alone decided executes from its retained plan, into an output named
    /// after its play session, not after its plan. A job started for an earlier plan under the
    /// same play session (another audio track, or a transcode of the existing chain) would
    /// otherwise be found there and served as the new plan's output. Only the owner's jobs, as
    /// on DELETE (#301); a job of another user under the same client-chosen id is not touched.
    /// </summary>
    private async Task StopJobsOfAnEnginePlan(string? playSessionId, Guid? owner)
    {
        if (_transcodeManager is null || string.IsNullOrEmpty(playSessionId) || owner is not { } ownerId)
        {
            return;
        }

        await _transcodeManager.StopTranscodingJobs(playSessionId, job => ownerId.Equals(job.UserId)).ConfigureAwait(false);
    }

    /// <summary>
    /// #374: the refusal a client can act on when no delivery the request permits exists.
    /// </summary>
    private ObjectResult NoViablePlan() => UnprocessableEntity(new ProblemDetails
    {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = "No playable output",
        Detail = "No permitted delivery matches the declared capabilities. Declare a compatible output profile, or allow transcoding.",
    });

    /// <summary>
    /// Issue #75 slice 75b: reads back the bounded structural scan the
    /// <see cref="PlaybackContractScanFilter"/> stashed (before model binding, behind the shadow
    /// gate) for this request, or <see langword="null"/> when the request was not scanned. The
    /// closed result carries only counts - never a client key or value.
    /// </summary>
    /// <returns>The captured scan, or <see langword="null"/> if none was stashed.</returns>
    private ContractStructuralScan? ReadCapturedScan() =>
        HttpContext.Items.TryGetValue(PlaybackContractScanFilter.ScanResultKey, out var value)
            ? value as ContractStructuralScan
            : null;

    private async Task<(PlaybackMediaKind Kind, MediaOptions Options, bool IsLocalVideo)> ResolveOptions(PlaybackPlanRequestBase request, CancellationToken cancellationToken)
    {
        var userId = RequestHelpers.GetUserId(User, request.UserId);
        var user = _userManager.GetUserById(userId) ?? throw new ResourceNotFoundException();
        var item = _itemAccessService.GetVisibleItemById<BaseItem>(request.ItemId, user) ?? throw new ResourceNotFoundException();

        var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, user, true, true, cancellationToken)
            .ConfigureAwait(false);

        // PR112b: the request now carries ClientCapabilities/PlaybackConstraints (PR91 decision
        // vocabulary), never a raw DeviceProfile - ReverseDlnaAdapter is the TEMPORARY translation
        // back to what the legacy pipeline still consumes (delete alongside it - PR114a).
        var options = new MediaOptions
        {
            MediaSources = mediaSources.ToArray(),
            Context = EncodingContext.Streaming,
            RequireDeclaredDelivery = true,
            ItemId = item.Id,
            UserId = userId,
            Profile = ReverseDlnaAdapter.ToDeviceProfile(request.Capabilities),
            MediaSourceId = request.MediaSourceId,
            // PR115a: the canary cohort key is deterministic on user+device, so v2 authority for a
            // session depends on the requesting device being identifiable here.
            DeviceId = User.GetDeviceId(),
        };
        ReverseDlnaAdapter.ApplyConstraints(options, request.Constraints);

        if (!request.Constraints.AllowTranscoding)
        {
            foreach (var mediaSource in mediaSources)
            {
                mediaSource.SupportsTranscoding = false;
            }
        }

        var kind = item.MediaType == MediaType.Audio ? PlaybackMediaKind.Audio : PlaybackMediaKind.Video;
        return (kind, options, kind == PlaybackMediaKind.Video && item is Movie or Episode);
    }

    /// <summary>
    /// Offers supported local films and episodes to the engine before any legacy planning.
    /// </summary>
    private static IDisposable? BeginLocalVideoScope(bool isLocalVideo, PlaybackPlanRequestBase request)
        => isLocalVideo ? LocalVideoPlanner.BeginRequest(new LocalVideoRequest(request.Capabilities, request.Constraints)) : null;

    /// <summary>
    /// Issue #43: opens a structured log scope carrying the attempt id for the rest of the action,
    /// or returns <c>null</c> when none is available — no scope is better than a scope carrying a
    /// null value. POST/PUT pass the request's own field; GET Stream/DELETE have no body, so they
    /// pass the id recovered from the stored session. Nests inside the per-request scope opened by
    /// <c>RequestCorrelationMiddleware</c> (issue #42), so lines emitted here carry BOTH the attempt
    /// id (same on a retry) and the request id (different on a retry).
    /// </summary>
    private IDisposable? BeginAttemptScope(string? playbackAttemptId)
    {
        if (string.IsNullOrEmpty(playbackAttemptId))
        {
            return null;
        }

        return _logger.BeginScope(new Dictionary<string, object>(1)
        {
            [PlaybackAttemptIdValidator.LogPropertyName] = playbackAttemptId
        });
    }
}
